#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ps2_runtime.h"
#include "ps2_hle.h"
#include "ps2_settings.h"
#include "ps2_rec.h"

int ps2_video_focused(void);

void ps2_spu2_mix(s16 *out, u32 n);
void ps2_spu2_init(void);

#define AUDIO_RATE      48000
#define AUDIO_CHANNELS  2
#define AUDIO_CHUNK     512

static SDL_AudioStream *audio_stream;
static int audio_cushion_bytes;
static u64 audio_bytes;
static int audio_started;
static FILE *wav_fp;
static u32 wav_bytes;
static FILE *stem_fp[3];

static void wav_open(void) {
    const char *path = getenv("PS2_AUDIO_WAV");
    u8 hdr[44];
    if (!path) return;
    wav_fp = fopen(path, "wb");
    if (!wav_fp) { ps2_log("audio: cannot open %s", path); return; }
    memset(hdr, 0, sizeof hdr);
    fwrite(hdr, 1, sizeof hdr, wav_fp);
    ps2_log("audio: tapping the mix to %s", path);
    if (getenv("PS2_AUDIO_STEMS")) {
        const char *names[3] = {"spu2", "vag", "adma"};
        for (int i = 0; i < 3; i++) {
            char name[1200];
            int n = snprintf(name, sizeof name, "%s.%s.s16", path, names[i]);
            if (n > 0 && (size_t)n < sizeof name) stem_fp[i] = fopen(name, "wb");
        }
    }
}

static void wav_put(const void *buf, int len) {
    if (!wav_fp) return;
    fwrite(buf, 1, (size_t)len, wav_fp);
    wav_bytes += (u32)len;
}

static void wav_close(void) {
    u8 h[44];
    u32 v;
    for (int i = 0; i < 3; i++)
        if (stem_fp[i]) { fclose(stem_fp[i]); stem_fp[i] = NULL; }
    if (!wav_fp) return;
    memcpy(h, "RIFF", 4);   v = 36u + wav_bytes;      memcpy(h + 4, &v, 4);
    memcpy(h + 8, "WAVEfmt ", 8);
    v = 16;                 memcpy(h + 16, &v, 4);
    { u16 t = 1;            memcpy(h + 20, &t, 2);
      t = AUDIO_CHANNELS;   memcpy(h + 22, &t, 2); }
    v = AUDIO_RATE;         memcpy(h + 24, &v, 4);
    v = AUDIO_RATE * AUDIO_CHANNELS * 2u; memcpy(h + 28, &v, 4);
    { u16 t = AUDIO_CHANNELS * 2u; memcpy(h + 32, &t, 2);
      t = 16;               memcpy(h + 34, &t, 2); }
    memcpy(h + 36, "data", 4);            memcpy(h + 40, &wav_bytes, 4);
    fseek(wav_fp, 0, SEEK_SET);
    fwrite(h, 1, sizeof h, wav_fp);
    fclose(wav_fp);
    wav_fp = NULL;
}

/* callback timing, reported every 5 s with PS2_AUDIO_STATS=1: a callback
   that comes late or mixes slowly is what the player hears as a dropout */
static struct {
    u64 calls, dry, late, last_ns, window_ns, frames;
    int max_queued;
    double max_gap_ms, max_mix_ms;
} acb;

static void SDLCALL audio_cb(void *userdata, SDL_AudioStream *stream,
                             int additional_amount, int total_amount) {
    s16 buf[AUDIO_CHUNK * AUDIO_CHANNELS];
    static int stats = -1;
    u64 t0 = SDL_GetTicksNS();
    (void)userdata;
    if (stats < 0) stats = getenv("PS2_AUDIO_STATS") != NULL;
    {   /* Keep a cushion queued ahead of the device: SDL asks for exactly the
           period it is about to play, so a callback that is late once (a lock
           held, the system busy) is heard as a gap.  Mixing a little ahead
           costs that much latency and absorbs those hiccups. */
        int queued = SDL_GetAudioStreamQueued(stream);   /* still unplayed */
        int target = audio_cushion_bytes;
        (void)total_amount;
        if (queued < 0) queued = 0;
        if (queued + additional_amount < target)
            additional_amount = target - queued;
        if (stats && queued > acb.max_queued) acb.max_queued = queued;
    }
    if (stats) {
        double gap = acb.last_ns ? (double)(t0 - acb.last_ns) / 1e6 : 0.0;
        acb.calls++;
        if (gap > acb.max_gap_ms) acb.max_gap_ms = gap;
        if (gap > 25.0) acb.late++;
        /* nothing left queued: the device was about to run dry */
        if (SDL_GetAudioStreamQueued(stream) <= 0) acb.dry++;
        acb.frames += (u64)additional_amount / (AUDIO_CHANNELS * sizeof(s16));
        acb.last_ns = t0;
        if (!acb.window_ns) acb.window_ns = t0;
    }
    while (additional_amount > 0) {
        int want_bytes = additional_amount;
        int frames, bytes;
        if (want_bytes > (int)sizeof buf) want_bytes = (int)sizeof buf;
        frames = want_bytes / (int)(AUDIO_CHANNELS * sizeof(s16));
        if (frames <= 0) break;
        ps2_spu2_mix(buf, (u32)frames);
        if (stem_fp[0]) fwrite(buf, 4, (size_t)frames, stem_fp[0]);
        ps2_ac4_vag_mix(buf, (u32)frames);
        if (stem_fp[1]) fwrite(buf, 4, (size_t)frames, stem_fp[1]);
        ps2_ac4_snd_mix(buf, (u32)frames);
        if (stem_fp[2]) fwrite(buf, 4, (size_t)frames, stem_fp[2]);
        ps2_rec_audio(buf, (u32)frames);   /* F7 video: the game at full volume */
        {   /* master volume (F4 > Quick > Audio) and the background mute */
            float g = ps2_cfg.volume;
            if (ps2_cfg.mute_unfocused && !ps2_video_focused()) g = 0.0f;
            if (g < 0.999f) {
                int q = (int)(g * 32768.0f + 0.5f);
                for (int i = 0; i < frames * AUDIO_CHANNELS; i++)
                    buf[i] = (s16)((buf[i] * q) >> 15);
            }
        }
        bytes = frames * (int)(AUDIO_CHANNELS * sizeof(s16));
        SDL_PutAudioStreamData(stream, buf, bytes);
        wav_put(buf, bytes);
        audio_bytes += (u64)bytes;
        additional_amount -= bytes;
    }
    if (stats) {
        u64 t1 = SDL_GetTicksNS();
        double mix = (double)(t1 - t0) / 1e6;
        if (mix > acb.max_mix_ms) acb.max_mix_ms = mix;
        if (t1 - acb.window_ns >= 5000000000ull) {
            ps2_log("audio-cb: %llu calls, %.0f frames a call, max gap %.1f ms, "
                    "%llu late (>25 ms), %llu dry, max mix %.2f ms, max queued %d bytes",
                    (unsigned long long)acb.calls,
                    acb.calls ? (double)acb.frames / (double)acb.calls : 0.0,
                    acb.max_gap_ms, (unsigned long long)acb.late,
                    (unsigned long long)acb.dry, acb.max_mix_ms, acb.max_queued);
            memset(&acb, 0, sizeof acb);
            acb.last_ns = t1;
            acb.window_ns = t1;
        }
    }
}

void ps2_audio_start(void) {
    SDL_AudioSpec spec;
    if (audio_started) return;
    if (getenv("PS2_NO_AUDIO")) {
        ps2_log("audio: disabled by PS2_NO_AUDIO");
        return;
    }
    {   /* The device period: SDL's default on WASAPI is 480 frames (10 ms),
           with no slack at all.  1024 frames (21 ms) plus a cushion of one
           more period keeps playback whole through a late callback, for
           about 40 ms of latency.  PS2_AUDIO_FRAMES overrides (256..8192). */
        const char *e = getenv("PS2_AUDIO_FRAMES");
        long frames = e ? strtol(e, NULL, 0) : 1024;
        char hint[16];
        if (frames < 256) frames = 256;
        if (frames > 8192) frames = 8192;
        snprintf(hint, sizeof hint, "%ld", frames);
        SDL_SetHint(SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES, hint);
        audio_cushion_bytes = (int)(2 * frames) * AUDIO_CHANNELS * (int)sizeof(s16);
    }
    if (!SDL_InitSubSystem(SDL_INIT_AUDIO)) {
        ps2_log("audio: SDL_InitSubSystem(AUDIO): %s", SDL_GetError());
        return;
    }
    ps2_spu2_init();
    spec.format = SDL_AUDIO_S16LE;
    spec.channels = AUDIO_CHANNELS;
    spec.freq = AUDIO_RATE;
    audio_stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
                                             &spec, audio_cb, NULL);
    if (!audio_stream) {
        ps2_log("audio: SDL_OpenAudioDeviceStream: %s", SDL_GetError());
        return;
    }
    wav_open();
    audio_started = 1;
    SDL_ResumeAudioStreamDevice(audio_stream);
    {
        int dev_frames = 0;
        SDL_AudioSpec dev;
        SDL_GetAudioDeviceFormat(SDL_GetAudioStreamDevice(audio_stream), &dev, &dev_frames);
        ps2_log("audio: %u Hz %u-channel s16 device open (device %d Hz, %d frames "
                "a period, %d-byte cushion), SPU2 mixer attached",
                AUDIO_RATE, AUDIO_CHANNELS, dev.freq, dev_frames, audio_cushion_bytes);
    }
}

int ps2_audio_running(void) { return audio_started; }
void ps2_audio_stop(void) {
    if (!audio_started) return;
    SDL_DestroyAudioStream(audio_stream);
    audio_stream = NULL;
    audio_started = 0;
    wav_close();
}

void ps2_audio_report(void) {
    if (!audio_started && !audio_bytes) return;
    ps2_log("audio: %llu bytes drained by the device (%.1f s at %u Hz)",
            (unsigned long long)audio_bytes,
            (double)audio_bytes / (double)(AUDIO_RATE * AUDIO_CHANNELS
                                           * (int)sizeof(s16)),
            AUDIO_RATE);
}
