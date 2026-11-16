/* Button glyphs for the device being played with: the game only knows the
   PlayStation symbols (cross, circle, square, triangle).  Each is shown as
   the control the player actually presses for it:
     PlayStation pad  the symbol of the face button bound to it
     Xbox / other pad A, B, X or Y (by position: south, east, west, north)
     keyboard         the bound key's letter (default X, S, Z, A)
   button_glyphs (settings): 0 follows the last device used, 1 PlayStation,
   2 Xbox, 3 keyboard.

   Where the symbols are drawn:
   - the menus' block font, a 256x80 4-bit image the game uploads every field
     (GS 0x3D4000, row 64: square, triangle, cross, circle), and the menu bar
     icons, a 120x18 4-bit image beside it (up, down, left, right, cross,
     triangle): both are rewritten as they are uploaded (ps2_gs.c), with the
     letters of that same block font;
   - the subtitle / help font (32x32 4-bit glyphs in EE RAM, text codes 8..11
     = circle, triangle, square, cross -- tools/ac4_text.py names them
     SQUARE, CROSS, TRIANGLE, CIRCLE, which is only a label):
     the letter glyph is copied over the symbol glyph, and back. */
#include "ps2_runtime.h"
#include "ps2_settings.h"
#include <SDL3/SDL.h>
#include <string.h>

enum { G_SQUARE, G_TRIANGLE, G_CROSS, G_CIRCLE, G_N };
enum { ST_PS, ST_XBOX, ST_KEYS };

static int last_style = ST_PS;              /* from the last input (auto) */
static const int act_of[G_N] = { PS2_ACT_SQUARE, PS2_ACT_TRIANGLE, PS2_ACT_CROSS, PS2_ACT_CIRCLE };

void ps2_glyphs_note_input(int keyboard, int gamepad_type) {
    if (keyboard) { last_style = ST_KEYS; return; }
    switch (gamepad_type) {
    case SDL_GAMEPAD_TYPE_PS3: case SDL_GAMEPAD_TYPE_PS4: case SDL_GAMEPAD_TYPE_PS5:
        last_style = ST_PS; break;
    default:
        last_style = ST_XBOX; break;
    }
}

static int style(void) {
    switch (ps2_cfg.button_glyphs) {
    case 1: return ST_PS;
    case 2: return ST_XBOX;
    case 3: return ST_KEYS;
    default: return last_style;
    }
}

/* what glyph g shows now: < 0 a PlayStation symbol (-1 - G_*), else a letter */
static int label(int g) {
    int st = style(), a = act_of[g];
    if (st == ST_KEYS) {
        int sc = ps2_cfg.key[a][0];
        const char *n = sc ? SDL_GetScancodeName((SDL_Scancode)sc) : NULL;
        if (n && n[0]) {
            char c = n[0];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }
        return -1 - g;
    }
    {   int b = ps2_cfg.pad[a][0] - 1;     /* the bound SDL button, or < 0 */
        int face = -1;
        if (b == SDL_GAMEPAD_BUTTON_SOUTH) face = G_CROSS;
        else if (b == SDL_GAMEPAD_BUTTON_EAST) face = G_CIRCLE;
        else if (b == SDL_GAMEPAD_BUTTON_WEST) face = G_SQUARE;
        else if (b == SDL_GAMEPAD_BUTTON_NORTH) face = G_TRIANGLE;
        if (face < 0) face = g;              /* bound elsewhere: keep the symbol */
        if (st == ST_PS) return -1 - face;
        return face == G_CROSS ? 'A' : face == G_CIRCLE ? 'B' : face == G_SQUARE ? 'X' : 'Y';
    }
}

/* ---- 4-bit images ---- */
static int px4(const u8 *img, int w, int x, int y) {
    u8 b = img[(y * w + x) >> 1];
    return (x & 1) ? b >> 4 : b & 15;
}
static void put4(u8 *img, int w, int x, int y, int v) {
    u8 *b = &img[(y * w + x) >> 1];
    *b = (x & 1) ? (u8)((*b & 0x0F) | (v << 4)) : (u8)((*b & 0xF0) | v);
}

#define BIG_W 256
#define BIG_H 80
#define NAV_W 120
#define NAV_H 18
static u8 big_orig[BIG_W * BIG_H / 2];
static int have_big;
/* the four symbols of the block font, row 64 (x0, x1) */
static const int big_sym[G_N][2] = { {32, 44}, {46, 60}, {62, 72}, {74, 86} };
#define BIG_SYM_Y0 64
#define BIG_SYM_Y1 79
/* the menu bar's cross and triangle */
static const int nav_sym[2][3] = { {G_CROSS, 80, 98}, {G_TRIANGLE, 100, 118} };

/* draw the source box (from the original block font) scaled into the target box */
static void blit_scaled(u8 *dst, int dw, int tx0, int ty0, int tw, int th,
                        int sx0, int sy0, int sw, int sh, int vmax) {
    int smax = 1;
    for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++) {
            int v = px4(big_orig, BIG_W, sx0 + x, sy0 + y);
            if (v > smax) smax = v;
        }
    for (int y = 0; y < th; y++)
        for (int x = 0; x < tw; x++) {
            int sxx = sx0 + x * sw / tw, syy = sy0 + y * sh / th;
            int v = px4(big_orig, BIG_W, sxx, syy) * vmax / smax;
            put4(dst, dw, tx0 + x, ty0 + y, v > 15 ? 15 : v);
        }
}

/* the ink box of a letter's 16x16 cell in the block font */
static void letter_box(int c, int *x0, int *y0, int *w, int *h) {
    int cx = (c & 15) * 16, cy = ((c - 0x20) >> 4) * 16;
    int lx = 16, ly = 16, hx = -1, hy = -1;
    for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            if (px4(big_orig, BIG_W, cx + x, cy + y)) {
                if (x < lx) lx = x;
                if (y < ly) ly = y;
                if (x > hx) hx = x;
                if (y > hy) hy = y;
            }
    if (hx < 0) { lx = ly = 0; hx = hy = 12; }
    *x0 = cx + lx; *y0 = cy + ly; *w = hx - lx + 1; *h = hy - ly + 1;
}

static void draw_label(u8 *dst, int dw, int x0, int x1, int y0, int y1, int lab,
                       int keep_symbol_from_big, int vmax) {
    int tw = x1 - x0, th = y1 - y0;
    for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++) put4(dst, dw, x, y, 0);
    if (lab < 0) {                           /* a PlayStation symbol */
        int g = -1 - lab;
        if (keep_symbol_from_big) {
            int sx0 = big_sym[g][0], sw = big_sym[g][1] - big_sym[g][0];
            blit_scaled(dst, dw, x0, y0, tw, th, sx0, BIG_SYM_Y0, sw, BIG_SYM_Y1 - BIG_SYM_Y0, vmax);
        }
        return;
    }
    {   int lx, ly, lw, lh;
        letter_box(lab, &lx, &ly, &lw, &lh);
        /* keep the letter's shape: as tall as the box allows, width to scale */
        int h = th - 2 > lh ? lh : th - 2, w = lw * h / (lh ? lh : 1);
        if (w > tw) { w = tw; }
        blit_scaled(dst, dw, x0 + (tw - w) / 2, y0 + (th - h) / 2, w, h, lx, ly, lw, lh, vmax);
    }
}

static u32 fnv(const u8 *p, u32 n) {
    u32 h = 2166136261u;
    for (u32 i = 0; i < n; i++) h = (h ^ p[i]) * 16777619u;
    return h;
}

/* called by ps2_gs.c for an IMAGE upload that is about to be written: returns
   the data to use instead, or NULL to keep it */
const u8 *ps2_glyphs_upload(u32 dbp, u32 psm, u32 w, u32 h, u32 x, u32 y,
                            const u8 *src, u32 bytes) {
    /* the bar is 1080 bytes, sent as 68 qwords (1088): keep the padding */
    static u8 big_new[BIG_W * BIG_H / 2], nav_new[(NAV_W * NAV_H / 2 + 15) & ~15];
    const u32 nav_bytes = NAV_W * NAV_H / 2;
    static u32 big_hash, nav_hash;
    if (dbp != 0x3D4000u || psm != 20u || x != 0) return NULL;
    if (w == BIG_W && h == BIG_H && y == 0 && bytes == sizeof big_orig) {
        if (!have_big) {
            /* the stock block font: the PS symbols where they belong */
            if (fnv(src, bytes) != 0x89A6764Fu) return NULL;
            memcpy(big_orig, src, bytes);
            big_hash = fnv(src, bytes);
            have_big = 1;
        } else if (fnv(src, bytes) != big_hash) return NULL;
        memcpy(big_new, big_orig, sizeof big_new);
        for (int g = 0; g < G_N; g++) {
            int lab = label(g);
            if (lab == -1 - g) continue;     /* unchanged */
            int vmax = 0;
            for (int yy = BIG_SYM_Y0; yy < BIG_SYM_Y1; yy++)
                for (int xx = big_sym[g][0]; xx < big_sym[g][1]; xx++) {
                    int v = px4(big_orig, BIG_W, xx, yy);
                    if (v > vmax) vmax = v;
                }
            draw_label(big_new, BIG_W, big_sym[g][0], big_sym[g][1], BIG_SYM_Y0, BIG_SYM_Y1,
                       lab, 1, vmax ? vmax : 15);
        }
        return big_new;
    }
    if (w == NAV_W && h == NAV_H && y == 160 && bytes == sizeof nav_new && have_big) {
        u32 hsh = fnv(src, nav_bytes);
        if (!nav_hash) {
            if (getenv("PS2_GLYPHS_LOG")) ps2_log("glyphs: nav bar %08x", hsh);
            if (hsh != 0xED3CB539u) return NULL;
            nav_hash = hsh;
        } else if (hsh != nav_hash) return NULL;
        memcpy(nav_new, src, sizeof nav_new);
        for (int k = 0; k < 2; k++) {
            int g = nav_sym[k][0], lab = label(g);
            if (lab == -1 - g) continue;
            if (lab < 0) {                   /* another PS symbol: from the bar itself */
                int from = (-1 - lab) == G_CROSS ? 0 : (-1 - lab) == G_TRIANGLE ? 1 : -1;
                if (from < 0) continue;      /* the bar has no circle / square */
                for (int yy = 0; yy < NAV_H; yy++)
                    for (int xx = 0; xx < 18; xx++)
                        put4(nav_new, NAV_W, nav_sym[k][1] + xx, yy,
                             px4(src, NAV_W, nav_sym[from][1] + xx, yy));
                continue;
            }
            draw_label(nav_new, NAV_W, nav_sym[k][1], nav_sym[k][2], 0, NAV_H, lab, 0, 15);
        }
        return nav_new;
    }
    return NULL;
}

/* ---- the subtitle / help font in EE RAM ---- */
#define SUB_SRC   0x003B0C70u      /* in the executable's data (file 0x2B1C70) */
#define SUB_COPY  0x003C2FF0u      /* where the game copies it */
#define SUB_STEP  0x210u
/* the font's glyph for text code c is glyph c - 1: the symbols are glyphs
   7 circle, 8 triangle, 9 square, 0x0A cross (text codes 08..0B), the letters
   A..Z glyphs 0x26..0x3F, the digits 0x1C..0x25 */
static const int sub_code[G_N] = { 0x09, 0x08, 0x0A, 0x07 };   /* square, triangle, cross, circle */
static u8 sub_orig[G_N][SUB_STEP];
static int sub_saved;

static int sub_code_of(int lab) {
    if (lab < 0) return sub_code[-1 - lab];
    if (lab >= 'A' && lab <= 'Z') return lab - 0x1A - 1;
    if (lab >= '0' && lab <= '9') return lab - 0x13 - 1;
    return -1;
}

/* once a field (the renderer's field service): keep codes 8..11 showing the
   right glyph, in both the executable's copy and the game's */
void ps2_glyphs_service(void) {
    static int last_sig = -1;
    int sig = 0;
    if (!ps2_ram) return;
    if (!sub_saved) {
        for (int g = 0; g < G_N; g++)
            memcpy(sub_orig[g], ps2_ram + SUB_SRC + (u32)sub_code[g] * SUB_STEP, SUB_STEP);
        sub_saved = 1;
    }
    for (int g = 0; g < G_N; g++) sig = sig * 131 + label(g) + 300;
    {   /* the game copies the font when a screen needs it: check now and then */
        static u32 tick;
        if (sig == last_sig && (++tick % 30u) != 0) return;
    }
    last_sig = sig;
    for (int g = 0; g < G_N; g++) {
        int lab = label(g), code = sub_code_of(lab);
        const u8 *from;
        u8 *to_src = ps2_ram + SUB_SRC + (u32)sub_code[g] * SUB_STEP;
        u8 *to_copy = ps2_ram + SUB_COPY + (u32)sub_code[g] * SUB_STEP;
        if (code < 0) continue;
        if (lab < 0) from = sub_orig[-1 - lab];
        else from = ps2_ram + SUB_SRC + (u32)code * SUB_STEP;
        if (memcmp(to_src, from, SUB_STEP)) memmove(to_src, from, SUB_STEP);
        /* the game's copy, only where it holds this font */
        if (!memcmp(ps2_ram + SUB_COPY + 0x26u * SUB_STEP, ps2_ram + SUB_SRC + 0x26u * SUB_STEP,
                    SUB_STEP) && memcmp(to_copy, from, SUB_STEP))
            memmove(to_copy, from, SUB_STEP);
    }
}
