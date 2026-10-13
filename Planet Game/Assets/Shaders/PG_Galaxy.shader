// Distant galaxies: one quad each, built by DeepSky.cs on the sky sphere and,
// like the stars, rotated by the view only.
//
// Types, after real photographs:
//  0 spiral - warm yellow bulge, blue-white logarithmic arms with pink
//    star-forming knots, brown dust lanes on the inner edge of the arms; the
//    inclination squashes the disk (Andromeda), edge-on it becomes a thin
//    disk split by a dark midplane lane around a round bulge (NGC 4565);
//    an optional bar for barred spirals.
//  1 elliptical - smooth de Vaucouleurs profile, yellow-orange.
// Detail fades out when a galaxy is only a few pixels wide, so small ones
// read as soft smudges and never shimmer.
Shader "PG/Galaxy"
{
    Properties
    {
        _Intensity ("Intensity", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "Queue"="Background-10" "IgnoreProjector"="True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"
            #include "PGSky.cginc"

            float _Intensity;

            struct appdata
            {
                float4 vertex : POSITION;   // point on the sky (unit sphere + tangent offset)
                float4 color  : COLOR;      // disk / body tint
                float2 uv     : TEXCOORD0;  // sky-plane coords in galaxy radii
                float4 p1     : TEXCOORD1;  // type, cos(inclination), seed, arm twist
                float4 p2     : TEXCOORD2;  // bulge radius, brightness, arm count, bar
                float2 corner : TEXCOORD3;  // quad corner in [-1, 1]
            };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                float4 p1     : TEXCOORD1;
                float4 p2     : TEXCOORD2;
                float2 corner : TEXCOORD3;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float3 viewDir = mul((float3x3)UNITY_MATRIX_V, v.vertex.xyz);
                o.pos = mul(UNITY_MATRIX_P, float4(viewDir * 100.0, 1.0));
                o.color = v.color;
                o.uv = v.uv;
                o.p1 = v.p1;
                o.p2 = v.p2;
                o.corner = v.corner;
                return o;
            }

            float3 Spiral(float2 s, float ci, float seed, float twist, float bulgeR, float arms, float bar, float3 tint, float detail)
            {
                // Sky plane -> disk plane. A disk has some thickness, so edge-on it stays visible.
                float ce = sqrt(ci * ci + 0.09 * 0.09);
                float2 g = float2(s.x, s.y / ce);
                float r = length(g);
                float th = atan2(g.y, g.x);

                float n = PGS_Fbm(float3(g * 5.0, seed * 7.13), 4);
                float ph = arms * (th - log(max(r, 0.03)) * twist) + seed * 6.2831 + n * 0.9;
                float armW = pow(saturate(0.5 + 0.5 * cos(ph)), 2.0);
                armW = lerp(0.35, armW, detail);
                float armMask = smoothstep(0.06 + 0.2 * bar, 0.28 + 0.2 * bar, r);

                // a tilted disk is seen through more stars: brighter per area, up to ~3x edge-on
                float disk = exp(-r * 2.7) * smoothstep(1.0, 0.5, r) * min(rsqrt(ce), 3.0);
                float mottle = 0.8 + 0.35 * PGS_Fbm(float3(g * 14.0, seed * 2.3), 3) * detail;
                float knots = saturate(PGS_Noise(float3(g * 22.0, seed * 3.1)) * 2.4 - 1.3) * armW * armMask * detail;
                float3 col = tint * disk * mottle * (0.5 + 0.8 * armW * armMask)
                           + float3(1.00, 0.42, 0.62) * knots * disk * 1.2;

                // bar and bulge: warm old stars
                float barI = bar * exp(-(g.x * g.x / 0.09 + g.y * g.y / 0.005));
                float2 sb = float2(s.x, s.y / max(ci, 0.65));
                float rb = length(sb) / bulgeR;
                float bul = exp(-pow(rb, 0.8) * 3.0) * 1.3 + exp(-rb * rb * 30.0) * 1.2;
                col += float3(1.00, 0.82, 0.58) * (bul + barI * 0.55 * smoothstep(1.0, 0.3, r));

                // dust: lanes on the inner edge of the arms, stronger the more inclined
                float lane = pow(saturate(0.5 + 0.5 * cos(ph - 0.8)), 10.0) * armMask * smoothstep(1.0, 0.45, r) * detail;
                float tau = lane * 1.3 * (0.45 + 0.55 * (1.0 - ci)) * (s.y > 0.0 ? 1.0 : 0.55);
                // edge-on: a dark midplane lane across the bulge, slightly to the near side
                float edge = saturate((0.45 - ci) / 0.4);
                edge *= edge;
                float mid = exp(-pow((s.y - 0.006) / (0.016 + 0.04 * ci), 2.0)) * smoothstep(1.0, 0.25, abs(s.x));
                tau += edge * mid * 2.6 * (0.75 + 0.25 * n);
                col *= exp(-tau * float3(1.00, 1.22, 1.50));
                return col;
            }

            float3 Elliptical(float2 s, float ci)
            {
                float2 e = float2(s.x, s.y / max(ci, 0.3));
                float r = length(e);
                // Sersic n = 2: a bright but soft core and a wide faint halo
                float lum = exp(-3.67 * (sqrt(r / 0.35) - 1.0)) * 0.10;
                lum *= smoothstep(1.0, 0.5, r);
                float3 c = lerp(float3(1.00, 0.70, 0.42), float3(1.00, 0.90, 0.74), exp(-r * 8.0));
                return c * lum;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float type = i.p1.x;
                // radii per pixel: shrink detail when the galaxy is tiny on screen
                float px = fwidth(i.uv.x);
                float detail = saturate(1.6 - px * 14.0);

                float3 c = type < 0.5
                    ? Spiral(i.uv, i.p1.y, i.p1.z, i.p1.w, i.p2.x, i.p2.z, i.p2.w, i.color.rgb, detail)
                    : Elliptical(i.uv, i.p1.y) * i.color.rgb;

                float2 k = abs(i.corner);
                float fade = (1.0 - smoothstep(0.8, 1.0, k.x)) * (1.0 - smoothstep(0.8, 1.0, k.y));
                return fixed4(c * fade * i.p2.y * _Intensity * _PG_StarFade, 0);
            }
            ENDCG
        }
    }
}
