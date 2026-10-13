// Deep-sky generator: evaluated ONCE per texel into a cubemap by DeepSky.cs,
// never per frame, so it can afford many octaves of noise.
//
// Modelled on real long-exposure photographs:
//  - Milky Way: a thin band made mostly of the grain of countless unresolved
//    stars, with patchy star clouds, a warm oval central bulge, blue-white
//    toward the anticentre, faint H-alpha patches, and dark dust: the "Great
//    Rift" splitting the middle of the band plus thin tendrils around it.
//  - Faint galactic cirrus at high latitude, so no part of the sky is empty.
//  - Nebulae, placed by DeepSky.cs:
//      0 emission (H II region: deep red H-alpha edges, magenta body, pink
//        core, optional teal OIII core, filaments and dark globules)
//      1 reflection (blue, wispy, lit by a small cluster of hot stars)
//      2 planetary (blue-green OIII interior, yellow-white ring, red rim)
//      3 supernova remnant (broken shell of red/teal filaments)
//      4 dark nebula (absorbs everything behind it)
//      5 dwarf irregular galaxy (a Magellanic-cloud-like patch with a bar and
//        pink star-forming knots)
//
// Output is linear HDR radiance, kept well below the bloom threshold so the
// game's ACES tonemapping keeps the colours. Each face is drawn as one
// full-screen quad; the face basis comes from C#.
Shader "Hidden/PG/DeepSkyBake"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "PGSky.cginc"

            #define MAX_NEB 32

            float4 _FaceZ, _FaceX, _FaceY;   // face basis (direction = Z + x X + y Y)
            float4 _GalN, _GalC;             // galactic pole and centre directions
            float  _MilkyWay, _Dust, _NebulaGain, _StarDust, _Res, _Debug;
            float  _NebCount;
            float4 _NebPos[MAX_NEB];         // xyz direction, w angular radius (rad)
            float4 _NebA[MAX_NEB];           // x type, y seed, z intensity, w axis ratio
            float4 _NebB[MAX_NEB];           // x position angle (rad), y OIII amount

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float2 xy : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(v.vertex.xy, 0.5, 1.0));
                o.xy = v.vertex.xy;
                return o;
            }

            // ------------------------------------------------------ star grain

            // Unresolved stars: jittered points on a 3D grid of K cells per unit,
            // each present with probability prob. 3x3x3 neighbours, so no point is cut.
            float StarGrain(float3 d, float K, float prob, float sharp)
            {
                float3 c = d * K;
                float3 base = floor(c);
                float sum = 0.0;
                for (int z = -1; z <= 1; z++)
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    float3 cell = base + float3(x, y, z);
                    if (PGS_Hash31(cell + 0.37) > prob) continue;
                    float3 pt = cell + 0.15 + 0.7 * PGS_Hash33(cell + 17.3);
                    float3 dv = c - pt;
                    float b = PGS_Hash31(cell + 41.7);
                    sum += exp(-dot(dv, dv) * sharp) * (0.12 + 2.2 * b * b * b * b);
                }
                return sum;
            }

            // ------------------------------------------------------- nebulae

            float3 Emission(float2 p, float3 q, float inten, float oiii)
            {
                float3 w = float3(PGS_Fbm(q * 0.8 + 1.3, 4), PGS_Fbm(q * 0.8 + 7.9, 4), PGS_Fbm(q * 0.8 + 4.1, 4));
                float3 wq = q + w * 1.5;
                float r = length(p);
                // lobed outline, and an edge cut by the gas density itself, not a circle
                float lobes = PGS_Fbm(q * 0.35 + 2.0, 4);
                float rr = r * (1.0 - 0.45 * lobes);
                float env = exp(-rr * rr * 2.4);
                float big = PGS_Fbm(wq, 5);
                float fineD = PGS_Fbm(wq * 4.0 + 13.0, 4);
                float dens = saturate(0.5 + 0.8 * big + 0.25 * fineD);
                float cloud = saturate(dens * 1.3 - (1.0 - env) * 1.1);
                float fil = PGS_Ridged(wq * 2.0 + 3.0, 6);
                fil = fil * fil * fil * fil;
                float core = exp(-dot(p, p) * 4.0);
                float gas = cloud * cloud * 1.1 + fil * 0.9 * saturate(env * 1.5 - 0.15) * (0.3 + 0.7 * dens);
                gas *= 0.45 + 0.9 * core;
                // dark globules and lanes inside the glowing gas
                float glob = saturate(PGS_Ridged(wq * 1.4 + 9.0, 6) * 2.0 - 1.3);
                gas *= 1.0 - 0.95 * glob;

                float3 cHa   = float3(0.85, 0.05, 0.15);
                float3 cMid  = float3(1.00, 0.20, 0.42);
                float3 cCore = float3(1.00, 0.58, 0.76);
                float3 cO3   = float3(0.18, 0.78, 0.86);
                float3 c = lerp(cHa, cMid, saturate(gas * 1.3));
                c = lerp(c, cCore, saturate(core * gas * 1.2));
                c = lerp(c, cO3, saturate(oiii * core * dens * 1.4));
                float halo = exp(-r * r * 2.0) * (0.6 + 0.4 * big) * 0.045;
                float lit = gas / (1.0 + 0.7 * gas) * 1.1;   // soft shoulder: bright cores stay pink, not white
                return (c * lit + cHa * halo * 1.6) * inten;
            }

            float3 Reflection(float2 p, float3 q, float seed, float inten)
            {
                float r = length(p);
                float3 w = float3(PGS_Fbm(q * 0.8 + 3.1, 3), PGS_Fbm(q * 0.8 + 8.4, 3), 0.0);
                float wisps = PGS_Fbm(float3(p.x * 2.4, p.y * 4.0, seed) + w * 1.2, 6);
                float env = exp(-r * r * 2.2) * saturate(1.2 - r);
                float gas = env * pow(saturate(0.45 + 0.8 * wisps), 2.0);
                float3 c = lerp(float3(0.16, 0.32, 1.00), float3(0.55, 0.72, 1.00), exp(-r * r * 6.0));
                float3 col = c * gas * 0.60;
                // the hot young stars that light it
                for (int k = 0; k < 7; k++)
                {
                    float3 h = PGS_Hash33(float3(seed, k, 3.7));
                    float2 sp = (h.xy - 0.5) * 0.9;
                    float2 dv = p - sp;
                    float d2 = dot(dv, dv);
                    col += float3(0.75, 0.85, 1.0) * (exp(-d2 * 9000.0) * 2.0 + exp(-d2 * 300.0) * 0.06) * (0.5 + h.z);
                }
                return col * inten;
            }

            float3 Planetary(float2 p, float3 q, float inten)
            {
                float r = length(p) * (1.0 + 0.07 * PGS_Fbm(q * 2.0, 3));
                float n = 0.7 + 0.4 * PGS_Fbm(q * 3.5 + 5.0, 4);
                float ring = exp(-pow((r - 0.50) / 0.16, 2.0));
                float inner = smoothstep(0.55, 0.0, r);
                float halo = exp(-pow(max(r - 0.62, 0.0) / 0.22, 2.0)) * smoothstep(0.45, 0.7, r);
                float3 ringCol = lerp(float3(0.95, 0.85, 0.50), float3(1.00, 0.32, 0.10), saturate((r - 0.40) / 0.25));
                float3 col = float3(0.15, 0.60, 1.00) * inner * 0.30 * n
                           + ringCol * ring * 0.55 * n
                           + float3(0.85, 0.12, 0.10) * halo * 0.20 * n
                           + float3(0.9, 0.95, 1.0) * exp(-r * r * 1200.0) * 1.2;
                return col * inten;
            }

            float3 Remnant(float2 p, float3 q, float inten)
            {
                float r = length(p) + 0.12 * PGS_Fbm(q * 1.4, 4);
                float shell = exp(-pow((r - 0.78) / 0.14, 2.0));
                float fil = PGS_Ridged(q * 2.3 + 5.0, 6);
                fil = fil * fil * fil * fil;
                float arcs = saturate(0.45 + 0.9 * PGS_Fbm(q * 0.45 + 11.0, 3));
                float g = shell * fil * arcs;
                float mixO = saturate(PGS_Fbm(q * 1.1 + 3.0, 3) * 1.3 + 0.5);
                float3 c = lerp(float3(0.90, 0.10, 0.20), float3(0.15, 0.78, 0.86), mixO);
                float inside = smoothstep(0.85, 0.2, r) * 0.025;
                return (c * g * 1.4 + float3(0.8, 0.1, 0.18) * inside) * inten;
            }

            float DarkTau(float2 p, float3 q, float inten)
            {
                float r = length(p) * (1.0 + 0.55 * PGS_Fbm(q * 0.6 + 2.0, 3));
                float env = smoothstep(1.0, 0.25, r);
                float dens = saturate(0.55 + 0.8 * PGS_Fbm(q * 1.3 + 6.0, 5));
                return env * dens * inten * 2.5;
            }

            float3 Dwarf(float3 d, float2 p, float3 q, float inten)
            {
                float r = length(p) * (1.0 + 0.35 * PGS_Fbm(q * 0.7 + 4.0, 3));
                float env = exp(-r * r * 1.8) * saturate(1.15 - r);
                float bar = exp(-(p.x * p.x / 0.22 + p.y * p.y / 0.02));
                float clouds = saturate(0.45 + 0.8 * PGS_Fbm(q * 2.4, 5));
                float body = env * clouds + bar * 0.6;
                float stars = StarGrain(d, _Res * 0.25, 0.10 + 0.8 * saturate(body), 6.0);
                float knots = saturate(PGS_Noise(q * 7.0 + 7.0) * 3.0 - 2.0) * env;
                float2 t = p - float2(0.38, 0.16);
                float tarantula = exp(-dot(t, t) * 70.0);
                return 2.5 * (float3(0.74, 0.78, 0.95) * body * 0.10
                      + float3(0.85, 0.88, 1.00) * stars * body * 0.06
                      + float3(1.0, 0.22, 0.42) * knots * 0.18
                      + float3(1.0, 0.40, 0.60) * tarantula * 0.45) * inten;
            }

            // -------------------------------------------------------- sky

            float3 Sky(float3 d)
            {
                float3 N = normalize(_GalN.xyz);
                float3 C = normalize(_GalC.xyz - N * dot(_GalC.xyz, N));
                float3 E = cross(N, C);

                float lat = asin(clamp(dot(d, N), -1.0, 1.0));
                float lon = atan2(dot(d, E), dot(d, C));
                float centre = exp(-lon * lon / (2.0 * 0.85 * 0.85));

                // ---- Milky Way: diffuse light of star clouds
                float warp = PGS_Fbm(d * 3.1 + 11.0, 4) * 0.04;
                float b = lat + warp;
                float h = lerp(0.045, 0.100, centre);
                float disk = exp(-abs(b) / h) * 0.75 + exp(-b * b / (2.0 * (h * 1.8) * (h * 1.8))) * 0.25;
                float lonProfile = 0.30 + 0.70 * pow(0.5 + 0.5 * cos(lon), 1.5);
                float clouds  = PGS_Fbm(d * 8.0 + 3.7, 6);
                float clouds2 = PGS_Fbm(d * 24.0 + 7.1, 5);
                float mott = saturate(0.45 + 0.65 * clouds + 0.35 * clouds2);
                float band = disk * lonProfile;
                float3 diskCol = lerp(float3(0.74, 0.80, 1.00), float3(1.00, 0.93, 0.82), centre);
                float3 col = diskCol * band * (0.2 + 1.1 * mott * mott) * 0.28;

                // central bulge: warm, oval, much brighter
                float2 bq = float2(lon / 0.26, lat / 0.17);
                float bulge = exp(-pow(dot(bq, bq), 0.65) * 1.6);
                col += float3(1.00, 0.86, 0.66) * bulge * (0.85 + 0.3 * mott) * 0.65;

                // faint H-alpha regions along the plane
                float ha = saturate(PGS_Fbm(d * 7.0 + 21.3, 5) * 1.7 - 0.4);
                col += float3(0.85, 0.08, 0.18) * ha * ha * exp(-abs(b) / 0.045) * lonProfile * 0.08;

                // grain of unresolved stars - most of the band's light, as in photographs
                float dens = saturate(band * 1.3 + bulge);
                float g1 = StarGrain(d, _Res * 0.25, 0.02 + 0.80 * dens, 6.0);
                float g2 = StarGrain(d, _Res * 0.11, 0.015 + 0.30 * dens, 3.0);
                float3 starCol = lerp(float3(0.82, 0.88, 1.00), float3(1.00, 0.90, 0.76), centre);
                col += starCol * (g1 * (0.10 + 1.8 * dens * mott) + g2 * (0.25 + 1.0 * dens)) * 0.06 * _StarDust;

                // galactic cirrus: faint dusty wisps lit by the galaxy, everywhere off the plane
                float cir = PGS_Fbm(d * 3.0 + 31.0, 5);
                float cirrus = pow(saturate(cir * 1.5 - 0.1), 2.0) * (0.35 + 0.65 * PGS_Ridged(d * 8.0 + 4.0, 5));
                col += float3(0.55, 0.56, 0.64) * cirrus * 0.035 * (1.0 - 0.6 * saturate(band * 2.0));
                // and large faint H-alpha loops
                float loopN = PGS_Ridged(d * 1.6 + 17.0, 4);
                col += float3(0.80, 0.08, 0.16) * pow(loopN, 8.0) * 0.03 * saturate(PGS_Fbm(d * 2.0 + 5.0, 3) + 0.3);

                col *= _MilkyWay;

                // ---- nebulae
                float tauExtra = 0.0;
                int count = (int)_NebCount;
                for (int i = 0; i < count; i++)
                {
                    float3 n = _NebPos[i].xyz;
                    float R = _NebPos[i].w;
                    float cd = dot(d, n);
                    if (cd < cos(min(R * 1.8, 1.5))) continue;
                    float type = _NebA[i].x, seed = _NebA[i].y, inten = _NebA[i].z, ratio = _NebA[i].w;
                    float3 up = abs(n.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0);
                    float3 t1 = normalize(cross(n, up));
                    float3 t2 = cross(n, t1);
                    float sa = sin(_NebB[i].x), ca = cos(_NebB[i].x);
                    float3 a1 = ca * t1 + sa * t2;
                    float3 a2 = -sa * t1 + ca * t2;
                    float2 p = float2(dot(d, a1), dot(d, a2) / ratio) / R;
                    float3 q = d * (2.2 / R) + seed * 17.0;
                    float g = inten * _NebulaGain;

                    if      (type < 0.5) col += Emission(p, q, g, _NebB[i].y);
                    else if (type < 1.5) col += Reflection(p, q, seed, g);
                    else if (type < 2.5) col += Planetary(p, q, g);
                    else if (type < 3.5) col += Remnant(p, q, g);
                    else if (type < 4.5) tauExtra += DarkTau(p, q, inten);
                    else                 col += Dwarf(d, p, q, g);
                }

                // ---- dust: the Great Rift along the middle of the band, thin tendrils, dark nebulae
                float lanesBig = PGS_Fbm(d * 5.0 + 5.3 + warp * 20.0, 5);
                float rift = exp(-pow((lat - 0.010 * sin(lon * 3.0) - warp * 0.5) / (h * 0.55), 2.0));
                float riftTau = rift * saturate(0.5 + 0.9 * lanesBig) * 3.0;
                float tendr = PGS_Ridged(d * 13.0 + 2.1, 6);
                tendr = tendr * tendr * tendr;
                float tendrTau = tendr * exp(-pow(b / (h * 1.5), 2.0)) * 2.2 * saturate(0.4 + 0.8 * PGS_Fbm(d * 3.0 + 8.0, 3));
                float tau = _Dust * (riftTau + tendrTau) * (0.6 + 0.8 * centre) + tauExtra;
                col *= exp(-tau * float3(0.82, 0.95, 1.12));   // reddening: blue absorbed most

                return col;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(_FaceZ.xyz + i.xy.x * _FaceX.xyz + i.xy.y * _FaceY.xyz);
                if (_Debug > 0.5) return float4(d * 0.5 + 0.5, 1.0);
                return float4(max(Sky(d), 0.0), 1.0);
            }
            ENDCG
        }
    }
}
