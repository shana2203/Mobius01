#ifndef PS2_VSYNC_H
#define PS2_VSYNC_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Display vblank tracking (ps2_vsync.c).  The renderer registers its window
   (a Win32 HWND, or NULL for the primary monitor); the field clock reads the
   latest vblank time and the measured refresh period, both in the
   CLOCK_MONOTONIC nanoseconds used by ps2_kernel.c.  ps2_vsync_sample returns
   0 while nothing reliable is known. */
void ps2_vsync_set_window(void *hwnd);
void ps2_vsync_window_moved(void);
int  ps2_vsync_sample(uint64_t *last_ns, uint64_t *period_ns);

#ifdef __cplusplus
}
#endif

#endif
