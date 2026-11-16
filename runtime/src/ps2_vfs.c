#ifndef _WIN32
#define _FILE_OFFSET_BITS 64
#endif
#include "ps2_runtime.h"
#include "ps2_hle.h"
#include "ps2_vfs.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <dirent.h>
#include <pthread.h>

#define SECTOR        PS2_VFS_SECTOR
#define FIRST_LSN     32u

typedef enum { SRC_IMAGE, SRC_TREE, SRC_HOST } vfs_src;

typedef struct vfs_claim vfs_claim;

typedef struct {
    ps2_disc_file f;
    vfs_src src;
    u64 image_off;
    char *host;
    u32 disc_size;
    const vfs_claim *claim;
    int failed;
} vfs_node;

static vfs_node **nodes;
static u32 nnodes, cnodes;
static u32 *slots;
static u32 nslots;
static int vfs_is_tree, vfs_opened;
static char vfs_path[1024];
static FILE *image_fp;
static u32 end_lsn;
static pthread_mutex_t vfs_lock = PTHREAD_MUTEX_INITIALIZER;
static u64 reads_sectors, reads_bytes, reads_from_mods;

typedef enum { CL_PENDING, CL_DISC, CL_NEW, CL_BAD } claim_kind;

struct vfs_claim {
    char *path;
    char *host;
    u64 size;
    int priority;
    unsigned order;
    const char *owner;
    claim_kind kind;
    u32 won;
};

static vfs_claim **claims;
static u32 nclaims, cclaims;
static u32 nconflicts;

static u32 sectors_of(u32 size) { return size ? (size + SECTOR - 1u) / SECTOR : 1u; }
static int cdp_patch_name(const char *name, u32 *entry);

static char upc(char c) { return (c >= 'a' && c <= 'z') ? (char)(c - 32) : c; }

static int ieq(const char *a, const char *b) {
    while (*a && *b && upc(*a) == upc(*b)) { a++; b++; }
    return !*a && !*b;
}

static int icmp(const char *a, const char *b) {
    while (*a && *b && upc(*a) == upc(*b)) { a++; b++; }
    return (unsigned char)upc(*a) - (unsigned char)upc(*b);
}

static char *xstrdup(const char *s) {
    size_t n = strlen(s) + 1;
    char *d = (char *)malloc(n);
    if (!d) ps2_fatal("vfs: out of memory");
    memcpy(d, s, n);
    return d;
}

static int norm_path(const char *name, char *out, size_t cap) {
    const char *p = name;
    size_t n = 0;
    for (const char *q = p; *q && (q - p) < 16; q++) {
        if (*q == '/' || *q == '\\') break;
        if (*q == ':') { p = q + 1; break; }
    }
    for (; *p; p++) {
        char c = *p == '\\' ? '/' : *p;
        if (c == ';') break;
        if (c == '/' && (n == 0 || out[n - 1] == '/')) continue;
        if (n + 1 >= cap) return -1;
        out[n++] = c;
    }
    while (n && out[n - 1] == '/') n--;
    out[n] = 0;
    return (int)n;
}

#ifdef _WIN32
static int seek64(FILE *fp, u64 off) { return _fseeki64(fp, (long long)off, SEEK_SET); }
#else
static int seek64(FILE *fp, u64 off) { return fseeko(fp, (off_t)off, SEEK_SET); }
#endif

static int host_stat(const char *path, u64 *size, int *is_dir) {
#ifdef _WIN32
    struct _stat64 st;
    if (_stat64(path, &st) != 0) return -1;
#else
    struct stat st;
    if (stat(path, &st) != 0) return -1;
#endif
    if (size) *size = (u64)st.st_size;
    if (is_dir) *is_dir = S_ISDIR(st.st_mode);
    return 0;
}

#define VFS_HANDLES 8
static struct { const char *path; FILE *fp; u64 tick; } handles[VFS_HANDLES];
static u64 handle_tick;

static FILE *handle_for(const char *path) {
    unsigned i, victim = 0;
    for (i = 0; i < VFS_HANDLES; i++) {
        if (handles[i].fp && handles[i].path == path) {
            handles[i].tick = ++handle_tick;
            return handles[i].fp;
        }
    }
    for (i = 0; i < VFS_HANDLES; i++) {
        if (!handles[i].fp) { victim = i; break; }
        if (handles[i].tick < handles[victim].tick) victim = i;
    }
    if (handles[victim].fp) fclose(handles[victim].fp);
    handles[victim].fp = fopen(path, "rb");
    handles[victim].path = handles[victim].fp ? path : NULL;
    handles[victim].tick = ++handle_tick;
    return handles[victim].fp;
}

static void guest_put(u32 addr, const u8 *src, u32 n) {
    while (n) {
        u32 off = addr & PS2_PAGE_MASK;
        u32 chunk = PS2_PAGE_SIZE - off;
        u8 *p = ps2_pt[addr >> PS2_PAGE_BITS];
        if (chunk > n) chunk = n;
        if (p) memcpy(p + off, src, chunk);
        else for (u32 i = 0; i < chunk; i++) ps2_w8(addr + i, src[i]);
        addr += chunk;
        src += chunk;
        n -= chunk;
    }
}

static u32 hash_name(const char *s) {
    u32 h = 2166136261u;
    for (; *s; s++) { h ^= (u8)upc(*s); h *= 16777619u; }
    return h;
}

static void hash_insert(u32 index) {
    u32 i = hash_name(nodes[index]->f.name) & (nslots - 1u);
    while (slots[i]) i = (i + 1u) & (nslots - 1u);
    slots[i] = index + 1u;
}

static void hash_grow(void) {
    u32 want = 1024;
    while (want < nnodes * 2u + 2u) want *= 2u;
    if (want <= nslots) return;
    free(slots);
    slots = (u32 *)calloc(want, sizeof *slots);
    if (!slots) ps2_fatal("vfs: out of memory indexing the disc");
    nslots = want;
    for (u32 i = 0; i < nnodes; i++) hash_insert(i);
}

static vfs_node *lookup(const char *normalised) {
    u32 i;
    if (!nslots) return NULL;
    for (i = hash_name(normalised) & (nslots - 1u); slots[i];
         i = (i + 1u) & (nslots - 1u)) {
        vfs_node *n = nodes[slots[i] - 1u];
        if (ieq(n->f.name, normalised)) return n;
    }
    return NULL;
}

static vfs_node *add_node(const char *name, u32 size) {
    vfs_node *n;
    if (strlen(name) >= sizeof n->f.name) {
        ps2_log("vfs: path too long for the disc index, skipped: %s", name);
        return NULL;
    }
    if (nnodes == cnodes) {
        cnodes = cnodes ? cnodes * 2u : 256u;
        nodes = (vfs_node **)realloc(nodes, cnodes * sizeof *nodes);
        if (!nodes) ps2_fatal("vfs: out of memory building the disc index");
    }
    n = (vfs_node *)calloc(1, sizeof *n);
    if (!n) ps2_fatal("vfs: out of memory building the disc index");
    snprintf(n->f.name, sizeof n->f.name, "%s", name);
    n->f.size = size;
    n->disc_size = size;
    nodes[nnodes++] = n;
    if (nnodes * 2u >= nslots) hash_grow();
    else hash_insert(nnodes - 1u);
    return n;
}

/* ---- DATA.CDP entry patches ------------------------------------------
   Ace Combat 04 keeps nearly everything in BIN/DATA.CDP (721 MB), indexed by
   BIN/DATA.TBL as (start sector, size in bytes) pairs.  A mod replaces one
   entry with a file named BIN/DATA.CDP.<entry number, 4 digits> (in its
   files/ directory): the replacement is appended after the original data
   (the disc's sectors are laid out virtually, so the archive simply grows)
   and DATA.TBL is served from memory with that entry pointing at it.  Size
   does not matter -- a translated text pack may be larger than the
   original. */
typedef struct {
    u64 off; u32 size; u32 entry; vfs_node *src;
    /* .PARTS patches: the original entry with some nested-pack parts
       replaced, rebuilt in memory on first read (texts inside a 10 MB
       mission pack cost a few KB on disk instead of the whole pack) */
    int parts; u32 orig_sec, orig_size;
    u8 *cache; u64 used;
} cdp_patch;
static vfs_node *cdp_node, *tbl_node;
static u64 cdp_orig_bytes;
static cdp_patch *cdp_patches;
static u32 cdp_npatches;
static u8 *tbl_mem;

static void node_read_base(vfs_node *n, u64 pos, u32 len, u8 *dst);
static void node_read(vfs_node *n, u64 pos, u32 len, u8 *dst);

static u32 rd32(const u8 *p) { u32 v; memcpy(&v, p, 4); return v; }

/* the nested pack (u32 count, u32 offsets[count], parts) with the part at
   path replaced: parts keep their order and 16-byte alignment, empty parts
   keep offset 0 -- the same as tools/ac4_es.py replace_part */
static u8 *pack_replace(const u8 *b, u32 blen, const u32 *path, u32 depth,
                        const u8 *nw, u32 nlen, u32 *out_len) {
    u32 n, k, i, nstarts = 0, starts[256], target, len = 0, cap;
    u32 newoff[256];
    u8 *out;
    if (!depth) {
        out = (u8 *)malloc(nlen ? nlen : 1u);
        if (out) memcpy(out, nw, nlen);
        *out_len = nlen;
        return out;
    }
    if (blen < 8) return NULL;
    n = rd32(b);
    if (!n || n > 256u || 4u + 4u * n > blen || path[0] >= n) return NULL;
    for (k = 0; k < n; k++) {
        u32 o = rd32(b + 4 + 4 * k), dup = 0;
        if (o < 4u + 4u * n || o > blen) continue;
        for (i = 0; i < nstarts; i++) if (starts[i] == o) dup = 1;
        if (!dup) starts[nstarts++] = o;
    }
    for (i = 1; i < nstarts; i++)                      /* sort */
        for (k = i; k > 0 && starts[k - 1] > starts[k]; k--) {
            u32 t = starts[k]; starts[k] = starts[k - 1]; starts[k - 1] = t;
        }
    target = rd32(b + 4 + 4 * path[0]);
    cap = blen + nlen + 16u * (nstarts + 1u);
    out = (u8 *)malloc(cap);
    if (!out || !nstarts) { free(out); return NULL; }
    memcpy(out, b, starts[0]);
    len = starts[0];
    for (i = 0; i < nstarts; i++) {
        u32 s = starts[i], e = i + 1 < nstarts ? starts[i + 1] : blen;
        const u8 *src = b + s;
        u32 slen = e - s;
        u8 *sub = NULL;
        while (len % 16u) out[len++] = 0;
        newoff[i] = len;
        if (s == target) {
            sub = pack_replace(src, slen, path + 1, depth - 1, nw, nlen, &slen);
            if (!sub) { free(out); return NULL; }
            src = sub;
        }
        if (len + slen > cap) {
            u8 *g = (u8 *)realloc(out, cap = len + slen + 16u * nstarts);
            if (!g) { free(sub); free(out); return NULL; }
            out = g;
        }
        memcpy(out + len, src, slen);
        len += slen;
        free(sub);
    }
    for (k = 0; k < n; k++) {
        u32 o = rd32(b + 4 + 4 * k);
        for (i = 0; i < nstarts; i++)
            if (starts[i] == o) { memcpy(out + 4 + 4 * k, &newoff[i], 4); break; }
    }
    *out_len = len;
    return out;
}

static u64 cdp_use_clock;
static const u8 *cdp_rebuilt(cdp_patch *p) {
    u8 *blob, *pf;
    u32 blen, plen, at, count, i, cached = 0;
    if (p->cache) { p->used = ++cdp_use_clock; return p->cache; }
    for (i = 0; i < cdp_npatches; i++) if (cdp_patches[i].cache) cached++;
    while (cached >= 3u) {                 /* keep the three most recent */
        cdp_patch *old = NULL;
        for (i = 0; i < cdp_npatches; i++)
            if (cdp_patches[i].cache && (!old || cdp_patches[i].used < old->used))
                old = &cdp_patches[i];
        free(old->cache); old->cache = NULL; cached--;
    }
    if (p->parts == 2) {
        /* version 2: the original entry, grown to the patch's size, with
           byte ranges written over it (u32 at, u32 len, data, padded to 4).
           Every original byte the patch does not name stays where it was:
           the game reads some parts at fixed places (the menu help pack
           177 keeps data right after its text that the hangar models use),
           so new texts go after the end of the entry and only the offset
           tables point at them -- see tools/ac4_es.py */
        u32 keep = p->orig_size < p->size ? p->orig_size : p->size;
        blob = (u8 *)calloc(p->size ? p->size : 1u, 1);
        pf = (u8 *)malloc(p->src->f.size ? p->src->f.size : 1u);
        if (!blob || !pf) { free(blob); free(pf); return NULL; }
        node_read_base(cdp_node, (u64)p->orig_sec * SECTOR, keep, blob);
        node_read(p->src, 0, p->src->f.size, pf);
        plen = p->src->f.size;
        count = plen >= 16 ? rd32(pf + 12) : 0;
        at = 16;
        for (i = 0; i < count; i++) {
            u32 to, n;
            if (at + 8u > plen) break;
            to = rd32(pf + at); n = rd32(pf + at + 4); at += 8;
            if (n > plen - at || to > p->size || n > p->size - to) {
                ps2_log("vfs: DATA.CDP entry %u: write %u is out of range", p->entry, i);
                break;
            }
            memcpy(blob + to, pf + at, n);
            at += n;
            while (at % 4u) at++;
        }
        free(pf);
        p->cache = blob;
        p->used = ++cdp_use_clock;
        return blob;
    }
    blob = (u8 *)malloc(p->orig_size ? p->orig_size : 1u);
    pf = (u8 *)malloc(p->src->f.size ? p->src->f.size : 1u);
    if (!blob || !pf) { free(blob); free(pf); return NULL; }
    node_read_base(cdp_node, (u64)p->orig_sec * SECTOR, p->orig_size, blob);
    node_read(p->src, 0, p->src->f.size, pf);
    blen = p->orig_size; plen = p->src->f.size;
    count = plen >= 16 ? rd32(pf + 12) : 0;
    at = 16;
    for (i = 0; i < count; i++) {
        u32 depth, path[16], dlen, nl, k;
        u8 *nb;
        if (at + 4 > plen) break;
        depth = rd32(pf + at); at += 4;
        if (depth > 16u || at + 4u * depth + 4u > plen) break;
        for (k = 0; k < depth; k++) path[k] = rd32(pf + at + 4 * k);
        at += 4u * depth;
        dlen = rd32(pf + at); at += 4;
        if (at + dlen > plen) break;
        nb = pack_replace(blob, blen, path, depth, pf + at, dlen, &nl);
        at += dlen;
        while (at % 4u) at++;
        if (!nb) { ps2_log("vfs: DATA.CDP entry %u: part %u does not fit the pack", p->entry, i); continue; }
        free(blob); blob = nb; blen = nl;
    }
    free(pf);
    if (blen != p->size)
        ps2_log("vfs: DATA.CDP entry %u rebuilt to %u bytes, the patch said %u", p->entry, blen, p->size);
    if (blen < p->size) {
        u8 *g = (u8 *)realloc(blob, p->size);
        if (!g) { free(blob); return NULL; }
        blob = g;
        memset(blob + blen, 0, p->size - blen);
    }
    p->cache = blob;
    p->used = ++cdp_use_clock;
    return blob;
}

/* the movie being streamed (ps2_ipu.c's overlays): the last .SMV read, and a
   count that moves on each time one is read from its first sector */
static char movie_name[64];
static u32 movie_gen;
u32 ps2_vfs_movie(char *name, u32 cap) {
    u32 g;
    pthread_mutex_lock(&vfs_lock);
    g = movie_gen;
    if (name && cap) snprintf(name, cap, "%s", movie_name);
    pthread_mutex_unlock(&vfs_lock);
    return g;
}

static void note_movie(vfs_node *n, u64 pos) {
    size_t k = strlen(n->f.name);
    if (k < 4 || strcmp(n->f.name + k - 4, ".SMV")) return;
    if (pos == 0 || strcmp(movie_name, n->f.name)) {
        snprintf(movie_name, sizeof movie_name, "%s", n->f.name);
        movie_gen++;
    }
}

static void node_read(vfs_node *n, u64 pos, u32 len, u8 *dst) {
    note_movie(n, pos);
    if (n == tbl_node && tbl_mem) {
        u32 data = pos < n->f.size ? (u32)((u64)n->f.size - pos < len ? n->f.size - pos : len) : 0u;
        if (data) memcpy(dst, tbl_mem + pos, data);
        if (data < len) memset(dst + data, 0, len - data);
        return;
    }
    if (n == cdp_node && cdp_npatches && pos + len > cdp_orig_bytes) {
        u32 done = 0;
        if (pos < cdp_orig_bytes) {
            done = (u32)(cdp_orig_bytes - pos);
            node_read_base(n, pos, done, dst);
        }
        while (done < len) {
            u64 at = pos + done;
            u32 k, run = len - done;
            const cdp_patch *p = NULL;
            for (k = 0; k < cdp_npatches; k++)
                if (at >= cdp_patches[k].off
                    && at < cdp_patches[k].off + sectors_of(cdp_patches[k].size) * (u64)SECTOR) {
                    p = &cdp_patches[k];
                    break;
                }
            if (!p) { memset(dst + done, 0, 1); done++; continue; }
            {
                u64 rel = at - p->off, end = sectors_of(p->size) * (u64)SECTOR;
                if (run > end - rel) run = (u32)(end - rel);
                if (p->parts) {
                    const u8 *blob = cdp_rebuilt((cdp_patch *)p);
                    u32 have = rel < p->size ? (u32)(p->size - rel) : 0u;
                    if (have > run) have = run;
                    if (blob && have) memcpy(dst + done, blob + rel, have);
                    else have = 0;
                    if (have < run) memset(dst + done + have, 0, run - have);
                } else {
                    node_read(p->src, rel, run, dst + done);   /* zeros past its size */
                }
            }
            done += run;
        }
        return;
    }
    node_read_base(n, pos, len, dst);
}

static void node_read_base(vfs_node *n, u64 pos, u32 len, u8 *dst) {
    u32 data = 0;
    if (pos < n->f.size) {
        u64 left = (u64)n->f.size - pos;
        data = left < len ? (u32)left : len;
    }
    if (data < len) memset(dst + data, 0, len - data);
    if (!data) return;
    switch (n->src) {
    case SRC_IMAGE:
        if (image_fp && seek64(image_fp, n->image_off + pos) == 0
            && fread(dst, 1, data, image_fp) == data)
            return;
        break;
    case SRC_TREE:
    case SRC_HOST: {
        FILE *fp = handle_for(n->host);
        size_t got;
        if (!fp || seek64(fp, pos) != 0) break;
        got = fread(dst, 1, data, fp);
        if (n->src == SRC_HOST) reads_from_mods++;
        if (got == data) return;
        memset(dst + got, 0, data - got);
        if (!n->failed) {
            n->failed = 1;
            ps2_log("vfs: '%s' is shorter on disk than when it was indexed "
                    "(%s); the rest reads as zeros", n->f.name, n->host);
        }
        return;
    }
    }
    memset(dst, 0, data);
    if (!n->failed) {
        n->failed = 1;
        ps2_log("vfs: cannot read '%s' from %s; it reads as zeros", n->f.name,
                n->src == SRC_IMAGE ? "the disc image" : n->host);
    }
}

static vfs_node *node_at(u32 lsn) {
    u32 lo = 0, hi = nnodes;
    while (lo < hi) {
        u32 mid = lo + (hi - lo) / 2u;
        vfs_node *n = nodes[mid];
        if (lsn < n->f.lsn) hi = mid;
        else if (lsn >= n->f.lsn + sectors_of(n->f.size)) lo = mid + 1u;
        else return n;
    }
    return NULL;
}

int ps2_vfs_read_sectors(u32 lsn, u32 sectors, void *dst) {
    u8 *out = (u8 *)dst;
    u32 done = 0;
    if (!vfs_opened) return -1;
    pthread_mutex_lock(&vfs_lock);
    reads_sectors += sectors;
    while (done < sectors) {
        vfs_node *n = node_at(lsn + done);
        u32 in, run;
        if (!n) {
            memset(out + (size_t)done * SECTOR, 0, SECTOR);
            done++;
            continue;
        }
        in = lsn + done - n->f.lsn;
        run = sectors_of(n->f.size) - in;
        if (run > sectors - done) run = sectors - done;
        node_read(n, (u64)in * SECTOR, run * SECTOR, out + (size_t)done * SECTOR);
        done += run;
    }
    pthread_mutex_unlock(&vfs_lock);
    return (int)sectors;
}

int ps2_vfs_read_sectors_guest(u32 lsn, u32 sectors, u32 guest_addr) {
    enum { CHUNK = 256 };
    u8 *buf;
    u32 done = 0;
    if (!vfs_opened) return -1;
    buf = (u8 *)malloc((size_t)(sectors < CHUNK ? (sectors ? sectors : 1u) : CHUNK)
                       * SECTOR);
    if (!buf) return -1;
    while (done < sectors) {
        u32 run = sectors - done < CHUNK ? sectors - done : CHUNK;
        ps2_vfs_read_sectors(lsn + done, run, buf);
        guest_put(guest_addr + done * SECTOR, buf, run * SECTOR);
        done += run;
    }
    free(buf);
    return (int)sectors;
}

int ps2_vfs_read(const ps2_disc_file *f, u64 pos, u32 len, void *dst) {
    vfs_node *n = (vfs_node *)(void *)f;
    if (!f || !vfs_opened) return -1;
    if (pos >= f->size) return 0;
    if (pos + len > f->size) len = (u32)((u64)f->size - pos);
    pthread_mutex_lock(&vfs_lock);
    node_read(n, pos, len, (u8 *)dst);
    reads_bytes += len;
    pthread_mutex_unlock(&vfs_lock);
    return (int)len;
}

int ps2_vfs_read_guest(const ps2_disc_file *f, u64 pos, u32 len,
                       u32 guest_addr) {
    enum { CHUNK = 1u << 20 };
    u8 *buf;
    u32 done = 0;
    if (!f || !vfs_opened) return -1;
    if (pos >= f->size) return 0;
    if (pos + len > f->size) len = (u32)((u64)f->size - pos);
    buf = (u8 *)malloc(len < CHUNK ? (len ? len : 1u) : CHUNK);
    if (!buf) return -1;
    while (done < len) {
        u32 run = len - done < CHUNK ? len - done : CHUNK;
        ps2_vfs_read(f, pos + done, run, buf);
        guest_put(guest_addr + done, buf, run);
        done += run;
    }
    free(buf);
    return (int)len;
}

const ps2_disc_file *ps2_vfs_find(const char *name) {
    char norm[256];
    vfs_node *n;
    if (!name || norm_path(name, norm, sizeof norm) <= 0) return NULL;
    {   /* tests: PS2_VFS_ALIAS=<path>=<other path> serves the other file when
           the game asks for the first (e.g. another mission's movie) */
        const char *e = getenv("PS2_VFS_ALIAS");
        char from[256];
        const char *eq = e ? strchr(e, '=') : NULL;
        if (eq && (size_t)(eq - e) < sizeof from) {
            memcpy(from, e, (size_t)(eq - e));
            from[eq - e] = 0;
            if (ieq(from, norm) && (n = lookup(eq + 1)) != NULL) {
                ps2_log("vfs: alias %s -> %s", norm, eq + 1);
                return &n->f;
            }
        }
    }
    n = lookup(norm);
    return n ? &n->f : NULL;
}

u32 ps2_vfs_file_count(void) { return nnodes; }
int ps2_vfs_ready(void) { return vfs_opened; }

static int image_sector(u32 lsn, u8 *dst) {
    return (image_fp && seek64(image_fp, (u64)lsn * SECTOR) == 0
            && fread(dst, 1, SECTOR, image_fp) == SECTOR) ? 0 : -1;
}

static void image_scan_dir(u32 lsn, u32 size, const char *prefix, int depth) {
    u8 buf[SECTOR];
    u32 sectors = (size + SECTOR - 1u) / SECTOR;
    for (u32 s = 0; s < sectors; s++) {
        u32 off = 0;
        if (image_sector(lsn + s, buf) != 0) break;
        while (off < SECTOR) {
            u8 len = buf[off];
            u32 elsn, esize, nlen;
            char name[128], full[256];
            if (len == 0 || off + len > SECTOR) break;
            memcpy(&elsn, buf + off + 2, 4);
            memcpy(&esize, buf + off + 10, 4);
            nlen = buf[off + 32];
            if (nlen == 1 && (buf[off + 33] == 0 || buf[off + 33] == 1)) {
                off += len;
                continue;
            }
            if (nlen > sizeof name - 1) nlen = sizeof name - 1;
            memcpy(name, buf + off + 33, nlen);
            name[nlen] = 0;
            for (u32 i = 0; i < nlen; i++)
                if (name[i] == ';') { name[i] = 0; break; }
            snprintf(full, sizeof full, "%s%s%s", prefix, *prefix ? "/" : "",
                     name);
            if (buf[off + 25] & 2) {
                if (depth < 8) image_scan_dir(elsn, esize, full, depth + 1);
            } else {
                vfs_node *n = add_node(full, esize);
                if (n) {
                    n->src = SRC_IMAGE;
                    n->image_off = (u64)elsn * SECTOR;
                    memcpy(n->f.date, buf + off + 18, 7);
                }
            }
            off += len;
        }
    }
}

static void tree_scan(const char *hostdir, const char *prefix, int depth) {
    DIR *d = opendir(hostdir);
    struct dirent *e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        char hp[1024], full[256];
        u64 size;
        int is_dir, hn, fn;
        if (e->d_name[0] == '.') continue;
        hn = snprintf(hp, sizeof hp, "%s/%s", hostdir, e->d_name);
        fn = snprintf(full, sizeof full, "%s%s%s", prefix, *prefix ? "/" : "",
                      e->d_name);
        if (hn < 0 || (size_t)hn >= sizeof hp || fn < 0
            || (size_t)fn >= sizeof full) {
            ps2_log("vfs: path too long, not indexed: %s/%s", hostdir, e->d_name);
            continue;
        }
        if (host_stat(hp, &size, &is_dir) != 0) continue;
        if (is_dir) {
            if (depth < 8) tree_scan(hp, full, depth + 1);
        } else if (size > 0xFFFFFFFFull) {
            ps2_log("vfs: '%s' is larger than a disc file can be; not indexed",
                    hp);
        } else {
            vfs_node *n = add_node(full, (u32)size);
            if (n) {
                n->src = SRC_TREE;
                n->host = xstrdup(hp);
            }
        }
    }
    closedir(d);
}

static int claim_beats(const vfs_claim *a, const vfs_claim *b) {
    if (a->priority != b->priority) return a->priority > b->priority ? 1 : -1;
    return a->order > b->order ? 1 : -1;
}

static void say_conflict(const char *what, const vfs_claim *win,
                         const vfs_claim *lose) {
    nconflicts++;
    ps2_log("vfs: conflict on %s -- %s (priority %d, '%s') wins over %s "
            "(priority %d, '%s')", what, win->owner, win->priority, win->path,
            lose->owner, lose->priority, lose->path);
}

s64 ps2_vfs_stat(const char *path) {
    char norm[512];
    vfs_node *n;
    s64 size = -1;
    if (!path || norm_path(path, norm, sizeof norm) <= 0 || !vfs_opened) return -1;
    pthread_mutex_lock(&vfs_lock);
    n = lookup(norm);
    if (n) size = n->f.size;
    pthread_mutex_unlock(&vfs_lock);
    return size;
}

s64 ps2_vfs_read_path(const char *path, u64 pos, u32 len, void *dst) {
    char norm[512];
    vfs_node *n;
    s64 got = -1;
    if (!path || norm_path(path, norm, sizeof norm) <= 0 || !vfs_opened) return -1;
    pthread_mutex_lock(&vfs_lock);
    n = lookup(norm);
    if (n) {
        if (pos >= n->f.size) got = 0;
        else {
            u64 left = (u64)n->f.size - pos;
            got = left < len ? (s64)left : (s64)len;
            node_read(n, pos, (u32)got, (u8 *)dst);
        }
    }
    pthread_mutex_unlock(&vfs_lock);
    return got;
}

void ps2_vfs_claim(const char *path, const char *host, u64 size, int priority,
                   const char *owner) {
    char norm[512];
    vfs_claim *c;
    if (vfs_opened) {
        ps2_log("vfs: %s's '%s' arrived after the disc was opened; ignored",
                owner ? owner : "?", path);
        return;
    }
    if (norm_path(path, norm, sizeof norm) <= 0) {
        ps2_log("vfs: %s's '%s' is not a usable path; ignored",
                owner ? owner : "?", path);
        return;
    }
    if (nclaims == cclaims) {
        cclaims = cclaims ? cclaims * 2u : 64u;
        claims = (vfs_claim **)realloc(claims, cclaims * sizeof *claims);
        if (!claims) ps2_fatal("vfs: out of memory recording a mod file");
    }
    c = (vfs_claim *)calloc(1, sizeof *c);
    if (!c) ps2_fatal("vfs: out of memory recording a mod file");
    c->path = xstrdup(norm);
    c->host = xstrdup(host);
    c->size = size;
    c->priority = priority;
    c->order = nclaims;
    c->owner = owner ? owner : "?";
    claims[nclaims++] = c;
}

static int claim_ptr_cmp(const void *a, const void *b) {
    const vfs_claim *x = *(const vfs_claim *const *)a;
    const vfs_claim *y = *(const vfs_claim *const *)b;
    int c = icmp(x->path, y->path);
    if (c) return c;
    return claim_beats(x, y) > 0 ? 1 : -1;
}

static void settle_file(vfs_claim **run, u32 i, u32 j, vfs_node *n) {
    vfs_claim *win = run[j - 1];
    claim_kind kind = n ? CL_DISC : CL_NEW;
    for (u32 x = i; x < j; x++) run[x]->kind = kind;
    for (u32 x = i; x + 1u < j; x++) {
        char what[300];
        snprintf(what, sizeof what, "'%s'", win->path);
        say_conflict(what, win, run[x]);
    }
    if (win->size > 0xFFFFFFFFull) {
        win->kind = CL_BAD;
        ps2_log("vfs: %s's '%s' is %llu bytes; a disc file cannot be over 4 GB",
                win->owner, win->path, (unsigned long long)win->size);
        return;
    }
    if (n) {
        ps2_log("vfs: %s replaces '%s' (%llu bytes, was %u)", win->owner,
                n->f.name, (unsigned long long)win->size, n->disc_size);
    } else {
        u32 entry;
        n = add_node(win->path, 0);
        if (!n) { win->kind = CL_BAD; return; }
        if (!cdp_patch_name(win->path, &entry))
            ps2_log("vfs: %s adds '%s', which is not a file the game has -- a new "
                    "file that code mods can read.  If it was meant to replace one "
                    "of the game's files, the name is wrong.", win->owner, win->path);
    }
    n->src = SRC_HOST;
    n->host = win->host;
    n->f.size = (u32)win->size;
    n->claim = win;
    win->won = 1;
}

/* BIN/DATA.CDP.<4 digits> (a whole entry) -> 1, BIN/DATA.CDP.<4 digits>.PARTS
   (some parts of it, see cdp_rebuilt) -> 2, anything else -> 0 */
static int cdp_patch_name(const char *name, u32 *entry) {
    static const char pre[] = "BIN/DATA.CDP.", suf[] = ".PARTS";
    size_t len = strlen(name), k, base = sizeof pre - 1u;
    u32 v = 0;
    int kind;
    if (len == base + 4u) kind = 1;
    else if (len == base + 4u + sizeof suf - 1u) kind = 2;
    else return 0;
    for (k = 0; k < base; k++)
        if (upc(name[k]) != pre[k]) return 0;
    for (k = base; k < base + 4u; k++) {
        if (name[k] < '0' || name[k] > '9') return 0;
        v = v * 10u + (u32)(name[k] - '0');
    }
    if (kind == 2)
        for (k = 0; k < sizeof suf - 1u; k++)
            if (upc(name[base + 4u + k]) != suf[k]) return 0;
    *entry = v;
    return kind;
}

static void cdp_patches_build(void) {
    vfs_node *cdp = lookup("BIN/DATA.CDP"), *tbl = lookup("BIN/DATA.TBL");
    u32 nent, i, n = 0;
    u64 off;
    if (!cdp || !tbl || tbl->f.size < 8u) return;
    for (i = 0; i < nnodes; i++) {
        u32 e;
        if (cdp_patch_name(nodes[i]->f.name, &e)) n++;
    }
    if (!n) return;
    cdp_patches = (cdp_patch *)calloc(n, sizeof *cdp_patches);
    tbl_mem = (u8 *)malloc(tbl->f.size);
    if (!cdp_patches || !tbl_mem) ps2_fatal("vfs: out of memory patching DATA.CDP");
    node_read_base(tbl, 0, tbl->f.size, tbl_mem);
    nent = tbl->f.size / 8u;
    cdp_orig_bytes = (u64)sectors_of(cdp->f.size) * SECTOR;
    off = cdp_orig_bytes;
    for (i = 0; i < nnodes; i++) {
        u32 e, sec, size, k;
        int kind = cdp_patch_name(nodes[i]->f.name, &e);
        cdp_patch *p;
        if (!kind) continue;
        if (e >= nent) {
            ps2_log("vfs: %s -- DATA.TBL has only %u entries; ignored", nodes[i]->f.name, nent);
            continue;
        }
        for (k = 0; k < cdp_npatches; k++)
            if (cdp_patches[k].entry == e) break;
        if (k < cdp_npatches) {
            ps2_log("vfs: %s -- entry %u is already replaced; ignored", nodes[i]->f.name, e);
            continue;
        }
        p = &cdp_patches[cdp_npatches];
        size = nodes[i]->f.size;
        if (kind == 2) {
            u8 h[16];
            node_read_base(nodes[i], 0, 16, h);
            if (memcmp(h, "AC4P", 4) || (rd32(h + 4) != 1u && rd32(h + 4) != 2u)) {
                ps2_log("vfs: %s is not a parts patch; ignored", nodes[i]->f.name);
                continue;
            }
            p->parts = (int)rd32(h + 4);
            p->orig_sec = rd32(tbl_mem + e * 8u);
            p->orig_size = rd32(tbl_mem + e * 8u + 4u);
            size = rd32(h + 8);
        }
        sec = (u32)(off / SECTOR);
        memcpy(tbl_mem + e * 8u, &sec, 4);
        memcpy(tbl_mem + e * 8u + 4u, &size, 4);
        p->off = off;
        p->size = size;
        p->entry = e;
        p->src = nodes[i];
        cdp_npatches++;
        off += (u64)sectors_of(size) * SECTOR;
    }
    if (!cdp_npatches) return;
    if (off > 0xFFFFF000ull) ps2_fatal("vfs: DATA.CDP with its patches would pass 4 GB");
    cdp_node = cdp;
    tbl_node = tbl;
    cdp->f.size = (u32)off;
    ps2_log("vfs: %u DATA.CDP entries replaced by mods (%.1f MB appended)",
            cdp_npatches, (double)(off - cdp_orig_bytes) / 1048576.0);
}

static void resolve_claims(void) {
    vfs_claim **run = (vfs_claim **)malloc((nclaims ? nclaims : 1u) * sizeof *run);
    if (!run) ps2_fatal("vfs: out of memory resolving mods");
    memcpy(run, claims, nclaims * sizeof *run);
    if (nclaims > 1) qsort(run, nclaims, sizeof *run, claim_ptr_cmp);
    for (u32 i = 0; i < nclaims;) {
        u32 j = i;
        while (j < nclaims && !icmp(run[j]->path, run[i]->path)) j++;
        settle_file(run, i, j, lookup(run[i]->path));
        i = j;
    }
    free(run);
}

int ps2_vfs_open(const char *path) {
    u64 size;
    int is_dir;
    if (vfs_opened) return 0;
    if (!path || !*path || host_stat(path, &size, &is_dir) != 0) {
        ps2_log("vfs: no disc at '%s'", path ? path : "");
        return -1;
    }
    snprintf(vfs_path, sizeof vfs_path, "%s", path);
    if (is_dir) {
        vfs_is_tree = 1;
        tree_scan(path, "", 0);
    } else {
        u8 pvd[SECTOR];
        u32 root_lsn, root_size;
        image_fp = fopen(path, "rb");
        if (!image_fp || image_sector(16, pvd) != 0
            || memcmp(pvd + 1, "CD001", 5) != 0) {
            ps2_log("vfs: '%s' is not an ISO9660 image", path);
            if (image_fp) fclose(image_fp);
            image_fp = NULL;
            return -1;
        }
        memcpy(&root_lsn, pvd + 156 + 2, 4);
        memcpy(&root_size, pvd + 156 + 10, 4);
        image_scan_dir(root_lsn, root_size, "", 0);
    }
    if (!nnodes) {
        ps2_log("vfs: '%s' has no files", path);
        return -1;
    }
    resolve_claims();
    cdp_patches_build();

    {
        u32 lsn = FIRST_LSN;
        for (u32 i = 0; i < nnodes; i++) {
            nodes[i]->f.lsn = lsn;
            lsn += sectors_of(nodes[i]->f.size);
        }
        end_lsn = lsn;
    }
    vfs_opened = 1;
    ps2_log("vfs: %s '%s', %u files, sectors %u..%u", vfs_is_tree
            ? "extracted tree" : "disc image", path, nnodes, FIRST_LSN, end_lsn);
    return 0;
}

void ps2_vfs_owner_stats(const char *owner, u32 *files) {
    u32 n = 0;
    for (u32 i = 0; i < nclaims; i++) {
        if (!owner || strcmp(claims[i]->owner, owner)) continue;
        n += claims[i]->won;
    }
    if (files) *files = n;
}

void ps2_vfs_report(void) {
    u32 replaced = 0, added = 0, bad = 0;
    if (!vfs_opened) return;
    for (u32 i = 0; i < nclaims; i++) {
        const vfs_claim *c = claims[i];
        if (c->kind == CL_DISC && c->won) replaced++;
        else if (c->kind == CL_NEW && c->won) added++;
        else if (c->kind == CL_BAD) bad++;
    }
    ps2_log("---- filesystem ----");
    ps2_log("   %s '%s': %u files, sectors %u..%u", vfs_is_tree
            ? "extracted tree" : "disc image", vfs_path, nnodes, FIRST_LSN,
            end_lsn);
    ps2_log("   %llu sector(s) and %llu byte(s) read, %llu read(s) from mod files",
            (unsigned long long)reads_sectors, (unsigned long long)reads_bytes,
            (unsigned long long)reads_from_mods);
    if (!nclaims) return;
    for (u32 i = 0; i < nnodes; i++)
        if (nodes[i]->claim)
            ps2_log("   %-8s %-32s %10u bytes  from %s",
                    nodes[i]->claim->kind == CL_NEW ? "new" : "replaced",
                    nodes[i]->f.name, nodes[i]->f.size, nodes[i]->claim->owner);
    if (replaced || added)
        ps2_log("   %u whole file(s) replaced, %u new file(s)", replaced, added);
    if (nconflicts)
        ps2_log("   %u conflict(s) between mods, each named above", nconflicts);
    if (bad)
        ps2_log("   %u mod file(s) could not be placed, each named above", bad);
}
