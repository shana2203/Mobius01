/* A small gzip/DEFLATE (RFC 1951/1952) decoder for the memory image, which the
   installer stores compressed in <install>\gamedata\ps2_image.bin (about 4 MB
   down to under 1 MB).  Stored, fixed and dynamic Huffman blocks. */
#include <stddef.h>
#include <string.h>

typedef unsigned char  u8;
typedef unsigned short u16;
typedef unsigned int   u32;

typedef struct {
    const u8 *in; size_t in_n, pos;
    u32 bits; int nbits;
    u8 *out; size_t out_n, out_pos;
    int err;
} inf_state;

typedef struct { u16 count[16]; u16 sym[288]; } inf_huff;

static int getbit(inf_state *s) {
    int b;
    if (!s->nbits) {
        if (s->pos >= s->in_n) { s->err = 1; return 0; }
        s->bits = s->in[s->pos++];
        s->nbits = 8;
    }
    b = (int)(s->bits & 1u);
    s->bits >>= 1;
    s->nbits--;
    return b;
}

static u32 getbits(inf_state *s, int n) {
    u32 v = 0;
    int i;
    for (i = 0; i < n; i++) v |= (u32)getbit(s) << i;
    return v;
}

static int build(inf_huff *h, const u8 *len, int n) {
    u16 offs[16];
    int i;
    memset(h->count, 0, sizeof h->count);
    for (i = 0; i < n; i++) h->count[len[i]]++;
    h->count[0] = 0;
    offs[1] = 0;
    for (i = 1; i < 15; i++) offs[i + 1] = (u16)(offs[i] + h->count[i]);
    for (i = 0; i < n; i++) if (len[i]) h->sym[offs[len[i]]++] = (u16)i;
    return 0;
}

static int decode(inf_state *s, const inf_huff *h) {
    int code = 0, first = 0, index = 0, len;
    for (len = 1; len < 16; len++) {
        int count = h->count[len];
        code |= getbit(s);
        if (code - count < first) return h->sym[index + (code - first)];
        index += count;
        first = (first + count) << 1;
        code <<= 1;
        if (s->err) return -1;
    }
    s->err = 1;
    return -1;
}

static const u16 len_base[29] = { 3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,
                                  67,83,99,115,131,163,195,227,258 };
static const u8  len_extra[29] = { 0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0 };
static const u16 dist_base[30] = { 1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,
                                   1025,1537,2049,3073,4097,6145,8193,12289,16385,24577 };
static const u8  dist_extra[30] = { 0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13 };

static int codes(inf_state *s, const inf_huff *lit, const inf_huff *dist) {
    for (;;) {
        int sym = decode(s, lit);
        if (s->err || sym < 0) return -1;
        if (sym < 256) {
            if (s->out_pos >= s->out_n) return -1;
            s->out[s->out_pos++] = (u8)sym;
        } else if (sym == 256) {
            return 0;
        } else {
            size_t len, d;
            sym -= 257;
            if (sym >= 29) return -1;
            len = len_base[sym] + getbits(s, len_extra[sym]);
            sym = decode(s, dist);
            if (s->err || sym < 0 || sym >= 30) return -1;
            d = dist_base[sym] + getbits(s, dist_extra[sym]);
            if (d > s->out_pos || s->out_pos + len > s->out_n) return -1;
            while (len--) { s->out[s->out_pos] = s->out[s->out_pos - d]; s->out_pos++; }
        }
    }
}

static int block_stored(inf_state *s) {
    size_t n;
    s->nbits = 0;   /* to a byte boundary */
    if (s->pos + 4 > s->in_n) return -1;
    n = (size_t)s->in[s->pos] | ((size_t)s->in[s->pos + 1] << 8);
    if ((n ^ 0xFFFFu) != ((size_t)s->in[s->pos + 2] | ((size_t)s->in[s->pos + 3] << 8))) return -1;
    s->pos += 4;
    if (s->pos + n > s->in_n || s->out_pos + n > s->out_n) return -1;
    memcpy(s->out + s->out_pos, s->in + s->pos, n);
    s->pos += n;
    s->out_pos += n;
    return 0;
}

static int block_fixed(inf_state *s) {
    static inf_huff lit, dist;
    static int ready;
    if (!ready) {
        u8 l[288];
        int i;
        for (i = 0; i < 144; i++) l[i] = 8;
        for (; i < 256; i++) l[i] = 9;
        for (; i < 280; i++) l[i] = 7;
        for (; i < 288; i++) l[i] = 8;
        build(&lit, l, 288);
        for (i = 0; i < 30; i++) l[i] = 5;
        build(&dist, l, 30);
        ready = 1;
    }
    return codes(s, &lit, &dist);
}

static int block_dynamic(inf_state *s) {
    static const u8 order[19] = { 16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };
    u8 lens[320];
    inf_huff lencode, lit, dist;
    int nlen = (int)getbits(s, 5) + 257, ndist = (int)getbits(s, 5) + 1;
    int ncode = (int)getbits(s, 4) + 4, i = 0;
    if (nlen > 286 || ndist > 30) return -1;
    memset(lens, 0, sizeof lens);
    for (i = 0; i < ncode; i++) lens[order[i]] = (u8)getbits(s, 3);
    build(&lencode, lens, 19);
    memset(lens, 0, sizeof lens);
    i = 0;
    while (i < nlen + ndist) {
        int sym = decode(s, &lencode), rep;
        u8 v = 0;
        if (s->err || sym < 0) return -1;
        if (sym < 16) { lens[i++] = (u8)sym; continue; }
        if (sym == 16) {
            if (!i) return -1;
            v = lens[i - 1];
            rep = 3 + (int)getbits(s, 2);
        } else if (sym == 17) {
            rep = 3 + (int)getbits(s, 3);
        } else {
            rep = 11 + (int)getbits(s, 7);
        }
        if (i + rep > nlen + ndist) return -1;
        while (rep--) lens[i++] = v;
    }
    build(&lit, lens, nlen);
    build(&dist, lens + nlen, ndist);
    return codes(s, &lit, &dist);
}

/* gzip in -> out; returns the bytes written, or -1 */
long ps2_gunzip(const u8 *in, size_t in_n, u8 *out, size_t out_n) {
    inf_state s;
    u8 flg;
    int last;
    memset(&s, 0, sizeof s);
    if (in_n < 18 || in[0] != 0x1F || in[1] != 0x8B || in[2] != 8) return -1;
    flg = in[3];
    s.pos = 10;
    if (flg & 4) { if (s.pos + 2 > in_n) return -1; s.pos += 2 + ((size_t)in[s.pos] | ((size_t)in[s.pos + 1] << 8)); }
    if (flg & 8) { while (s.pos < in_n && in[s.pos]) s.pos++; s.pos++; }
    if (flg & 16) { while (s.pos < in_n && in[s.pos]) s.pos++; s.pos++; }
    if (flg & 2) s.pos += 2;
    if (s.pos >= in_n) return -1;
    s.in = in; s.in_n = in_n; s.out = out; s.out_n = out_n;
    do {
        int type, r;
        last = getbit(&s);
        type = (int)getbits(&s, 2);
        if (s.err) return -1;
        if (type == 0) r = block_stored(&s);
        else if (type == 1) r = block_fixed(&s);
        else if (type == 2) r = block_dynamic(&s);
        else r = -1;
        if (r || s.err) return -1;
    } while (!last);
    return (long)s.out_pos;
}
