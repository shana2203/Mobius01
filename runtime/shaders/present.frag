#version 450
layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 out_color;
layout(set = 0, binding = 0) uniform sampler2D src;
layout(push_constant) uniform PC {
    vec4 map;
    vec4 bound;
    vec4 opts;
    vec4 grade;
} pc;

vec2 texel;
vec2 lo, hi;
vec2 footprint;
bool shrinking;

vec3 tap(vec2 uv) { return texture(src, clamp(uv, lo, hi)).rgb; }

vec3 filtered(vec2 uv, vec2 dtex) {
    vec2 size = vec2(textureSize(src, 0));
    if (pc.opts.x < 0.5) {
        ivec2 p = ivec2(floor(clamp(uv, lo, hi) * size));
        return texelFetch(src, p, 0).rgb;
    }
    if (pc.opts.x > 1.5) {
        vec2 t = uv * size - 0.5;
        vec2 scale = max(1.0 / max(dtex, vec2(1e-6)), vec2(1.0));
        vec2 f = fract(t);
        vec2 region = 0.5 - 0.5 / scale;
        vec2 d = f - 0.5;
        f = (d - clamp(d, -region, region)) * scale + 0.5;
        return tap((floor(t) + f + 0.5) / size);
    }
    return tap(uv);
}

vec3 downsample(vec2 uv, vec2 dtex, vec2 one) {
    ivec2 n = ivec2(clamp(ceil(dtex - 0.01), vec2(1.0), vec2(4.0)));
    vec2 step = dtex / vec2(n) * one;
    vec2 first = uv - 0.5 * dtex * one + 0.5 * step;
    vec3 sum = vec3(0.0);
    for (int y = 0; y < n.y; y++)
        for (int x = 0; x < n.x; x++)
            sum += tap(first + vec2(float(x), float(y)) * step);
    return sum / float(n.x * n.y);
}

// ---- AMD FidelityFX Super Resolution 1.0 -----------------------------------
// EASU (edge-adaptive spatial upsampling) and RCAS (robust contrast-adaptive
// sharpening), after AMD's FSR 1.0 algorithm (FidelityFX, MIT licence).
// EASU reads 12 source texels around the output position, estimates the local
// edge direction and length from luma gradients at the four nearest texels,
// and weights the taps with a Lanczos-2 approximation stretched along the
// edge; the result is clamped to the four nearest texels against ringing.

float fsr_luma(vec3 c) { return c.g + 0.5 * (c.r + c.b); }

void fsr_set(inout vec2 dir, inout float len, float w,
             float lA, float lB, float lC, float lD, float lE) {
    // lA above, lB left, lC centre, lD right, lE below
    float dc = lD - lC, cb = lC - lB;
    float lenX = max(abs(dc), abs(cb));
    lenX = lenX > 0.0 ? 1.0 / lenX : 0.0;
    float dirX = lD - lB;
    lenX = clamp(abs(dirX) * lenX, 0.0, 1.0);
    lenX *= lenX;
    float ec = lE - lC, ca = lC - lA;
    float lenY = max(abs(ec), abs(ca));
    lenY = lenY > 0.0 ? 1.0 / lenY : 0.0;
    float dirY = lE - lA;
    lenY = clamp(abs(dirY) * lenY, 0.0, 1.0);
    lenY *= lenY;
    dir += vec2(dirX, dirY) * w;
    len += (lenX + lenY) * w;
}

void fsr_tap(inout vec3 acc, inout float wsum, vec2 off, vec2 dir, vec2 len2,
             float lob, float clp, vec3 c) {
    vec2 v = vec2(off.x * dir.x + off.y * dir.y, off.x * -dir.y + off.y * dir.x);
    v *= len2;
    float d2 = min(dot(v, v), clp);
    float wB = 0.4 * d2 - 1.0;          // (2/5) x^2 - 1
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = 1.5625 * wB - 0.5625;          // (25/16) wB - (25/16 - 1)
    float w = wB * wA;
    acc += c * w;
    wsum += w;
}

vec3 fsr_easu(vec2 uv) {
    vec2 size = vec2(textureSize(src, 0));
    vec2 pp = uv * size - 0.5;
    vec2 fp = floor(pp);
    pp -= fp;
    vec2 base = (fp + 0.5) / size, one = 1.0 / size;
    //      b c
    //    e f g h
    //    i j k l
    //      n o
    vec3 b = tap(base + vec2( 0.0, -1.0) * one), c = tap(base + vec2( 1.0, -1.0) * one);
    vec3 e = tap(base + vec2(-1.0,  0.0) * one), f = tap(base);
    vec3 g = tap(base + vec2( 1.0,  0.0) * one), h = tap(base + vec2( 2.0,  0.0) * one);
    vec3 i = tap(base + vec2(-1.0,  1.0) * one), j = tap(base + vec2( 0.0,  1.0) * one);
    vec3 k = tap(base + vec2( 1.0,  1.0) * one), l = tap(base + vec2( 2.0,  1.0) * one);
    vec3 n = tap(base + vec2( 0.0,  2.0) * one), o = tap(base + vec2( 1.0,  2.0) * one);
    float bL = fsr_luma(b), cL = fsr_luma(c), eL = fsr_luma(e), fL = fsr_luma(f);
    float gL = fsr_luma(g), hL = fsr_luma(h), iL = fsr_luma(i), jL = fsr_luma(j);
    float kL = fsr_luma(k), lL = fsr_luma(l), nL = fsr_luma(n), oL = fsr_luma(o);

    vec2 dir = vec2(0.0);
    float len = 0.0;
    fsr_set(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    fsr_set(dir, len, pp.x * (1.0 - pp.y),         cL, fL, gL, hL, kL);
    fsr_set(dir, len, (1.0 - pp.x) * pp.y,         fL, iL, jL, kL, nL);
    fsr_set(dir, len, pp.x * pp.y,                 gL, jL, kL, lL, oL);

    float dirR = dot(dir, dir);
    if (dirR < 1.0 / 32768.0) dir = vec2(1.0, 0.0);
    else dir *= inversesqrt(dirR);
    len = len * 0.5;
    len *= len;
    float stretch = dot(dir, dir) / max(abs(dir.x), abs(dir.y));
    vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = 1.0 / lob;

    vec3 acc = vec3(0.0);
    float wsum = 0.0;
    fsr_tap(acc, wsum, vec2( 0.0, -1.0) - pp, dir, len2, lob, clp, b);
    fsr_tap(acc, wsum, vec2( 1.0, -1.0) - pp, dir, len2, lob, clp, c);
    fsr_tap(acc, wsum, vec2(-1.0,  1.0) - pp, dir, len2, lob, clp, i);
    fsr_tap(acc, wsum, vec2( 0.0,  1.0) - pp, dir, len2, lob, clp, j);
    fsr_tap(acc, wsum, vec2( 0.0,  0.0) - pp, dir, len2, lob, clp, f);
    fsr_tap(acc, wsum, vec2(-1.0,  0.0) - pp, dir, len2, lob, clp, e);
    fsr_tap(acc, wsum, vec2( 1.0,  1.0) - pp, dir, len2, lob, clp, k);
    fsr_tap(acc, wsum, vec2( 2.0,  1.0) - pp, dir, len2, lob, clp, l);
    fsr_tap(acc, wsum, vec2( 2.0,  0.0) - pp, dir, len2, lob, clp, h);
    fsr_tap(acc, wsum, vec2( 1.0,  0.0) - pp, dir, len2, lob, clp, g);
    fsr_tap(acc, wsum, vec2( 1.0,  2.0) - pp, dir, len2, lob, clp, o);
    fsr_tap(acc, wsum, vec2( 0.0,  2.0) - pp, dir, len2, lob, clp, n);

    vec3 mn = min(min(f, g), min(j, k)), mx = max(max(f, g), max(j, k));
    vec3 res = wsum != 0.0 ? acc / wsum : f;
    return clamp(res, mn, mx);
}

bool fsr_on() { return pc.opts.x > 2.5 && !shrinking; }

// Postprocessing must see the same resolved image as the centre sample.
// Sampling raw supersampled texels at the edges can reintroduce aliasing.
vec3 resolved(vec2 uv) {
    vec2 one = 1.0 / vec2(textureSize(src, 0));
    if (fsr_on()) return fsr_easu(uv);
    return shrinking && pc.opts.x > 0.5 ? downsample(uv, footprint, one)
                                       : filtered(uv, footprint);
}

// RCAS: sharpen with the strongest negative lobe that cannot clip the
// cross of upscaled neighbours (one output pixel away), so it never rings.
vec3 fsr_rcas(vec2 uv, vec3 e, float amount, vec2 px) {
    vec3 b = resolved(uv + vec2(0.0, -px.y));
    vec3 d = resolved(uv + vec2(-px.x, 0.0));
    vec3 f = resolved(uv + vec2( px.x, 0.0));
    vec3 h = resolved(uv + vec2(0.0,  px.y));
    vec3 mn4 = min(min(b, d), min(f, h));
    vec3 mx4 = max(max(b, d), max(f, h));
    vec3 hitMin = mn4 / max(4.0 * mx4, vec3(1e-5));
    vec3 hitMax = (1.0 - mx4) / min(4.0 * mn4 - 4.0, vec3(-1e-5));
    vec3 lobeRGB = max(-hitMin, hitMax);
    float sharp = exp2(-2.0 * (1.0 - amount));       // slider 1 = 0 stops
    float lobe = max(-(0.25 - 1.0 / 16.0), min(max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b)), 0.0)) * sharp;
    return clamp((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0), 0.0, 1.0);
}

vec3 fxaa(vec2 uv, vec3 rgbM) {
    const vec3 L = vec3(0.299, 0.587, 0.114);
    vec3 rgbNW = resolved(uv + vec2(-1.0, -1.0) * texel);
    vec3 rgbNE = resolved(uv + vec2( 1.0, -1.0) * texel);
    vec3 rgbSW = resolved(uv + vec2(-1.0,  1.0) * texel);
    vec3 rgbSE = resolved(uv + vec2( 1.0,  1.0) * texel);
    float lNW = dot(rgbNW, L), lNE = dot(rgbNE, L);
    float lSW = dot(rgbSW, L), lSE = dot(rgbSE, L), lM = dot(rgbM, L);
    float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
    float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
    if (lMax - lMin < max(1.0 / 32.0, lMax / 8.0)) return rgbM;
    vec2 dir = vec2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
    float reduce = max((lNW + lNE + lSW + lSE) * 0.03125, 1.0 / 128.0);
    float rcp = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * rcp, vec2(-8.0), vec2(8.0)) * texel;
    vec3 a = 0.5 * (resolved(uv + dir * (1.0 / 3.0 - 0.5)) + resolved(uv + dir * (2.0 / 3.0 - 0.5)));
    vec3 b = a * 0.5 + 0.25 * (resolved(uv - dir * 0.5) + resolved(uv + dir * 0.5));
    float lB = dot(b, L);
    return (lB < lMin || lB > lMax) ? a : b;
}

vec3 sharpen(vec2 uv, vec3 c, float amount) {
    vec3 n = resolved(uv + vec2(0.0, -texel.y));
    vec3 s = resolved(uv + vec2(0.0,  texel.y));
    vec3 e = resolved(uv + vec2( texel.x, 0.0));
    vec3 w = resolved(uv + vec2(-texel.x, 0.0));
    vec3 mn = min(c, min(min(n, s), min(e, w)));
    vec3 mx = max(c, max(max(n, s), max(e, w)));
    vec3 blur = (n + s + e + w) * 0.25;
    return clamp(c + (c - blur) * (amount * 2.0), mn, mx);
}

void main() {
    vec2 uv = v_uv * pc.map.xy + pc.map.zw;
    vec2 size = vec2(textureSize(src, 0));
    vec2 dtex = fwidth(uv * size);
    bool shrink = max(dtex.x, dtex.y) > 1.01;
    footprint = dtex;
    shrinking = shrink;
    if (any(lessThan(v_uv, pc.bound.xy)) || any(greaterThan(v_uv, pc.bound.zw))) {
        out_color = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    if (pc.grade.w < 0.5 && pc.opts.x > 0.5 && pc.opts.x < 1.5 && !shrink) {
        out_color = vec4(texture(src, uv).rgb, 1.0);
        return;
    }
    texel = 1.0 / size;
    lo = (pc.bound.xy * pc.map.xy + pc.map.zw) + 0.5 * texel;
    hi = (pc.bound.zw * pc.map.xy + pc.map.zw) - 0.5 * texel;
    bool fsr = fsr_on();
    vec3 c = fsr ? fsr_easu(uv)
           : shrink && pc.opts.x > 0.5 ? downsample(uv, dtex, texel)
                                       : filtered(uv, dtex);
    if (shrink) texel *= max(dtex, vec2(1.0));
    if (pc.opts.y > 0.5) c = fxaa(uv, c);
    if (pc.opts.z > 0.001)
        c = fsr ? fsr_rcas(uv, c, pc.opts.z, dtex / size) : sharpen(uv, c, pc.opts.z);
    c += pc.grade.x;
    c = (c - 0.5) * pc.grade.y + 0.5;
    if (abs(pc.grade.z - 1.0) > 0.001) c = pow(max(c, vec3(0.0)), vec3(1.0 / pc.grade.z));
    if (abs(pc.opts.w - 1.0) > 0.001) c = mix(vec3(dot(c, vec3(0.299, 0.587, 0.114))), c, pc.opts.w);
    out_color = vec4(clamp(c, 0.0, 1.0), 1.0);
}
