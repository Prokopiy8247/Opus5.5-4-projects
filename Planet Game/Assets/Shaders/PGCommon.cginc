// Shared globals and helpers for the PG shaders.
//
// Lighting contract: C# (PGGlobalLighting) pushes the star's render-space
// position every frame. Every shader derives the sun direction PER PIXEL from
// that position, so each planet is lit from where the star actually is relative
// to it - a planet on the far side of Helion shows its night side, as it should.
#ifndef PG_COMMON_INCLUDED
#define PG_COMMON_INCLUDED

float4 _PG_SunPos;       // xyz: render-space position of the star centre
float4 _PG_SunColor;     // rgb: sun radiance (intensity baked in)
float4 _PG_Ambient;      // rgb: deep-space ambient
float4 _PG_Ground;       // rgb: ground bounce floor
float4 _PG_LocalUp;      // xyz: up at the camera (away from the current body)
float  _PG_StarFade;     // 1 in space, 0 under a bright day sky

inline float3 PG_SunDirAt(float3 wpos)
{
    return normalize(_PG_SunPos.xyz - wpos);
}

// Ray / sphere (sphere at the origin). Returns (tNear, tFar); tFar < tNear = miss.
inline float2 PG_RaySphere(float3 ro, float3 rd, float r)
{
    float b = dot(ro, rd);
    float c = dot(ro, ro) - r * r;
    float h = b * b - c;
    if (h < 0.0) return float2(1.0, -1.0);
    h = sqrt(h);
    return float2(-b - h, -b + h);
}

inline float PG_Hash(float3 p)
{
    p = frac(p * 0.3183099 + float3(0.71, 0.113, 0.419));
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

inline float PG_Noise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = PG_Hash(i);
    float n100 = PG_Hash(i + float3(1, 0, 0));
    float n010 = PG_Hash(i + float3(0, 1, 0));
    float n110 = PG_Hash(i + float3(1, 1, 0));
    float n001 = PG_Hash(i + float3(0, 0, 1));
    float n101 = PG_Hash(i + float3(1, 0, 1));
    float n011 = PG_Hash(i + float3(0, 1, 1));
    float n111 = PG_Hash(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

inline float PG_Fbm(float3 p, int octaves)
{
    float s = 0.0, a = 0.5, norm = 0.0;
    for (int k = 0; k < octaves; k++)
    {
        s += a * PG_Noise(p);
        norm += a;
        p = p * 2.03 + float3(1.7, 9.2, 3.1);
        a *= 0.5;
    }
    return s / norm;
}

// Day factor at a point on a sphere: 0 night, 1 day, soft terminator.
inline float PG_Day(float3 up, float3 sunDir)
{
    return smoothstep(-0.16, 0.24, dot(up, sunDir));
}

#endif
