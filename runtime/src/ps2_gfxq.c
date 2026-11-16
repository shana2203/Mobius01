#include "ps2_gfxq.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

void ps2_vif_transfer(int which, const ps2_reg128 *q, u32 qwc);
void ps2_vif_write(int which, u32 word);
void ps2_vif_clear_stall(int which);
void ps2_vif_fbrst(int which, u32 val);
void ps2_gif_transfer(const ps2_reg128 *data, u32 qwc);
void ps2_gs_priv_write_now(u32 addr, u64 val);
void ps2_gif_ctrl_write(u32 addr, u32 val);
int  ps2_gs_present_now(void);
void ps2_request_exit(void);

int ps2_gfxq_on;
/* fields presented by the GS so far, in issue order: the field being
   presented right now is ps2_field_presenting (see ps2_field_issue_ns) */
static u32 ps2_field_consumed;
u32 ps2_field_presenting;

enum {
    GQ_WRAP = 0,
    GQ_VIF_QW,
    GQ_GIF_QW,
    GQ_VIF_W,
    GQ_FBRST,
    GQ_VIFCLR,
    GQ_GS_PRIV,
    GQ_MMIO,
    GQ_FIELD
};

static u8 *q_buf;
static size_t q_cap;
static size_t q_head, q_tail;
static int q_busy;
static int q_running;
static pthread_t q_thread;
static _Thread_local int q_is_worker;
static pthread_mutex_t q_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t q_not_empty = PTHREAD_COND_INITIALIZER;
static pthread_cond_t q_not_full  = PTHREAD_COND_INITIALIZER;
static pthread_cond_t q_idle      = PTHREAD_COND_INITIALIZER;

static u64 st_records, st_bytes, st_producer_waits, st_drains, st_drain_waits;
static size_t st_peak_fill;
static int q_fields_pending;
static int q_field_depth = 2;
static u64 st_field_waits;
static u64 st_field_wait_ns;

static u64 gq_now_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (u64)ts.tv_sec * 1000000000ull + (u64)ts.tv_nsec;
}
static u64 st_producer_ns, st_drain_ns;

static void ps2_gsq_speed_line(void);
static u64 speed_present_ns, speed_present_n, speed_work_ns;
static u64 speed_vu_runs, speed_vu_insns, speed_vu_guards;
void ps2_vu1_speed_counters(u64 *, u64 *, u64 *);
int ps2_speed_enabled(void) {
    static int enabled = -1;
    int value = __atomic_load_n(&enabled, __ATOMIC_RELAXED);
    if (value < 0) {
        const char *e = getenv("PS2_SPEED_LOG"); value = e && atoi(e) != 0;
        __atomic_store_n(&enabled, value, __ATOMIC_RELAXED);
    }
    return value;
}
void ps2_speed_sample(u64 fields, u64 dropped) {
    static u64 prev_ns, prev_fields, prev_dropped, prev_present, prev_n, prev_work;
    static u64 prev_field_wait, prev_ring_wait, prev_drain, prev_bytes;
    static u64 prev_vu_runs, prev_vu_insns, prev_vu_guards;
    if (!ps2_speed_enabled()) return;
    u64 now = gq_now_ns();
    if (!prev_ns) {
        ps2_log("speed: diagnostics v1; 5s intervals");
        ps2_log("speed: EE/present/worker ms are interval totals; present_ms is GS display-list handoff, not Vulkan GPU time. See vk-speed for renderer host stages.");
        prev_ns = now; prev_fields = fields; prev_dropped = dropped;
        prev_field_wait=st_field_wait_ns; prev_ring_wait=st_producer_ns;
        prev_drain=st_drain_ns; prev_bytes=st_bytes;
        pthread_mutex_lock(&q_lock);
        prev_present=speed_present_ns; prev_n=speed_present_n; prev_work=speed_work_ns;
        prev_vu_runs=speed_vu_runs; prev_vu_insns=speed_vu_insns; prev_vu_guards=speed_vu_guards;
        pthread_mutex_unlock(&q_lock);
        return;
    }
    if (now - prev_ns < 5000000000ull) return;
    double dt = (now - prev_ns) / 1e9;
    pthread_mutex_lock(&q_lock);
    u64 pn = speed_present_n, pt = speed_present_ns, work = speed_work_ns;
    u64 vr=speed_vu_runs, vi=speed_vu_insns, vg=speed_vu_guards;
    size_t fill = q_head - q_tail;
    int pending = q_fields_pending;
    pthread_mutex_unlock(&q_lock);
    ps2_log("speed: dt=%.2fs fields=%.2f/s dropped=%llu gfxq=%d pending=%d ring_KB=%zu queued_MB=%.2f",
        dt, (fields-prev_fields)/dt, (unsigned long long)(dropped-prev_dropped),
        ps2_gfxq_on, pending, fill >> 10, (st_bytes-prev_bytes)/1048576.0);
    ps2_log("speed: EE wait_ms field=%.2f ring=%.2f drain=%.2f; present=%.2f/s present_ms=%.2f worker_active_ms=%.2f",
        (st_field_wait_ns-prev_field_wait)/1e6, (st_producer_ns-prev_ring_wait)/1e6,
        (st_drain_ns-prev_drain)/1e6, (pn-prev_n)/dt, (pt-prev_present)/1e6, (work-prev_work)/1e6);
    ps2_log("speed: VU1 runs=%.2f/s instructions_M=%.3f guard_hits=%llu",
        (vr-prev_vu_runs)/dt, (vi-prev_vu_insns)/1e6, (unsigned long long)(vg-prev_vu_guards));
    ps2_gsq_speed_line();
    {   u64 ps2_av_latency_take(u64 *max_ns);
        u64 mx, avg = ps2_av_latency_take(&mx);
        if (avg)
            ps2_log("speed: picture latency (field issued -> presented) avg_ms=%.1f max_ms=%.1f",
                    avg / 1e6, mx / 1e6);
    }
    {   /* movies: IPU macroblocks per second (840 per 640x336 frame) */
        u64 ps2_ipu_macroblocks(void), ps2_ipu_pictures_started(void), ps2_ac4_snd_stream_samples(void);
        static u64 prev_mb, prev_pic, prev_snd;
        u64 mb = ps2_ipu_macroblocks(), pic = ps2_ipu_pictures_started();
        u64 snd = ps2_ac4_snd_stream_samples();
        if (mb != prev_mb || snd != prev_snd)
            ps2_log("speed: movie pictures=%.2f/s (IPU macroblocks %.0f/s); movie sound "
                    "%.0f samples/s -> %.2f pictures per sound second",
                    (pic - prev_pic) / dt, (mb - prev_mb) / dt, (snd - prev_snd) / dt,
                    snd != prev_snd ? (double)(pic - prev_pic) * 48000.0 / (double)(snd - prev_snd) : 0.0);
        prev_mb = mb; prev_pic = pic; prev_snd = snd;
    }
    prev_vu_runs=vr; prev_vu_insns=vi; prev_vu_guards=vg;
    prev_ns=now; prev_fields=fields; prev_dropped=dropped; prev_present=pt; prev_n=pn;
    prev_field_wait=st_field_wait_ns; prev_ring_wait=st_producer_ns; prev_drain=st_drain_ns;
    prev_bytes=st_bytes;
    prev_work=work;
}

static size_t q_producer_tail, q_reserved_end;
static u8 *q_reserve(u32 n) {
    size_t off = q_head & (q_cap - 1);
    size_t pad = off + n > q_cap ? q_cap - off : 0;
    size_t need = pad + n;
    if (q_cap - (q_head - q_producer_tail) < need) {
        pthread_mutex_lock(&q_lock);
        q_producer_tail = q_tail;
        while (q_cap - (q_head - q_producer_tail) < need) {
            u64 t0 = gq_now_ns();
            st_producer_waits++;
            pthread_cond_wait(&q_not_full, &q_lock);
            st_producer_ns += gq_now_ns() - t0;
            q_producer_tail = q_tail;
        }
        pthread_mutex_unlock(&q_lock);
    }
    q_reserved_end = q_head + need;
    if (pad) {
        q_buf[off] = GQ_WRAP;
        off = 0;
    }
    return q_buf + off;
}

static void q_commit(u32 n) {
    size_t fill;
    __atomic_store_n(&q_head, q_reserved_end, __ATOMIC_SEQ_CST);
    st_records++;
    st_bytes += n;
    fill = q_reserved_end - __atomic_load_n(&q_tail, __ATOMIC_RELAXED);
    if (fill > st_peak_fill) st_peak_fill = fill;
    if (!__atomic_load_n(&q_busy, __ATOMIC_SEQ_CST)) {
        pthread_mutex_lock(&q_lock);
        pthread_cond_signal(&q_not_empty);
        pthread_mutex_unlock(&q_lock);
    }
}

static inline void put8(u8 *p, u32 off, u8 v)   { p[off] = v; }
static inline void put32(u8 *p, u32 off, u32 v) { memcpy(p + off, &v, 4); }
static inline void put64(u8 *p, u32 off, u64 v) { memcpy(p + off, &v, 8); }
static inline u32  get32(const u8 *p, u32 off)  { u32 v; memcpy(&v, p + off, 4); return v; }
static inline u64  get64(const u8 *p, u32 off)  { u64 v; memcpy(&v, p + off, 8); return v; }

/* ------------------------------------------------------------------------
   Second stage: the GS on a thread of its own.

   With PS2_ASYNC_GFX the whole graphics machine ran on one worker, and in
   heavy scenes that one thread was the limit (VIF1 unpack + VU1 microcode
   about 55 % of its work, GS primitives, texture decoding and Vulkan
   recording the rest).  The worker now stops at the GIF: every GS register
   write, image transfer, privileged register write and field it produces
   goes into a second ring, and a GS thread applies them in the same order.
   Nothing flows back from the GS to VIF1/VU1/GIF -- the GIF tag walker
   stays on the worker, so XGKICK packet lengths are known there -- and the
   only GS-visible state the worker changes is the GIF path number, which
   travels in the ring as well.  PS2_GFX_SPLIT=0 keeps the single worker.
   ------------------------------------------------------------------------ */
void ps2_gs_write_reg(u32 reg, u64 val);
void ps2_gs_hwreg_pair(u64 lo, u64 hi);
void ps2_gs_hwreg_stream(const void *data, u32 qwc);
extern int ps2_gif_path;

int ps2_gsq_split;
int ps2_gsq_path;

enum { G2_WRAP = 0, G2_REG, G2_PAIR, G2_STREAM, G2_PRIV, G2_FIELD, G2_PATH };
#define G2_STREAM_MAX 4096u            /* quadwords per image record */
#define G2_PUBLISH_BYTES (128u << 10)  /* hand work over at least this often */

static u8 *g2_buf;
static size_t g2_cap;
static size_t g2_head;                 /* published end (producer writes) */
static size_t g2_tail;                 /* consumed end (GS thread writes) */
static size_t g2_w, g2_ptail;          /* producer's write cursor and cached tail */
static int g2_busy, g2_running;
static pthread_t g2_thread;
static pthread_cond_t g2_not_empty = PTHREAD_COND_INITIALIZER;
static pthread_cond_t g2_not_full  = PTHREAD_COND_INITIALIZER;
static _Thread_local int q_is_stage_a;
static int g2_sent_path = -1;
static u64 st2_records, st2_bytes, st2_waits, st2_wait_ns, st2_work_ns;
static size_t st2_peak;

static void g2_publish(void) {
    size_t w = g2_w;
    if (w == g2_head) return;
    __atomic_store_n(&g2_head, w, __ATOMIC_SEQ_CST);
    { size_t fill = w - __atomic_load_n(&g2_tail, __ATOMIC_RELAXED);
      if (fill > st2_peak) st2_peak = fill; }
    if (!__atomic_load_n(&g2_busy, __ATOMIC_SEQ_CST)) {
        pthread_mutex_lock(&q_lock);
        pthread_cond_signal(&g2_not_empty);
        pthread_mutex_unlock(&q_lock);
    }
}

/* room for one record of n bytes; the caller fills it before the next call */
static u8 *g2_reserve(u32 n) {
    size_t off, pad, need;
    if (g2_w - g2_head >= G2_PUBLISH_BYTES) g2_publish();
    off = g2_w & (g2_cap - 1);
    pad = off + n > g2_cap ? g2_cap - off : 0;
    need = pad + n;
    if (g2_cap - (g2_w - g2_ptail) < need) {
        g2_ptail = __atomic_load_n(&g2_tail, __ATOMIC_ACQUIRE);
        if (g2_cap - (g2_w - g2_ptail) < need) {
            u64 t0 = gq_now_ns();
            g2_publish();
            pthread_mutex_lock(&q_lock);
            st2_waits++;
            while (g2_running && g2_cap - (g2_w - (g2_ptail = g2_tail)) < need)
                pthread_cond_wait(&g2_not_full, &q_lock);
            pthread_mutex_unlock(&q_lock);
            st2_wait_ns += gq_now_ns() - t0;
        }
    }
    if (pad) { g2_buf[off] = G2_WRAP; g2_w += pad; off = 0; }
    g2_w += n;
    st2_records++;
    st2_bytes += n;
    return g2_buf + off;
}

static inline int g2_route(void) { return ps2_gsq_split && q_is_stage_a; }

static void g2_path_sync(void) {
    int p = ps2_gif_path;
    if (p != g2_sent_path) {
        u8 *r = g2_reserve(2);
        r[0] = G2_PATH; r[1] = (u8)p;
        g2_sent_path = p;
    }
}

void ps2_gsq_reg(u32 reg, u64 val) {
    u8 *r;
    if (!g2_route()) { ps2_gs_write_reg(reg, val); return; }
    g2_path_sync();
    r = g2_reserve(10);
    r[0] = G2_REG; r[1] = (u8)reg;
    memcpy(r + 2, &val, 8);
}

void ps2_gsq_pair(u64 lo, u64 hi) {
    u8 *r;
    if (!g2_route()) { ps2_gs_hwreg_pair(lo, hi); return; }
    g2_path_sync();
    r = g2_reserve(17);
    r[0] = G2_PAIR;
    memcpy(r + 1, &lo, 8); memcpy(r + 9, &hi, 8);
}

void ps2_gsq_stream(const void *data, u32 qwc) {
    const u8 *src = (const u8 *)data;
    if (!g2_route()) { ps2_gs_hwreg_stream(data, qwc); return; }
    g2_path_sync();
    while (qwc) {
        u32 n = qwc < G2_STREAM_MAX ? qwc : G2_STREAM_MAX;
        u8 *r = g2_reserve(5u + n * 16u);
        r[0] = G2_STREAM;
        memcpy(r + 1, &n, 4);
        memcpy(r + 5, src, (size_t)n * 16u);
        src += (size_t)n * 16u;
        qwc -= n;
    }
}

static void g2_priv(u32 addr, u64 val) {
    u8 *r = g2_reserve(13);
    r[0] = G2_PRIV;
    memcpy(r + 1, &addr, 4); memcpy(r + 5, &val, 8);
}

static void g2_field(void) {
    u8 *r = g2_reserve(1);
    r[0] = G2_FIELD;
    g2_publish();                      /* a field is a natural hand-over point */
}

static u32 g2_apply(const u8 *r) {
    switch (r[0]) {
    case G2_REG:  ps2_gs_write_reg(r[1], get64(r, 2)); return 10u;
    case G2_PAIR: ps2_gs_hwreg_pair(get64(r, 1), get64(r, 9)); return 17u;
    case G2_STREAM: {
        u32 qwc = get32(r, 1);
        ps2_gs_hwreg_stream(r + 5, qwc);
        return 5u + qwc * 16u;
    }
    case G2_PRIV: ps2_gs_priv_write_now(get32(r, 1), get64(r, 5)); return 13u;
    case G2_PATH: ps2_gsq_path = (int)r[1]; return 2u;
    case G2_FIELD:
        ps2_field_presenting = ps2_field_consumed++;
        if (!ps2_gs_present_now()) ps2_request_exit();
        return 1u;
    default:
        ps2_log("gsq: corrupt record opcode %u -- stopping the GS thread", r[0]);
        return 0u;
    }
}

static void *g2_main(void *unused) {
    (void)unused;
    q_is_worker = 1;
    ps2_host_prof_attach("gs");
    int speed_on = ps2_speed_enabled();
    u64 burst = 0;
    pthread_mutex_lock(&q_lock);
    for (;;) {
        while (g2_running && __atomic_load_n(&g2_head, __ATOMIC_SEQ_CST) == g2_tail) {
            if (burst) { st2_work_ns += gq_now_ns() - burst; burst = 0; }
            __atomic_store_n(&g2_busy, 0, __ATOMIC_SEQ_CST);
            pthread_cond_broadcast(&q_idle);
            if (__atomic_load_n(&g2_head, __ATOMIC_SEQ_CST) != g2_tail) break;
            pthread_cond_wait(&g2_not_empty, &q_lock);
        }
        if (!g2_running && __atomic_load_n(&g2_head, __ATOMIC_SEQ_CST) == g2_tail) break;
        __atomic_store_n(&g2_busy, 1, __ATOMIC_SEQ_CST);
        if (!burst && speed_on) burst = gq_now_ns();
        size_t tail = g2_tail, head = __atomic_load_n(&g2_head, __ATOMIC_ACQUIRE);
        int fields = 0, corrupt = 0;
        u64 present_ns = 0;
        pthread_mutex_unlock(&q_lock);
        while (tail != head) {
            const u8 *r = g2_buf + (tail & (g2_cap - 1));
            u32 n;
            if (*r == G2_WRAP) { tail += g2_cap - (tail & (g2_cap - 1)); continue; }
            if (*r == G2_FIELD) {
                u64 start = speed_on ? gq_now_ns() : 0;
                { PS2_PHASE_BEGIN(PS2_PH_GFXQ); n = g2_apply(r); PS2_PHASE_END(PS2_PH_GFXQ); }
                if (start) present_ns += gq_now_ns() - start;
                fields++;
                tail += n;
                break;                 /* let the EE know right away */
            }
            { PS2_PHASE_BEGIN(PS2_PH_GFXQ); n = g2_apply(r); PS2_PHASE_END(PS2_PH_GFXQ); }
            if (!n) { corrupt = 1; break; }
            tail += n;
            /* free ring space in large steps while a long batch runs */
            if (((tail - g2_tail) & ~(size_t)0xFFFFF) && tail != head) {
                __atomic_store_n(&g2_tail, tail, __ATOMIC_RELEASE);
                pthread_mutex_lock(&q_lock);
                pthread_cond_broadcast(&g2_not_full);
                pthread_mutex_unlock(&q_lock);
            }
        }
        pthread_mutex_lock(&q_lock);
        __atomic_store_n(&g2_tail, tail, __ATOMIC_RELEASE);
        if (fields) {
            if (speed_on) {
                speed_present_ns += present_ns; speed_present_n += (u64)fields;
                ps2_vu1_speed_counters(&speed_vu_runs, &speed_vu_insns, &speed_vu_guards);
            }
            q_fields_pending -= fields;
            pthread_cond_broadcast(&q_not_full);
        }
        pthread_cond_broadcast(&g2_not_full);
        if (corrupt) { g2_running = 0; q_running = 0; pthread_cond_broadcast(&q_not_empty); break; }
    }
    __atomic_store_n(&g2_busy, 0, __ATOMIC_SEQ_CST);
    pthread_cond_broadcast(&q_idle);
    pthread_cond_broadcast(&g2_not_full);
    pthread_mutex_unlock(&q_lock);
    return NULL;
}

static int g2_start(void) {
    const char *e = getenv("PS2_GFX_SPLIT");
    size_t mb = 32;
    if (e && *e == '0') {
        ps2_log("gsq: PS2_GFX_SPLIT=0 -- the GS stays on the graphics worker");
        return 0;
    }
    { const char *m = getenv("PS2_GFX_SPLIT_MB");
      if (m && *m) { double v = atof(m); if (v >= 1.0 && v <= 1024.0) mb = (size_t)v; } }
    g2_cap = 1;
    while (g2_cap < mb * 1024u * 1024u) g2_cap <<= 1;
    g2_buf = (u8 *)malloc(g2_cap);
    if (!g2_buf) return 0;
    g2_head = g2_tail = g2_w = g2_ptail = 0;
    g2_running = 1;
    if (pthread_create(&g2_thread, NULL, g2_main, NULL) != 0) {
        free(g2_buf); g2_buf = NULL; g2_running = 0;
        return 0;
    }
    ps2_gsq_split = 1;
    ps2_log("gsq: two-stage graphics machine -- VIF1, VU1 and GIF on the worker, "
            "the GS and its Vulkan recording on a thread of their own; %zu MB ring",
            g2_cap >> 20);
    return 1;
}

static void ps2_gsq_speed_line(void) {
    static u64 prev_work, prev_wait;
    u64 work, wait;
    size_t fill;
    if (!ps2_gsq_split) return;
    pthread_mutex_lock(&q_lock);
    work = st2_work_ns; wait = st2_wait_ns;
    fill = g2_head - g2_tail;
    pthread_mutex_unlock(&q_lock);
    ps2_log("speed: GS thread active_ms=%.2f ring_KB=%zu; VIF/VU1 waited on it ms=%.2f",
            (work - prev_work) / 1e6, fill >> 10, (wait - prev_wait) / 1e6);
    prev_work = work; prev_wait = wait;
}

static void g2_stop(void) {
    if (!ps2_gsq_split) return;
    pthread_mutex_lock(&q_lock);
    g2_running = 0;
    pthread_cond_broadcast(&g2_not_empty);
    pthread_mutex_unlock(&q_lock);
    pthread_join(g2_thread, NULL);
    ps2_gsq_split = 0;
}

static u32 q_apply(const u8 *r) {
    if (ps2_gsq_split) {
        /* privileged GS registers and fields belong to the GS stage */
        if (r[0] == GQ_GS_PRIV) { g2_priv(get32(r, 1), get64(r, 5)); return 13u; }
        if (r[0] == GQ_FIELD)   { g2_field(); return 1u; }
    }
    switch (r[0]) {
    case GQ_VIF_QW: {
        int ch = (int)r[1];
        u32 qwc = get32(r, 2);
        const ps2_reg128 *qw = (const ps2_reg128 *)(r + 6);
        ps2_vif_transfer(ch, qw, qwc);
        return 6u + qwc * 16u;
    }
    case GQ_GIF_QW: {
        u32 qwc = get32(r, 1);
        ps2_gif_transfer((const ps2_reg128 *)(r + 5), qwc);
        return 5u + qwc * 16u;
    }
    case GQ_VIF_W:   ps2_vif_write((int)r[1], get32(r, 2));        return 6u;
    case GQ_FBRST:   ps2_vif_fbrst((int)r[1], get32(r, 2));        return 6u;
    case GQ_VIFCLR:  ps2_vif_clear_stall((int)r[1]);               return 2u;
    case GQ_GS_PRIV: ps2_gs_priv_write_now(get32(r, 1), get64(r, 5)); return 13u;
    case GQ_MMIO:    ps2_gif_ctrl_write(get32(r, 1), get32(r, 5)); return 9u;
    case GQ_FIELD:
        ps2_field_presenting = ps2_field_consumed++;
        if (!ps2_gs_present_now()) ps2_request_exit();
        return 1u;
    default:
        ps2_log("gfxq: corrupt record opcode %u -- stopping the worker", r[0]);
        return 0u;
    }
}

static void *q_main(void *unused) {
    (void)unused;
    q_is_worker = 1;
    q_is_stage_a = 1;
    ps2_host_prof_attach("gfx");
    u64 speed_burst=0;
    int speed_on=ps2_speed_enabled();
    pthread_mutex_lock(&q_lock);
    for (;;) {
        while (q_running && __atomic_load_n(&q_head, __ATOMIC_SEQ_CST) == q_tail) {
            if (speed_burst) { speed_work_ns += gq_now_ns()-speed_burst; speed_burst=0; }
            __atomic_store_n(&q_busy, 0, __ATOMIC_SEQ_CST);
            pthread_cond_broadcast(&q_idle);
            if (__atomic_load_n(&q_head, __ATOMIC_SEQ_CST) != q_tail) break;
            pthread_cond_wait(&q_not_empty, &q_lock);
        }
        if (!q_running && __atomic_load_n(&q_head, __ATOMIC_SEQ_CST) == q_tail) break;
        __atomic_store_n(&q_busy, 1, __ATOMIC_SEQ_CST);
        if (!speed_burst && speed_on) speed_burst=gq_now_ns();
        size_t tail = q_tail, head = __atomic_load_n(&q_head, __ATOMIC_ACQUIRE);
        unsigned records = 0;
        int field = 0, corrupt = 0;
        u64 speed_elapsed = 0;
        pthread_mutex_unlock(&q_lock);
        while (tail != head && records < 256) {
            const u8 *r = q_buf + (tail & (q_cap - 1));
            if (*r == GQ_WRAP) {
                tail += q_cap - (tail & (q_cap - 1));
                continue;
            }
            field = *r == GQ_FIELD;
            u64 start = field && speed_on ? gq_now_ns() : 0;
            u32 n;
            { PS2_PHASE_BEGIN(PS2_PH_GFXQ);
              n = q_apply(r);
              PS2_PHASE_END(PS2_PH_GFXQ); }
            if (start) speed_elapsed = gq_now_ns() - start;
            if (!n) { corrupt = 1; break; }
            tail += n;
            records++;
            if (field) break;
        }
        if (ps2_gsq_split) g2_publish();
        pthread_mutex_lock(&q_lock);
        __atomic_store_n(&q_tail, tail, __ATOMIC_RELEASE);
        if (field && !corrupt && ps2_gsq_split) {
            /* the GS thread presents it and releases the field */
            if (speed_burst) { u64 now=gq_now_ns(); speed_work_ns+=now-speed_burst; speed_burst=now; }
        } else if (field && !corrupt) {
            if (speed_on) {
                speed_present_ns += speed_elapsed; speed_present_n++;
                ps2_vu1_speed_counters(&speed_vu_runs,&speed_vu_insns,&speed_vu_guards);
            }
            if (speed_burst) { u64 now=gq_now_ns(); speed_work_ns+=now-speed_burst; speed_burst=now; }
            q_fields_pending--;
        }
        pthread_cond_broadcast(&q_not_full);
        if (corrupt) { q_running = 0; break; }
    }
    __atomic_store_n(&q_busy, 0, __ATOMIC_SEQ_CST);
    pthread_cond_broadcast(&q_idle);
    pthread_mutex_unlock(&q_lock);
    return NULL;
}

static const char *q_forbidden;
void ps2_gfxq_forbid(const char *why) { if (!q_forbidden) q_forbidden = why; }

void ps2_gfxq_init(void) {
    const char *e = getenv("PS2_ASYNC_GFX");
    size_t mb;
    /* AC4's in-flight frames once came out as a few stray screen-sized
       triangles with the graphics machine on its own thread.  Re-tested after
       the VU pair-hazard fix: none in about 2,500 async flight fields, and
       the EE never touches VU1 memory directly (PS2_TRAP_VU1_EE).  The EE
       thread still holds full speed at 6x, so stay synchronous unless asked. */
    if (!e || !*e) e = "0";
    if (e && *e == '0') {
        ps2_log("gfxq: PS2_ASYNC_GFX=0 -- the graphics machine runs on the EE "
                "thread, as it did before.");
        return;
    }

    if (q_forbidden) {
        ps2_log("gfxq: PS2_ASYNC_GFX is set but %s, so the graphics machine "
                "stays on the EE thread.", q_forbidden);
        return;
    }

    mb = 64;
    { const char *m = getenv("PS2_ASYNC_MB");
      if (m && *m) { double v = atof(m); if (v >= 1.0 && v <= 1024.0) mb = (size_t)v; } }
    { const char *d = getenv("PS2_ASYNC_FIELDS");
      if (d && *d) { int v = atoi(d); if (v >= 1 && v <= 8) q_field_depth = v; } }
    q_cap = 1;
    while (q_cap < mb * 1024u * 1024u) q_cap <<= 1;
    q_buf = (u8 *)malloc(q_cap);
    if (!q_buf) {
        ps2_log("gfxq: cannot allocate a %zu MB ring; staying synchronous",
                q_cap >> 20);
        return;
    }
    q_head = q_tail = q_producer_tail = q_reserved_end = 0;
    q_running = 1;
    g2_start();
    if (pthread_create(&q_thread, NULL, q_main, NULL) != 0) {
        ps2_log("gfxq: cannot start the worker thread; staying synchronous");
        free(q_buf); q_buf = NULL; q_running = 0;
        g2_stop();
        return;
    }
    ps2_gfxq_on = 1;
    ps2_log("gfxq: the graphics machine (VIF, VU1, GIF, GS) runs on its own "
            "thread; %zu MB ring", q_cap >> 20);
}

void ps2_gfxq_drain(void) {
    u64 t0;
    if (!ps2_gfxq_on || q_is_worker) return;
    t0 = gq_now_ns();
    pthread_mutex_lock(&q_lock);
    st_drains++;
#define Q_PENDING() (q_head != q_tail || q_busy || q_fields_pending \
                     || (ps2_gsq_split && (g2_head != g2_tail || g2_busy)))
    if (Q_PENDING()) {
        st_drain_waits++;
        pthread_cond_signal(&q_not_empty);
        while (q_running && Q_PENDING())
            pthread_cond_wait(&q_idle, &q_lock);
    }
#undef Q_PENDING
    pthread_mutex_unlock(&q_lock);
    st_drain_ns += gq_now_ns() - t0;
}

void ps2_gfxq_shutdown(void) {
    if (!ps2_gfxq_on) return;
    ps2_gfxq_drain();
    pthread_mutex_lock(&q_lock);
    q_running = 0;
    pthread_cond_broadcast(&q_not_empty);
    pthread_mutex_unlock(&q_lock);
    pthread_join(q_thread, NULL);
    g2_stop();
    ps2_gfxq_on = 0;
}

int ps2_gfxq_blocks_capture(void) { return ps2_gfxq_on; }

void ps2_gfxq_report(void) {
    if (!st_records) return;
    ps2_log("gfxq: %llu records, %llu MB queued, peak fill %zu KB of %zu MB",
            (unsigned long long)st_records,
            (unsigned long long)(st_bytes >> 20),
            st_peak_fill >> 10, q_cap >> 20);
    ps2_log("gfxq: %llu drains, %llu of them actually waited (%.1f ms total); "
            "the EE waited on a full ring %llu times (%.1f ms total)",
            (unsigned long long)st_drains, (unsigned long long)st_drain_waits,
            (double)st_drain_ns / 1e6,
            (unsigned long long)st_producer_waits,
            (double)st_producer_ns / 1e6);
    ps2_log("gfxq: the EE waited for the graphics machine to catch up on "
            "fields %llu times (%.1f ms total), at a depth of %d",
            (unsigned long long)st_field_waits,
            (double)st_field_wait_ns / 1e6, q_field_depth);
    if (st_producer_waits)
        ps2_log("gfxq:   a non-zero full-ring count means the graphics machine "
                "is the constraint, not the EE -- a bigger ring will not help.");
    if (st2_records)
        ps2_log("gsq: %llu GS records, %llu MB, peak fill %zu KB of %zu MB; the "
                "VIF/VU1 stage waited on a full GS ring %llu times (%.1f ms total)",
                (unsigned long long)st2_records, (unsigned long long)(st2_bytes >> 20),
                st2_peak >> 10, g2_cap >> 20, (unsigned long long)st2_waits,
                (double)st2_wait_ns / 1e6);
}

/* VIF0 feeds VU0, which the EE drives directly: COP2 macro instructions and
   VCALLMS microprograms run on the EE thread and read VU0 memory as soon as
   the game's DMA has been issued.  Queueing VIF0 to the graphics thread let
   a VU0 microprogram run while its vertices were still being unpacked --
   AC4's VU0 skinning then mixed two characters' vertices (the deck crew in
   Mission 01 came out as heaps of polygons).  So only VIF1, which belongs
   to the graphics machine, goes through the queue; VIF0 runs here, in
   order with the EE, as it does with PS2_ASYNC_GFX=0.  `which` carries the
   channel in bit 0 (see PS2_VIF_WORD_AT). */
static int vif_is_vu0(int which) { return (which & 1) == 0; }

void ps2_gfxq_vif_qw(int ch, const ps2_reg128 *qw, u32 qwc) {
    if (!ps2_gfxq_on || vif_is_vu0(ch)) {
        ps2_vif_transfer(ch, qw, qwc);
        return;
    }
    if (!qwc) return;
    {
        u32 n = 6u + qwc * 16u;
        u8 *r = q_reserve(n);
        put8(r, 0, GQ_VIF_QW);
        put8(r, 1, (u8)ch);
        put32(r, 2, qwc);
        memcpy(r + 6, qw, (size_t)qwc * 16u);
        q_commit(n);
    }
}

void ps2_gfxq_gif_qw(const ps2_reg128 *qw, u32 qwc) {
    if (!ps2_gfxq_on) { ps2_gif_transfer(qw, qwc); return; }
    if (!qwc) return;
    {
        u32 n = 5u + qwc * 16u;
        u8 *r = q_reserve(n);
        put8(r, 0, GQ_GIF_QW);
        put32(r, 1, qwc);
        memcpy(r + 5, qw, (size_t)qwc * 16u);
        q_commit(n);
    }
}

void ps2_gfxq_vif_word(int ch, u32 word) {
    if (!ps2_gfxq_on || vif_is_vu0(ch)) { ps2_vif_write(ch, word); return; }
    { u8 *r = q_reserve(6);
      put8(r, 0, GQ_VIF_W); put8(r, 1, (u8)ch); put32(r, 2, word);
      q_commit(6); }
}

void ps2_gfxq_fbrst(int ch, u32 val) {
    if (!ps2_gfxq_on || vif_is_vu0(ch)) { ps2_vif_fbrst(ch, val); return; }
    { u8 *r = q_reserve(6);
      put8(r, 0, GQ_FBRST); put8(r, 1, (u8)ch); put32(r, 2, val);
      q_commit(6); }
}

void ps2_gfxq_vif_clear(int ch) {
    if (!ps2_gfxq_on || vif_is_vu0(ch)) { ps2_vif_clear_stall(ch); return; }
    { u8 *r = q_reserve(2);
      put8(r, 0, GQ_VIFCLR); put8(r, 1, (u8)ch);
      q_commit(2); }
}

void ps2_gfxq_gs_priv(u32 addr, u64 val) {
    if (!ps2_gfxq_on) { ps2_gs_priv_write_now(addr, val); return; }
    { u8 *r = q_reserve(13);
      put8(r, 0, GQ_GS_PRIV); put32(r, 1, addr); put64(r, 5, val);
      q_commit(13); }
}

/* when the EE issued each field (and with it that field's sound), by field
   number from 0 -- the renderer compares its present time against this */
static u64 field_issue[1024];
static u32 field_issue_seq;
u64 ps2_field_issue_ns(u32 seq) {
    if (seq >= __atomic_load_n(&field_issue_seq, __ATOMIC_ACQUIRE)
        || field_issue_seq - seq >= 1024u) return 0;
    return __atomic_load_n(&field_issue[seq & 1023u], __ATOMIC_RELAXED);
}

/* fields issued so far: the number the next field will present as */
u32 ps2_field_issued(void) { return __atomic_load_n(&field_issue_seq, __ATOMIC_ACQUIRE); }

int ps2_gfxq_field(void) {
    __atomic_store_n(&field_issue[field_issue_seq & 1023u], gq_now_ns(), __ATOMIC_RELAXED);
    __atomic_store_n(&field_issue_seq, field_issue_seq + 1u, __ATOMIC_RELEASE);
    if (!ps2_gfxq_on) {
        u64 start = ps2_speed_enabled() ? gq_now_ns() : 0;
        ps2_field_presenting = ps2_field_consumed++;
        int result = ps2_gs_present_now();
        if (start) {
            speed_present_ns += gq_now_ns()-start; speed_present_n++;
            ps2_vu1_speed_counters(&speed_vu_runs,&speed_vu_insns,&speed_vu_guards);
        }
        return result;
    }
    pthread_mutex_lock(&q_lock);
    if (q_fields_pending >= q_field_depth) {
        u64 t0 = gq_now_ns();
        st_field_waits++;
        while (q_running && q_fields_pending >= q_field_depth)
            pthread_cond_wait(&q_not_full, &q_lock);
        st_field_wait_ns += gq_now_ns() - t0;
    }
    q_fields_pending++;
    pthread_mutex_unlock(&q_lock);
    { u8 *r = q_reserve(1);
      put8(r, 0, GQ_FIELD);
      q_commit(1); }
    return 1;
}

void ps2_gfxq_mmio(u32 addr, u32 val) {
    if (!ps2_gfxq_on) { ps2_gif_ctrl_write(addr, val); return; }
    { u8 *r = q_reserve(9);
      put8(r, 0, GQ_MMIO); put32(r, 1, addr); put32(r, 5, val);
      q_commit(9); }
}
