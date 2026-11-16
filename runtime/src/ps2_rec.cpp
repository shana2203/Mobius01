/* F7 video recording to MP4 (H.264 video, AAC sound) with Windows Media
   Foundation.  Media Foundation is part of Windows 10/11 (the "N" editions
   need the Media Feature Pack), so it is loaded at run time: the game starts
   without it and F7 then only says that recording is not available.

   The renderer hands over finished frames (ps2_video.c reads them back a few
   frames late, without waiting for the GPU) and the audio callback hands over
   the mix; both are queued here and encoded on a thread of its own.  Frames
   are stamped with the time they were drawn, so a slow moment gives a longer
   frame instead of desynchronising the sound. */

#include "ps2_rec.h"

#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

extern "C" void ps2_log(const char *fmt, ...);

#ifdef _WIN32
#include <initguid.h>
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <codecapi.h>

namespace {

typedef HRESULT (WINAPI *PFN_MFStartup)(ULONG, DWORD);
typedef HRESULT (WINAPI *PFN_MFShutdown)(void);
typedef HRESULT (WINAPI *PFN_MFCreateMediaType)(IMFMediaType **);
typedef HRESULT (WINAPI *PFN_MFCreateMemoryBuffer)(DWORD, IMFMediaBuffer **);
typedef HRESULT (WINAPI *PFN_MFCreateSample)(IMFSample **);
typedef HRESULT (WINAPI *PFN_MFCreateAttributes)(IMFAttributes **, UINT32);
typedef HRESULT (WINAPI *PFN_MFCreateSinkWriterFromURL)(LPCWSTR, IMFByteStream *,
                                                        IMFAttributes *, IMFSinkWriter **);
struct Mf {
    bool tried = false, ok = false;
    PFN_MFStartup Startup;
    PFN_MFShutdown Shutdown;
    PFN_MFCreateMediaType CreateMediaType;
    PFN_MFCreateMemoryBuffer CreateMemoryBuffer;
    PFN_MFCreateSample CreateSample;
    PFN_MFCreateAttributes CreateAttributes;
    PFN_MFCreateSinkWriterFromURL CreateSinkWriterFromURL;
} mf;

bool mf_load() {
    if (mf.tried) return mf.ok;
    mf.tried = true;
    HMODULE plat = LoadLibraryW(L"mfplat.dll");
    HMODULE rw = LoadLibraryW(L"mfreadwrite.dll");
    if (!plat || !rw) {
        ps2_log("rec: Media Foundation is not installed (mfplat/mfreadwrite)");
        return false;
    }
#define GET(m, n) (mf.n = (decltype(mf.n))(void *)GetProcAddress(m, "MF" #n))
    GET(plat, Startup); GET(plat, Shutdown); GET(plat, CreateMediaType);
    GET(plat, CreateMemoryBuffer); GET(plat, CreateSample); GET(plat, CreateAttributes);
    GET(rw, CreateSinkWriterFromURL);
#undef GET
    mf.ok = mf.Startup && mf.Shutdown && mf.CreateMediaType && mf.CreateMemoryBuffer
         && mf.CreateSample && mf.CreateAttributes && mf.CreateSinkWriterFromURL;
    if (!mf.ok) ps2_log("rec: Media Foundation entry points missing");
    return mf.ok;
}

template <class T> void release(T *&p) { if (p) { p->Release(); p = nullptr; } }

const int AUDIO_RATE = 48000;
const size_t MAX_QUEUED_FRAMES = 6;

struct Frame {
    std::vector<uint8_t> px;
    uint32_t pitch = 0;
    int bgr = 1;
    uint64_t t_ns = 0;
};

struct Recorder {
    std::mutex lock;
    std::condition_variable cv;
    std::thread thread;
    bool running = false;        /* accepting frames and sound */
    bool stopping = false;       /* finish the file and end the thread */
    std::deque<Frame> queue;
    std::vector<Frame> pool;
    std::vector<int16_t> audio;  /* interleaved stereo waiting to be encoded */
    bool with_audio = false;
    uint32_t w = 0, h = 0;
    uint64_t start_ns = 0, last_t_ns = 0;
    uint64_t frames_in = 0, frames_dropped = 0;
    char path[1024] = {0};
    char message[1200] = {0};
    uint64_t message_at = 0;
} R;

std::atomic<int> g_active{0};
std::atomic<uint64_t> g_last_t{0};

void set_message(const char *fmt, const char *arg) {
    std::lock_guard<std::mutex> g(R.lock);
    snprintf(R.message, sizeof R.message, fmt, arg);
    R.message_at = SDL_GetTicks();
}

/* BT.709 limited range, chroma averaged over 2x2 */
void to_nv12(const Frame &f, uint32_t w, uint32_t h, uint8_t *dst) {
    uint8_t *yp = dst, *uv = dst + (size_t)w * h;
    const int ri = f.bgr ? 2 : 0, bi = f.bgr ? 0 : 2;
    for (uint32_t y = 0; y < h; y += 2) {
        const uint8_t *s0 = f.px.data() + (size_t)y * f.pitch;
        const uint8_t *s1 = s0 + f.pitch;
        uint8_t *y0 = yp + (size_t)y * w, *y1 = y0 + w;
        uint8_t *c = uv + (size_t)(y / 2) * w;
        for (uint32_t x = 0; x < w; x += 2) {
            int r[4], g[4], b[4];
            const uint8_t *p[4] = { s0 + x * 4, s0 + x * 4 + 4, s1 + x * 4, s1 + x * 4 + 4 };
            for (int k = 0; k < 4; k++) { r[k] = p[k][ri]; g[k] = p[k][1]; b[k] = p[k][bi]; }
            y0[x]     = (uint8_t)(16 + ((47 * r[0] + 157 * g[0] + 16 * b[0] + 128) >> 8));
            y0[x + 1] = (uint8_t)(16 + ((47 * r[1] + 157 * g[1] + 16 * b[1] + 128) >> 8));
            y1[x]     = (uint8_t)(16 + ((47 * r[2] + 157 * g[2] + 16 * b[2] + 128) >> 8));
            y1[x + 1] = (uint8_t)(16 + ((47 * r[3] + 157 * g[3] + 16 * b[3] + 128) >> 8));
            int R4 = r[0] + r[1] + r[2] + r[3], G4 = g[0] + g[1] + g[2] + g[3];
            int B4 = b[0] + b[1] + b[2] + b[3];
            int cb = 128 + ((-26 * R4 - 86 * G4 + 112 * B4 + 512) >> 10);
            int cr = 128 + ((112 * R4 - 102 * G4 - 10 * B4 + 512) >> 10);
            c[x]     = (uint8_t)(cb < 16 ? 16 : cb > 240 ? 240 : cb);
            c[x + 1] = (uint8_t)(cr < 16 ? 16 : cr > 240 ? 240 : cr);
        }
    }
}

HRESULT video_types(IMFSinkWriter *wr, DWORD *stream) {
    IMFMediaType *out = nullptr, *in = nullptr;
    HRESULT hr = mf.CreateMediaType(&out);
    UINT32 bitrate = (UINT32)((double)R.w * R.h * 60.0 * 0.12);
    if (bitrate < 4000000u) bitrate = 4000000u;
    if (SUCCEEDED(hr)) hr = out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = out->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(out, MF_MT_FRAME_SIZE, R.w, R.h);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(out, MF_MT_FRAME_RATE, 60, 1);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(out, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = wr->AddStream(out, stream);
    if (SUCCEEDED(hr)) hr = mf.CreateMediaType(&in);
    if (SUCCEEDED(hr)) hr = in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = in->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(in, MF_MT_FRAME_SIZE, R.w, R.h);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(in, MF_MT_FRAME_RATE, 60, 1);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(in, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = wr->SetInputMediaType(*stream, in, nullptr);
    release(out); release(in);
    return hr;
}

HRESULT audio_types(IMFSinkWriter *wr, DWORD *stream) {
    IMFMediaType *out = nullptr, *in = nullptr;
    HRESULT hr = mf.CreateMediaType(&out);
    if (SUCCEEDED(hr)) hr = out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = out->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_RATE);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
    if (SUCCEEDED(hr)) hr = out->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
    if (SUCCEEDED(hr)) hr = wr->AddStream(out, stream);
    if (SUCCEEDED(hr)) hr = mf.CreateMediaType(&in);
    if (SUCCEEDED(hr)) hr = in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = in->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_RATE);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
    if (SUCCEEDED(hr)) hr = in->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AUDIO_RATE * 4);
    if (SUCCEEDED(hr)) hr = wr->SetInputMediaType(*stream, in, nullptr);
    release(out); release(in);
    return hr;
}

IMFSinkWriter *open_writer(bool hardware, DWORD *vs, DWORD *as) {
    wchar_t wpath[1024];
    IMFAttributes *attr = nullptr;
    IMFSinkWriter *wr = nullptr;
    MultiByteToWideChar(CP_UTF8, 0, R.path, -1, wpath, 1024);
    HRESULT hr = mf.CreateAttributes(&attr, 2);
    if (SUCCEEDED(hr)) hr = attr->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware);
    if (SUCCEEDED(hr)) hr = attr->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
    if (SUCCEEDED(hr)) hr = mf.CreateSinkWriterFromURL(wpath, nullptr, attr, &wr);
    if (SUCCEEDED(hr)) hr = video_types(wr, vs);
    if (SUCCEEDED(hr) && R.with_audio) hr = audio_types(wr, as);
    if (SUCCEEDED(hr)) hr = wr->BeginWriting();
    release(attr);
    if (FAILED(hr)) {
        ps2_log("rec: %s encoder setup failed (0x%08lx)", hardware ? "hardware" : "software",
                (unsigned long)hr);
        release(wr);
        DeleteFileW(wpath);
    }
    return wr;
}

HRESULT write_video(IMFSinkWriter *wr, DWORD vs, const Frame &f, LONGLONG t, LONGLONG dur) {
    IMFMediaBuffer *buf = nullptr;
    IMFSample *s = nullptr;
    DWORD size = (DWORD)((size_t)R.w * R.h * 3 / 2);
    BYTE *p = nullptr;
    HRESULT hr = mf.CreateMemoryBuffer(size, &buf);
    if (SUCCEEDED(hr)) hr = buf->Lock(&p, nullptr, nullptr);
    if (SUCCEEDED(hr)) {
        to_nv12(f, R.w, R.h, p);
        buf->Unlock();
        hr = buf->SetCurrentLength(size);
    }
    if (SUCCEEDED(hr)) hr = mf.CreateSample(&s);
    if (SUCCEEDED(hr)) hr = s->AddBuffer(buf);
    if (SUCCEEDED(hr)) hr = s->SetSampleTime(t);
    if (SUCCEEDED(hr)) hr = s->SetSampleDuration(dur);
    if (SUCCEEDED(hr)) hr = wr->WriteSample(vs, s);
    release(s); release(buf);
    return hr;
}

HRESULT write_audio(IMFSinkWriter *wr, DWORD as, const int16_t *pcm, size_t frames,
                    uint64_t at_frame) {
    IMFMediaBuffer *buf = nullptr;
    IMFSample *s = nullptr;
    DWORD size = (DWORD)(frames * 4);
    BYTE *p = nullptr;
    HRESULT hr = mf.CreateMemoryBuffer(size, &buf);
    if (SUCCEEDED(hr)) hr = buf->Lock(&p, nullptr, nullptr);
    if (SUCCEEDED(hr)) {
        memcpy(p, pcm, size);
        buf->Unlock();
        hr = buf->SetCurrentLength(size);
    }
    if (SUCCEEDED(hr)) hr = mf.CreateSample(&s);
    if (SUCCEEDED(hr)) hr = s->AddBuffer(buf);
    if (SUCCEEDED(hr)) hr = s->SetSampleTime((LONGLONG)(at_frame * 10000000ull / AUDIO_RATE));
    if (SUCCEEDED(hr)) hr = s->SetSampleDuration((LONGLONG)(frames * 10000000ull / AUDIO_RATE));
    if (SUCCEEDED(hr)) hr = wr->WriteSample(as, s);
    release(s); release(buf);
    return hr;
}

void encoder_main() {
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD vs = 0, as = 0;
    IMFSinkWriter *wr = nullptr;
    bool started_mf = SUCCEEDED(mf.Startup(MF_VERSION, MFSTARTUP_LITE));
    if (started_mf) {
        wr = open_writer(true, &vs, &as);
        if (!wr) wr = open_writer(false, &vs, &as);
    }
    if (!wr) {
        {
            std::lock_guard<std::mutex> g(R.lock);
            R.running = false;
            R.queue.clear();
        }
        g_active = 0;
        set_message("No se pudo grabar: el codificador de video de Windows no est\xc3\xa1 disponible%s", "");
        if (started_mf) mf.Shutdown();
        if (SUCCEEDED(co)) CoUninitialize();
        return;
    }
    ps2_log("rec: recording %ux%u%s to %s", R.w, R.h, R.with_audio ? " with sound" : "", R.path);

    bool have_prev = false, failed = false;
    Frame prev;
    uint64_t audio_done = 0, frames_out = 0;
    std::vector<int16_t> pcm;
    for (;;) {
        Frame f;
        bool got = false, end = false;
        {
            std::unique_lock<std::mutex> g(R.lock);
            R.cv.wait(g, [] { return !R.queue.empty() || !R.audio.empty() || R.stopping; });
            if (!R.queue.empty()) { f = std::move(R.queue.front()); R.queue.pop_front(); got = true; }
            pcm.swap(R.audio);
            R.audio.clear();
            end = R.stopping && R.queue.empty() && !got;
        }
        if (!pcm.empty() && R.with_audio && !failed) {
            if (FAILED(write_audio(wr, as, pcm.data(), pcm.size() / 2, audio_done))) failed = true;
            audio_done += pcm.size() / 2;
        }
        pcm.clear();
        /* a frame is written when the next one arrives, so it lasts until then */
        if (got || end) {
            if (have_prev && !failed) {
                LONGLONG t = (LONGLONG)((prev.t_ns - R.start_ns) / 100u);
                LONGLONG next = got ? (LONGLONG)((f.t_ns - R.start_ns) / 100u)
                                    : t + 166667;
                if (next <= t) next = t + 1;
                if (FAILED(write_video(wr, vs, prev, t, next - t))) failed = true;
                else frames_out++;
            }
            if (have_prev) {
                std::lock_guard<std::mutex> g(R.lock);
                R.pool.push_back(std::move(prev));
            }
            if (got) { prev = std::move(f); have_prev = true; }
            else have_prev = false;
        }
        if (end) break;
    }
    HRESULT fin = failed ? E_FAIL : wr->Finalize();
    release(wr);
    mf.Shutdown();
    if (SUCCEEDED(co)) CoUninitialize();
    {
        std::lock_guard<std::mutex> g(R.lock);
        ps2_log("rec: %s %s: %llu frames written, %llu dropped, %.1f s of sound",
                SUCCEEDED(fin) ? "saved" : "FAILED", R.path,
                (unsigned long long)frames_out, (unsigned long long)R.frames_dropped,
                (double)audio_done / AUDIO_RATE);
        R.pool.clear();
    }
    const char *name = strrchr(R.path, '/');
    name = name ? name + 1 : R.path;
    if (SUCCEEDED(fin)) set_message("Video guardado: videos/%s", name);
    else set_message("No se pudo terminar el video %s", name);
}

} // namespace

extern "C" int ps2_rec_start(uint32_t w, uint32_t h, int with_audio, uint64_t start_ns,
                             char *path, size_t cap) {
    if (g_active) return -1;
    if (R.thread.joinable()) R.thread.join();     /* the previous file is finished */
    if (!mf_load()) {
        set_message("No se puede grabar: falta Media Foundation de Windows%s", "");
        return -1;
    }
    char dir[1024], stamp[32];
    const char *base = SDL_GetBasePath();
    time_t now = time(nullptr);
    struct tm local;
    if (!base || snprintf(dir, sizeof dir, "%s../videos", base) >= (int)sizeof dir
        || !SDL_CreateDirectory(dir)) {
        ps2_log("rec: could not create the videos folder");
        set_message("No se pudo crear la carpeta de videos%s", "");
        return -1;
    }
    if (localtime_s(&local, &now) != 0 || !strftime(stamp, sizeof stamp, "%Y%m%d-%H%M%S", &local))
        snprintf(stamp, sizeof stamp, "video");
    {
        std::lock_guard<std::mutex> g(R.lock);
        snprintf(R.path, sizeof R.path, "%s/AC4-%s.mp4", dir, stamp);
        R.w = w & ~1u;
        R.h = h & ~1u;
        R.with_audio = with_audio != 0;
        R.start_ns = start_ns;
        R.last_t_ns = 0;
        R.frames_in = R.frames_dropped = 0;
        R.queue.clear();
        R.audio.clear();
        R.stopping = false;
        R.running = true;
        R.message[0] = 0;
        if (path && cap) snprintf(path, cap, "%s", R.path);
    }
    g_last_t = start_ns;
    g_active = 1;
    R.thread = std::thread(encoder_main);
    return 0;
}

extern "C" void ps2_rec_stop(void) {
    if (!g_active) return;
    g_active = 0;
    std::lock_guard<std::mutex> g(R.lock);
    R.running = false;
    R.stopping = true;
    R.cv.notify_all();
}

extern "C" void ps2_rec_shutdown(void) {
    ps2_rec_stop();
    if (R.thread.joinable()) R.thread.join();
}

extern "C" int ps2_rec_active(void) { return g_active.load(); }

extern "C" void ps2_rec_video(const void *pixels, uint32_t pitch, int bgr, uint64_t t_ns) {
    if (!g_active || t_ns < R.start_ns) return;
    std::lock_guard<std::mutex> g(R.lock);
    if (!R.running) return;
    R.frames_in++;
    if (R.queue.size() >= MAX_QUEUED_FRAMES) { R.frames_dropped++; return; }
    Frame f;
    if (!R.pool.empty()) { f = std::move(R.pool.back()); R.pool.pop_back(); }
    f.px.resize((size_t)pitch * R.h);
    memcpy(f.px.data(), pixels, f.px.size());
    f.pitch = pitch;
    f.bgr = bgr;
    f.t_ns = t_ns;
    R.queue.push_back(std::move(f));
    g_last_t = t_ns;
    R.cv.notify_one();
}

extern "C" void ps2_rec_audio(const int16_t *pcm, uint32_t frames) {
    if (!g_active) return;
    std::lock_guard<std::mutex> g(R.lock);
    if (!R.running || !R.with_audio) return;
    if (R.audio.size() > (size_t)AUDIO_RATE * 2 * 4) return;   /* encoder stuck: 4 s */
    R.audio.insert(R.audio.end(), pcm, pcm + (size_t)frames * 2);
    R.cv.notify_one();
}

extern "C" double ps2_rec_seconds(void) {
    if (!g_active) return -1.0;
    uint64_t t = g_last_t.load();
    return t > R.start_ns ? (double)(t - R.start_ns) / 1e9 : 0.0;
}

extern "C" int ps2_rec_message(char *buf, size_t cap) {
    std::lock_guard<std::mutex> g(R.lock);
    if (!R.message[0] || SDL_GetTicks() - R.message_at > 5000) return 0;
    snprintf(buf, cap, "%s", R.message);
    return 1;
}

#else  /* not Windows: no recorder */

extern "C" int ps2_rec_start(uint32_t, uint32_t, int, uint64_t, char *, size_t) { return -1; }
extern "C" void ps2_rec_stop(void) {}
extern "C" void ps2_rec_shutdown(void) {}
extern "C" int ps2_rec_active(void) { return 0; }
extern "C" void ps2_rec_video(const void *, uint32_t, int, uint64_t) {}
extern "C" void ps2_rec_audio(const int16_t *, uint32_t) {}
extern "C" double ps2_rec_seconds(void) { return -1.0; }
extern "C" int ps2_rec_message(char *, size_t) { return 0; }

#endif
