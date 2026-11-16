/* Display vertical-blank tracking for the field clock.

   The guest's field clock runs on its own timer at 59.94 Hz while a FIFO
   swapchain shows frames on the monitor's own vblanks (60.00 Hz on most PC
   displays).  Two free-running clocks make each field land at a different
   point of the display period; whenever it lands near a vblank, frames hop
   between scanouts and the picture judders.

   A helper thread blocks in IDXGIOutput::WaitForVBlank on the monitor that
   shows the window and publishes the time of the latest vblank and a smoothed
   refresh period.  ps2_kernel.c uses them to steer its field deadlines (a
   gentle phase lock, never a blocking wait), so a stalled or missing monitor
   only means the clock falls back to the nominal 59.94 Hz. */

#include "ps2_runtime.h"
#include "ps2_vsync.h"

#include <stdlib.h>
#include <time.h>

#ifdef _WIN32
#define COBJMACROS
#include <windows.h>
#include <dxgi.h>
#include <pthread.h>

static u64 vs_mono_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (u64)ts.tv_sec * 1000000000ull + (u64)ts.tv_nsec;
}

static HWND vs_hwnd;              /* written by the renderer, read here */
static int vs_window_seq;         /* bumped when the window may have moved */
static u64 vs_last_ns;            /* time of the latest accepted vblank */
static u64 vs_period_ns;          /* smoothed refresh period, 0 = unknown */
static int vs_started;
static pthread_t vs_thread;

static IDXGIOutput *pick_output(HMONITOR want) {
    IDXGIFactory1 *f = NULL;
    IDXGIOutput *found = NULL, *first = NULL;
    UINT a, o;
    if (FAILED(CreateDXGIFactory1(&IID_IDXGIFactory1, (void **)&f)) || !f)
        return NULL;
    for (a = 0; !found; a++) {
        IDXGIAdapter1 *ad = NULL;
        if (IDXGIFactory1_EnumAdapters1(f, a, &ad) == DXGI_ERROR_NOT_FOUND) break;
        for (o = 0; !found; o++) {
            IDXGIOutput *out = NULL;
            DXGI_OUTPUT_DESC d;
            if (IDXGIAdapter1_EnumOutputs(ad, o, &out) == DXGI_ERROR_NOT_FOUND) break;
            if (SUCCEEDED(IDXGIOutput_GetDesc(out, &d)) && d.Monitor == want) {
                found = out;
            } else if (!first) {
                first = out;
            } else {
                IDXGIOutput_Release(out);
            }
        }
        IDXGIAdapter1_Release(ad);
    }
    IDXGIFactory1_Release(f);
    if (found) {
        if (first) IDXGIOutput_Release(first);
        return found;
    }
    return first;
}

static HMONITOR current_monitor(void) {
    HWND h = __atomic_load_n(&vs_hwnd, __ATOMIC_ACQUIRE);
    if (h) return MonitorFromWindow(h, MONITOR_DEFAULTTOPRIMARY);
    { POINT p = { 0, 0 }; return MonitorFromPoint(p, MONITOR_DEFAULTTOPRIMARY); }
}

static void *vs_main(void *unused) {
    IDXGIOutput *out = NULL;
    HMONITOR mon = NULL;
    int seen_seq = -1, fails = 0, accepted = 0;
    u64 prev = 0, period = 0;
    (void)unused;
    for (;;) {
        int seq = __atomic_load_n(&vs_window_seq, __ATOMIC_ACQUIRE);
        if (seq != seen_seq || !out) {
            HMONITOR m = current_monitor();
            seen_seq = seq;
            if (m != mon || !out) {
                if (out) IDXGIOutput_Release(out);
                out = pick_output(m);
                mon = m;
                prev = 0; period = 0; accepted = 0;
                __atomic_store_n(&vs_period_ns, 0, __ATOMIC_RELEASE);
                if (!out) {
                    if (fails++ == 0)
                        ps2_log("vsync: no DXGI output for the window's monitor; "
                                "the field clock stays at 59.94 Hz");
                    Sleep(500);
                    continue;
                }
                {   DXGI_OUTPUT_DESC d;
                    if (SUCCEEDED(IDXGIOutput_GetDesc(out, &d)))
                        ps2_log("vsync: tracking vblanks on %ls", d.DeviceName);
                }
            }
        }
        if (FAILED(IDXGIOutput_WaitForVBlank(out))) {
            IDXGIOutput_Release(out);
            out = NULL;
            __atomic_store_n(&vs_period_ns, 0, __ATOMIC_RELEASE);
            Sleep(100);
            continue;
        }
        {
            u64 now = vs_mono_ns();
            u64 d = prev ? now - prev : 0;
            prev = now;
            if (d < 2000000ull) {          /* returned at once: display asleep? */
                Sleep(1);
                continue;
            }
            if (d > 50000000ull) continue; /* below 20 Hz: not a refresh */
            if (!period) {
                period = d;
            } else {
                /* a missed wakeup shows up as two periods: keep only
                   intervals near the estimate (or every one, while the
                   estimate is still settling) */
                u64 lo = period - period / 8u, hi = period + period / 8u;
                if (accepted >= 16 && (d < lo || d > hi)) {
                    /* a whole number of periods is still a true vblank
                       time; anything else is a spurious wakeup */
                    u64 k = (d + period / 2u) / period;
                    s64 off = (s64)d - (s64)(k * period);
                    if (d > hi && off < (s64)(period / 8u) && -off < (s64)(period / 8u))
                        __atomic_store_n(&vs_last_ns, now, __ATOMIC_RELEASE);
                    continue;
                }
                period += ((s64)d - (s64)period) / (accepted < 64 ? 8 : 64);
            }
            accepted++;
            __atomic_store_n(&vs_last_ns, now, __ATOMIC_RELEASE);
            if (accepted >= 16) {
                if (!__atomic_load_n(&vs_period_ns, __ATOMIC_ACQUIRE))
                    ps2_log("vsync: display refresh measured at %.3f Hz",
                            1e9 / (double)period);
                __atomic_store_n(&vs_period_ns, period, __ATOMIC_RELEASE);
            }
        }
    }
    return NULL;
}

void ps2_vsync_set_window(void *hwnd) {
    __atomic_store_n(&vs_hwnd, (HWND)hwnd, __ATOMIC_RELEASE);
    __atomic_fetch_add(&vs_window_seq, 1, __ATOMIC_ACQ_REL);
    if (!vs_started) {
        vs_started = 1;
        if (pthread_create(&vs_thread, NULL, vs_main, NULL) == 0)
            pthread_detach(vs_thread);
        else
            ps2_log("vsync: could not start the vblank thread");
    }
}

void ps2_vsync_window_moved(void) {
    __atomic_fetch_add(&vs_window_seq, 1, __ATOMIC_ACQ_REL);
}

int ps2_vsync_sample(u64 *last_ns, u64 *period_ns) {
    u64 p = __atomic_load_n(&vs_period_ns, __ATOMIC_ACQUIRE);
    u64 t = __atomic_load_n(&vs_last_ns, __ATOMIC_ACQUIRE);
    if (!p || !t) return 0;
    if (vs_mono_ns() - t > 100000000ull) return 0;   /* stale: thread stuck */
    *last_ns = t;
    *period_ns = p;
    return 1;
}

#else

void ps2_vsync_set_window(void *hwnd) { (void)hwnd; }
void ps2_vsync_window_moved(void) {}
int ps2_vsync_sample(u64 *last_ns, u64 *period_ns) {
    (void)last_ns; (void)period_ns;
    return 0;
}

#endif
