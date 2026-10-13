// Noise and frame helpers for the deep-sky shaders (PG/DeepSkyBake, PG/Galaxy).
//
// Sin-free hashes (stable at the coordinate ranges used on the sky sphere),
// quintic gradient noise, fBm and ridged fBm. Everything is evaluated on 3D
// directions, so the sky has no seams between cubemap faces.
#ifndef PG_SKY_INCLUDED
#define PG_SKY_INCLUDED

inline float3 PGS_Hash33(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}

inline float PGS_Hash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

// Gradient noise, roughly [-1, 1].
inline float PGS_Noise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    #define PGS_G(o) dot(PGS_Hash33(i + o) * 2.0 - 1.0, f - o)
    float n000 = PGS_G(float3(0, 0, 0));
    float n100 = PGS_G(float3(1, 0, 0));
    float n010 = PGS_G(float3(0, 1, 0));
    float n110 = PGS_G(float3(1, 1, 0));
    float n001 = PGS_G(float3(0, 0, 1));
    float n101 = PGS_G(float3(1, 0, 1));
    float n011 = PGS_G(float3(0, 1, 1));
    float n111 = PGS_G(float3(1, 1, 1));
    #undef PGS_G
    return 1.6 * lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                      lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

// fBm, roughly [-1, 1].
inline float PGS_Fbm(float3 p, int octaves)
{
    float s = 0.0, a = 0.5, norm = 0.0;
    for (int k = 0; k < octaves; k++)
    {
        s += a * PGS_Noise(p);
        norm += a;
        p = p * 2.02 + float3(1.7, 9.2, 3.1);
        a *= 0.5;
    }
    return s / norm;
}

// Ridged fBm in [0, 1]: thin bright creases, used for filaments and dust lanes.
inline float PGS_Ridged(float3 p, int octaves)
{
    float s = 0.0, a = 0.5, norm = 0.0;
    for (int k = 0; k < octaves; k++)
    {
        float n = 1.0 - abs(PGS_Noise(p));
        s += a * n * n;
        norm += a;
        p = p * 2.07 + float3(5.3, 1.1, 7.7);
        a *= 0.52;
    }
    return s / norm;
}

#endif
