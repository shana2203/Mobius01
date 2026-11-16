#ifndef PS2_REC_H
#define PS2_REC_H

/* F7 video recording: the picture the player sees (without the F4 menu) and
   the game's sound, written as H.264 + AAC in an MP4 file by Windows Media
   Foundation, on a thread of its own. */

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* start a recording of w x h frames (even sizes); start_ns is the clock the
   video timestamps are on (vk_mono_ns).  0 on success; the file path is
   written to path. */
int  ps2_rec_start(uint32_t w, uint32_t h, int with_audio, uint64_t start_ns,
                   char *path, size_t cap);
/* stop: the file is finished on the recording thread (no wait) */
void ps2_rec_stop(void);
/* at exit: stop and wait until the file is finished */
void ps2_rec_shutdown(void);
int  ps2_rec_active(void);
/* one frame, 4 bytes a pixel (bgr: B,G,R,A in memory, else R,G,B,A) */
void ps2_rec_video(const void *pixels, uint32_t pitch, int bgr, uint64_t t_ns);
/* the mix as the device gets it, 48 kHz stereo s16 (any thread) */
void ps2_rec_audio(const int16_t *pcm, uint32_t frames);
/* for the on-screen indicator: seconds recorded so far (< 0: not
   recording) and the last message ("" if none), shown for a few seconds */
double ps2_rec_seconds(void);
int  ps2_rec_message(char *buf, size_t cap);

#ifdef __cplusplus
}
#endif

#endif
