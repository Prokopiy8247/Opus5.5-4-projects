// Atmosphere shell: single-scattering approximation by ray marching between
// analytic sphere intersections. Drawn on the back faces of a sphere at the top
// of the atmosphere, so the same shader is the glowing rim seen from space AND
// the sky dome seen from the ground - entering the air is a continuous change
// of the view ray, never a swap of objects or a fade to black.
//
// Output is premultiplied: on the day side the sky occludes the stars, at night
// it is nearly transparent.
Shader "PG/Atmosphere"
{
    Properties
    {
        _PlanetCenter ("Planet Centre (render space)", Vector) = (0,0,0,0)
        _PlanetRadius ("Planet Radius", Float) = 3000
        _AtmoRadius   ("Atmosphere Radius", Float) = 4800
        _AtmoColor    ("Rayleigh Colour", Color) = (0.38,0.60,0.98,1)
        _SunsetColor  ("Terminator Colour", Color) = (1.0,0.48,0.22,1)
        _Density      ("Density (1/m)", Float) = 0.0012
        _ScaleFrac    ("Scale height / shell thickness", Float) = 0.17
        _Intensity    ("Intensity", Float) = 1.25
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
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float4 _PlanetCenter;
            float  _PlanetRadius;
            float  _AtmoRadius;
            float4 _AtmoColor;
            float4 _SunsetColor;
            float  _Density;
            float  _ScaleFrac;
            float  _Intensity;

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

            fixed4 frag (v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos - _PlanetCenter.xyz;
                float3 rd = normalize(i.wpos - _WorldSpaceCameraPos);

                float2 ta = PG_RaySphere(ro, rd, _AtmoRadius);
                if (ta.y <= 0.0) return 0;
                float t0 = max(ta.x, 0.0);
                float t1 = ta.y;
                float2 tp = PG_RaySphere(ro, rd, _PlanetRadius);
                if (tp.y > tp.x && tp.x > 0.0) t1 = min(t1, tp.x);
                float seg = t1 - t0;
                if (seg <= 0.0) return 0;

                const int N = 12;
                float ds = seg / N;
                float Hs = max((_AtmoRadius - _PlanetRadius) * _ScaleFrac, 1.0);
                float optical = 0.0, lit = 0.0, dusk = 0.0;
                [unroll]
                for (int k = 0; k < N; k++)
                {
                    float3 p = ro + rd * (t0 + (k + 0.5) * ds);
                    float r = length(p);
                    float dens = exp(-max(r - _PlanetRadius, 0.0) / Hs) * ds;
                    float3 L = normalize(_PG_SunPos.xyz - (_PlanetCenter.xyz + p));
                    float mu = dot(p / r, L);
                    optical += dens;
                    lit  += dens * smoothstep(-0.24, 0.24, mu);
                    dusk += dens * smoothstep(-0.14, 0.02, mu) * (1.0 - smoothstep(0.02, 0.38, mu));
                }

                float alpha = 1.0 - exp(-optical * _Density);
                float litFrac = lit / max(optical, 1e-4);
                float duskFrac = saturate(dusk / max(optical, 1e-4) * 1.6);

                float3 Lc = PG_SunDirAt(_WorldSpaceCameraPos);
                float cosA = saturate(dot(rd, Lc));
                float mie = pow(cosA, 12.0) * 0.45 + pow(cosA, 220.0) * 1.6;

                float3 base = lerp(_AtmoColor.rgb, _SunsetColor.rgb, duskFrac);
                float3 col = (base * _Intensity + mie) * _PG_SunColor.rgb * litFrac * alpha;
                // Night side: a faint deep-blue airglow instead of a hole to
                // black space, so a sky looking toward the terminator darkens
                // smoothly (the planet-shadow band) rather than cutting out.
                col += _AtmoColor.rgb * 0.035 * alpha * (1.0 - litFrac);
                float a = alpha * (0.22 + 0.78 * litFrac);
                return float4(col, a);
            }
            ENDCG
        }
    }
}
