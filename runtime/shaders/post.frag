#version 450
// Second pass of the present when the picture is supersampled (internal
// resolution above the window): present.frag has already scaled the picture
// down once into an image the size of the window, and this pass runs FXAA,
// the sharpening and the colour adjustments on it.  The single-pass version
// re-ran the whole downscale for every neighbour FXAA and the sharpening look
// at (about 13 times a pixel); the result is the same picture.
layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 out_color;
layout(set = 0, binding = 0) uniform sampler2D src;   // the scaled picture
layout(set = 1, binding = 0) uniform sampler2D big;   // the game's picture
layout(push_constant) uniform PC {
    vec4 map;      // v_uv -> uv in the game's picture, as in present.frag
    vec4 bound;    // the picture inside the window, in v_uv units
    vec4 opts;     // y: FXAA, z: sharpen amount, w: saturation
    vec4 grade;    // brightness, contrast, gamma
} pc;

vec2 lo, hi, texel;

vec3 tap(vec2 uv) { return texture(src, clamp(uv, lo, hi)).rgb; }

// FXAA's in-between taps: scaled down from the game's picture at that very
// spot, exactly as the single-pass present did (a bilinear tap of the scaled
// picture would blur the edges a little more)
vec3 fine(vec2 uv) {
    uv = clamp(uv, lo, hi);
    vec2 size = vec2(textureSize(big, 0));
    vec2 one = 1.0 / size;
    vec2 dtex = pc.map.xy * size * texel;          // game texels per window pixel
    vec2 buv = uv * pc.map.xy + pc.map.zw;
    vec2 blo = lo * pc.map.xy + pc.map.zw, bhi = hi * pc.map.xy + pc.map.zw;
    ivec2 n = ivec2(clamp(ceil(dtex - 0.01), vec2(1.0), vec2(4.0)));
    vec2 step = dtex / vec2(n) * one;
    vec2 first = buv - 0.5 * dtex * one + 0.5 * step;
    vec3 sum = vec3(0.0);
    for (int y = 0; y < n.y; y++)
        for (int x = 0; x < n.x; x++)
            sum += texture(big, clamp(first + vec2(float(x), float(y)) * step, blo, bhi)).rgb;
    return sum / float(n.x * n.y);
}

vec3 fxaa(vec2 uv, vec3 rgbM) {
    const vec3 L = vec3(0.299, 0.587, 0.114);
    vec3 rgbNW = tap(uv + vec2(-1.0, -1.0) * texel);
    vec3 rgbNE = tap(uv + vec2( 1.0, -1.0) * texel);
    vec3 rgbSW = tap(uv + vec2(-1.0,  1.0) * texel);
    vec3 rgbSE = tap(uv + vec2( 1.0,  1.0) * texel);
    float lNW = dot(rgbNW, L), lNE = dot(rgbNE, L);
    float lSW = dot(rgbSW, L), lSE = dot(rgbSE, L), lM = dot(rgbM, L);
    float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
    float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
    if (lMax - lMin < max(1.0 / 32.0, lMax / 8.0)) return rgbM;
    vec2 dir = vec2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
    float reduce = max((lNW + lNE + lSW + lSE) * 0.03125, 1.0 / 128.0);
    float rcp = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * rcp, vec2(-8.0), vec2(8.0)) * texel;
    vec3 a = 0.5 * (fine(uv + dir * (1.0 / 3.0 - 0.5)) + fine(uv + dir * (2.0 / 3.0 - 0.5)));
    vec3 b = a * 0.5 + 0.25 * (fine(uv - dir * 0.5) + fine(uv + dir * 0.5));
    float lB = dot(b, L);
    return (lB < lMin || lB > lMax) ? a : b;
}

vec3 sharpen(vec2 uv, vec3 c, float amount) {
    vec3 n = tap(uv + vec2(0.0, -texel.y));
    vec3 s = tap(uv + vec2(0.0,  texel.y));
    vec3 e = tap(uv + vec2( texel.x, 0.0));
    vec3 w = tap(uv + vec2(-texel.x, 0.0));
    vec3 mn = min(c, min(min(n, s), min(e, w)));
    vec3 mx = max(c, max(max(n, s), max(e, w)));
    vec3 blur = (n + s + e + w) * 0.25;
    return clamp(c + (c - blur) * (amount * 2.0), mn, mx);
}

void main() {
    vec2 uv = v_uv;
    if (any(lessThan(uv, pc.bound.xy)) || any(greaterThan(uv, pc.bound.zw))) {
        out_color = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    texel = 1.0 / vec2(textureSize(src, 0));
    lo = pc.bound.xy + 0.5 * texel;
    hi = pc.bound.zw - 0.5 * texel;
    vec3 c = texture(src, uv).rgb;
    if (pc.opts.y > 0.5) c = fxaa(uv, c);
    if (pc.opts.z > 0.001) c = sharpen(uv, c, pc.opts.z);
    c += pc.grade.x;
    c = (c - 0.5) * pc.grade.y + 0.5;
    if (abs(pc.grade.z - 1.0) > 0.001) c = pow(max(c, vec3(0.0)), vec3(1.0 / pc.grade.z));
    if (abs(pc.opts.w - 1.0) > 0.001) c = mix(vec3(dot(c, vec3(0.299, 0.587, 0.114))), c, pc.opts.w);
    out_color = vec4(clamp(c, 0.0, 1.0), 1.0);
}
