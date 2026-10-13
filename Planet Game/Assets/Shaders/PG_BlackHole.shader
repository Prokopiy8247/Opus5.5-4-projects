// A black hole with an accretion disk on the background sky: one quad built by
// DeepSky.cs on the sky sphere and, like the stars, rotated by the view only.
// It is scenery - infinitely far away, no gravity, no light, no collider.
//
// Every pixel traces its light ray backwards through the Schwarzschild metric
// (units of the Schwarzschild radius; the bending is the exact null geodesic,
// x'' = -1.5 h^2 x / r^5). That gives, after real simulations and Interstellar's
// Gargantua:
//  - the black shadow, 2.6 radii wide;
//  - the thin disk crossing in front of it, its far side bent up over the top
//    of the shadow and its underside bent round below it;
//  - the thin photon ring hugging the shadow;
//  - the sky behind (_PG_SkyCube) stretched into arcs around the hole.
// The disk turns slowly, faster inside (Kepler); the side turning toward the
// viewer is brighter (Doppler beaming).
//
// Drawn after the deep sky and the stars and without depth, so it hides the
// sky behind it and every real object covers it. Toward the rim of the quad
// the bending and the coverage fade out, so it joins the ordinary sky without
// a seam. Fades with _PG_StarFade under a bright day sky.
Shader "PG/BlackHole"
{
    Properties
    {
        _Intensity ("Disk intensity", Float) = 1.0
        _DiskInner ("Disk inner radius", Float) = 2.6
        _DiskOuter ("Disk outer radius", Float) = 13.0
        _Spin ("Disk rotation speed", Float) = 0.5
        _Doppler ("Doppler beaming", Range(0, 1)) = 0.45
        _HotColor ("Inner colour", Color) = (1.00, 0.90, 0.74, 1)
        _MidColor ("Middle colour", Color) = (1.00, 0.63, 0.27, 1)
        _CoolColor ("Outer colour", Color) = (0.80, 0.24, 0.06, 1)
        _StarGain ("Lensed stars", Float) = 1.6
        _Glow ("Glow around the hole", Float) = 0.09
    }
    SubShader
    {
        Tags { "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "Queue"="Background+10" "IgnoreProjector"="True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"
            #include "PGSky.cginc"

            #define BH_STEPS 120

            float _Intensity, _DiskInner, _DiskOuter, _Spin, _Doppler, _StarGain, _Glow;
            float4 _HotColor, _MidColor, _CoolColor;
            samplerCUBE _PG_SkyCube;

            struct appdata
            {
                float4 vertex : POSITION;   // point on the sky (unit sphere + tangent offset)
                float2 uv     : TEXCOORD0;  // quad corner in [-1, 1]
                float4 p1     : TEXCOORD1;  // xyz: direction of the hole, w: viewer distance in radii
                float4 p2     : TEXCOORD2;  // xyz: disk axis
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float3 dir : TEXCOORD1;
                float4 p1  : TEXCOORD2;
                float4 p2  : TEXCOORD3;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float3 viewDir = mul((float3x3)UNITY_MATRIX_V, v.vertex.xyz);
                o.pos = mul(UNITY_MATRIX_P, float4(viewDir * 100.0, 1.0));
                o.uv = v.uv;
                o.dir = v.vertex.xyz;
                o.p1 = v.p1;
                o.p2 = v.p2;
                return o;
            }

            // One layer of the disk's streaks, turned back by 'ang' around the axis.
            float Streaks(float2 q, float r, float ang, float seed)
            {
                float s, c;
                sincos(ang, s, c);
                float2 qr = float2(q.x * c - q.y * s, q.x * s + q.y * c);
                // (cos, sin) of the azimuth instead of the angle itself: no seam.
                // Long along the orbit, short across it.
                float n = PGS_Fbm(float3(qr * 2.6, r * 1.15 + seed), 4);
                float rings = PGS_Fbm(float3(qr * 0.9, r * 5.5 + seed * 3.0), 3);
                return saturate(0.52 + 0.62 * n + 0.38 * rings);
            }

            // Premultiplied emission (rgb) and opacity (a) of the disk where a ray
            // travelling along v crosses it at p.
            float4 Disk(float3 p, float3 v, float3 axis, float3 du, float3 dw)
            {
                float r = length(p);
                float u = (r - _DiskInner) / (_DiskOuter - _DiskInner);
                if (u <= 0.0 || u >= 1.0) return 0;

                // Differential rotation would wind the pattern up for ever, so two
                // copies run half a period apart and cross-fade as they reset.
                const float period = 36.0;
                float om = _Spin * pow(r, -1.5);
                float f1 = frac(_Time.y / period), f2 = frac(_Time.y / period + 0.5);
                float2 q = float2(dot(p, du), dot(p, dw)) / r;
                float w1 = 1.0 - abs(2.0 * f1 - 1.0);
                float streak = lerp(Streaks(q, r, -om * (f2 - 0.5) * period, 17.3),
                                    Streaks(q, r, -om * (f1 - 0.5) * period, 0.0), w1);

                float dens = smoothstep(0.0, 0.05, u) * (1.0 - smoothstep(0.45, 1.0, u));
                float alpha = saturate(dens * (0.22 + 1.5 * streak * streak));

                float heat = pow(_DiskInner / r, 1.1);
                float3 tint = heat > 0.45 ? lerp(_MidColor.rgb, _HotColor.rgb, saturate((heat - 0.45) / 0.45))
                                          : lerp(_CoolColor.rgb, _MidColor.rgb, saturate(heat / 0.45));

                // Kepler orbit; the light we see left along -v.
                float beta = min(sqrt(0.5 / r), 0.6);
                float3 vel = cross(axis, p) / r * beta;
                float dop = sqrt(1.0 - beta * beta) / (1.0 + dot(vel, normalize(v)));
                float boost = pow(dop, 3.0 * _Doppler);
                tint *= lerp(float3(1.0, 0.86, 0.72), float3(1.0, 1.04, 1.10), saturate((dop - 0.7) / 0.8 * _Doppler + 0.5 * (1.0 - _Doppler)));

                float3 em = tint * (0.30 + 2.6 * heat) * (0.30 + 1.5 * streak) * boost * _Intensity;
                return float4(em * alpha, alpha);
            }

            // Sparse point stars for the bent sky: the real star sprites cannot be moved.
            float3 Stars(float3 dir)
            {
                float3 p = dir * 120.0;
                float3 id = floor(p);
                float3 h = PGS_Hash33(id);
                float3 d = p - (id + 0.25 + 0.5 * h);
                float on = step(0.88, PGS_Hash31(id + 7.7));
                float mag = 0.25 + 0.75 * h.x * h.x;
                float3 c = lerp(float3(1.0, 0.82, 0.66), float3(0.72, 0.84, 1.0), h.y);
                return c * (on * mag * exp(-dot(d, d) * 38.0));
            }

            float4 frag (v2f i) : SV_Target
            {
                float edge = length(i.uv);
                if (edge >= 0.97 || _PG_StarFade < 0.004) return 0;

                float3 d = normalize(i.dir);
                float3 centre = i.p1.xyz;
                float dist = i.p1.w;
                float3 axis = i.p2.xyz;
                float3 du = normalize(centre - axis * dot(centre, axis));
                float3 dw = cross(axis, du);

                float3 x = -centre * dist;
                float3 v = d;
                float3 hv = cross(x, v);
                float h2 = dot(hv, hv);
                float side = dot(x, axis);
                float escape2 = dist * dist * 1.02;

                float3 col = 0;
                float trans = 1.0;
                float sky = 0.0;    // 1: the ray got away and shows the sky behind

                UNITY_LOOP
                for (int k = 0; k < BH_STEPS; k++)
                {
                    float r2 = dot(x, x);
                    float r = sqrt(r2);
                    float dt = r * lerp(0.045, 0.15, saturate((r - 2.0) / 12.0));
                    v -= x * (1.5 * h2 / (r2 * r2 * r) * dt);
                    float3 xn = x + v * dt;

                    float sideN = dot(xn, axis);
                    UNITY_BRANCH
                    if (side * sideN < 0.0)
                    {
                        float4 dk = Disk(lerp(x, xn, side / (side - sideN)), v, axis, du, dw);
                        col += trans * dk.rgb;
                        trans *= 1.0 - dk.a;
                    }
                    x = xn;
                    side = sideN;

                    float rn2 = dot(x, x);
                    if (rn2 < 1.0) break;                               // fell through the horizon
                    if (rn2 > escape2 && dot(x, v) > 0.0) { sky = 1.0; break; }
                }

                // The bending falls off slowly (as 1 / distance); it is eased out before
                // the rim so that the sky there is exactly the sky underneath.
                float cover = 1.0 - smoothstep(0.62, 0.96, edge);
                float3 ld = normalize(lerp(d, normalize(v), 1.0 - smoothstep(0.45, 0.85, edge)));

                // Near the Einstein ring a single star behind the hole smears into a long
                // arc or a full circle; those are faded by how lopsided the stretch is.
                float3 gx = ddx(ld), gy = ddy(ld);
                float gt = dot(gx, gx) + dot(gy, gy);
                float gd = dot(gx, gx) * dot(gy, gy) - dot(gx, gy) * dot(gx, gy);
                float gs = sqrt(max(gt * gt - 4.0 * gd, 0.0));
                float stretch = sqrt((gt + gs) / max(gt - gs, 1e-12));
                float3 stars = Stars(ld) * (_StarGain * saturate(3.0 / stretch - 0.35));
                float3 bg = texCUBE(_PG_SkyCube, ld).rgb + stars;

                // light of the disk scattered around the hole
                float ang = acos(clamp(dot(d, centre), -1.0, 1.0)) * dist / _DiskOuter;
                float3 glow = _MidColor.rgb * (_Glow * _Intensity * exp(-ang * 3.2) * (1.0 - smoothstep(0.5, 0.95, edge)));

                float3 c = col + (bg * cover + glow) * (trans * sky);
                return float4(c, cover) * _PG_StarFade;
            }
            ENDCG
        }
    }
}
