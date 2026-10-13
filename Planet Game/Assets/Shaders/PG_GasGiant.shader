// Gas giant: a participating medium, not a surface. One sphere at the top of
// the haze, drawn on its back faces, ray-marches the view ray through the gas
// between the analytic sphere intersections. From orbit the dense cloud deck
// makes it read as a solid banded planet with a soft limb; from inside the
// same shader is the fog around the ship, the darker cloud decks above and
// below, the clearer gaps between them and the glowing calm at the core - so
// flying in and out of it is one continuous change of the view ray.
//
// Opaque geometry in front of the far side (the ship, other planets) occludes
// the shell through ZTest, so the ship is never fogged by gas behind it.
// Output is premultiplied.
Shader "PG/GasGiant"
{
    Properties
    {
        _PlanetCenter ("Planet Centre (render space)", Vector) = (0,0,0,0)
        _DeckRadius   ("Cloud Deck Radius", Float) = 8500
        _OuterRadius  ("Haze Top Radius", Float) = 10300
        _Density      ("Deck Density (1/m)", Float) = 0.0022
        _BandA        ("Zone Colour", Color) = (0.86,0.74,0.55,1)
        _BandB        ("Belt Colour", Color) = (0.68,0.44,0.26,1)
        _BandC        ("Dark Belt Colour", Color) = (0.42,0.24,0.17,1)
        _StormColor   ("Storm Colour", Color) = (0.86,0.30,0.16,1)
        _HazeColor    ("Haze Colour", Color) = (0.95,0.78,0.52,1)
        _DeepColor    ("Depth Glow Colour", Color) = (0.55,0.20,0.10,1)
        _StormDir     ("Storm Direction (xyz) + size (w, rad)", Vector) = (0.6,-0.35,0.72,0.22)
        _BandCount    ("Bands", Float) = 7
        _Seed         ("Seed Offset", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float4 _PlanetCenter;
            float  _DeckRadius;
            float  _OuterRadius;
            float  _Density;
            float4 _BandA, _BandB, _BandC, _StormColor, _HazeColor, _DeepColor;
            float4 _StormDir;
            float  _BandCount;
            float4 _Seed;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // Extinction per metre at radius r (planet-local direction d).
            float DensityAt(float r, float3 d)
            {
                float H = (_OuterRadius - _DeckRadius) * 0.3;
                float haze = 0.30 * exp(-max(r - _DeckRadius, 0.0) / H);
                // Below the deck: alternating cloud decks with clearer gaps, then
                // a calm, thin core. u is 0 at the deck top, 1 at the centre.
                float u = saturate((_DeckRadius - r) / _DeckRadius);
                float wob = PG_Noise(d * 9.0 + _Seed.xyz) * 0.9;
                float layers = 0.5 + 0.5 * cos((u * 7.0 + wob) * 6.2831853);
                float deck = lerp(0.22, 1.0, smoothstep(0.30, 0.85, layers));
                deck *= lerp(1.0, 0.12, smoothstep(0.62, 0.90, u));      // the quiet core
                float inDeck = smoothstep(_DeckRadius + 120.0, _DeckRadius - 60.0, r);
                return _Density * lerp(haze, deck, inDeck);
            }

            float3 BandColor(float3 d, float u)
            {
                // Differential rotation: the bands drift at slightly different rates.
                float lat = d.y;
                float spin = _Time.y * 0.0045 * (1.0 + 0.35 * sin(lat * 9.0));
                float cs = cos(spin), sn = sin(spin);
                float3 q = float3(d.x * cs - d.z * sn, d.y, d.x * sn + d.z * cs);

                float warp = PG_Fbm(q * 5.0 + _Seed.xyz, 2);
                float fine = PG_Noise(q * 22.0 + _Seed.zxy);
                float b = lat * _BandCount + (warp - 0.5) * 1.5 + (fine - 0.5) * 0.25;
                float z1 = 0.5 + 0.5 * sin(b * 3.14159265);
                float z2 = 0.5 + 0.5 * sin(b * 1.16 + 1.3);
                float3 c = lerp(_BandA.rgb, _BandB.rgb, smoothstep(0.30, 0.75, z1));
                c = lerp(c, _BandC.rgb, smoothstep(0.62, 0.95, z2) * 0.85);

                // The great storm: an oval with swirl rings, fixed to the bands.
                float3 sd = normalize(_StormDir.xyz);
                float ca = dot(q, sd);
                float ang = acos(clamp(ca, -1.0, 1.0));
                float rel = ang / _StormDir.w;
                float swirl = 0.5 + 0.5 * sin(rel * 11.0 - warp * 5.0);
                float storm = (1.0 - smoothstep(0.55, 1.0, rel)) * lerp(0.65, 1.0, swirl);
                c = lerp(c, _StormColor.rgb, storm);

                // Deeper gas is darker and redder.
                return lerp(c, _DeepColor.rgb, smoothstep(0.0, 0.45, u) * 0.85);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos - _PlanetCenter.xyz;
                float3 rd = normalize(i.wpos - _WorldSpaceCameraPos);

                float2 ta = PG_RaySphere(ro, rd, _OuterRadius);
                if (ta.y <= 0.0) return 0;
                float t0 = max(ta.x, 0.0);
                float seg = ta.y - t0;
                if (seg <= 0.0) return 0;

                // Everything visible lies within a few optical depths; the rest
                // of a long chord is folded into one deep-haze term below.
                const int N = 24;
                float march = min(seg, 9000.0);
                float ds = march / N;
                // Interleaved-gradient dither of the start, so the coarse steps
                // never show as concentric bands.
                float jit = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));

                float T = 1.0;
                float3 col = 0;
                [loop]
                for (int k = 0; k < N; k++)
                {
                    float3 p = ro + rd * (t0 + (k + jit) * ds);
                    float r = length(p);
                    float3 d = p / max(r, 1.0);
                    float dens = DensityAt(r, d);
                    float a = 1.0 - exp(-dens * ds);
                    if (a > 0.002)
                    {
                        float u = saturate((_DeckRadius - r) / _DeckRadius);
                        float3 L = normalize(_PG_SunPos.xyz - (_PlanetCenter.xyz + p));
                        // Sunlight fades within ~1.6 km of the deck top.
                        float sun = smoothstep(-0.15, 0.25, dot(d, L)) * exp(-max(_DeckRadius - r, 0.0) / 1600.0);
                        // Heat from below: the depths and the calm core glow.
                        float glow = 0.10 + 1.6 * smoothstep(0.35, 1.0, u);
                        // Lightning deep in the decks, a few cells at a time.
                        float3 cell = floor(d * 34.0);
                        float flash = step(0.988, PG_Hash(cell + floor(_Time.y * 3.0) * 7.31))
                                    * smoothstep(0.03, 0.12, u) * (1.0 - smoothstep(0.35, 0.5, u));
                        float3 light = _PG_SunColor.rgb * sun + _PG_Ambient.rgb * 2.5
                                     + _DeepColor.rgb * glow + float3(0.75, 0.85, 1.0) * flash * 3.0;
                        float3 c = r > _DeckRadius ? _HazeColor.rgb : BandColor(d, u);
                        col += T * a * c * light;
                        T *= 1.0 - a;
                        if (T < 0.01) break;
                    }
                }

                if (seg > march && T > 0.01)
                {
                    float a = 1.0 - exp(-(seg - march) * _Density * 0.3);
                    col += T * a * _DeepColor.rgb * 0.6;
                    T *= 1.0 - a;
                }
                return float4(col, 1.0 - T);
            }
            ENDCG
        }
    }
}
