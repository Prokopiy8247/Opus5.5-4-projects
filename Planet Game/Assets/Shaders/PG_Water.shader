// Ocean shell: a rigid sphere at radius + seaLevel (never a copy of the
// terrain). Rendered as the first transparent so the sea floor - which the
// terrain function already tints with shallow/deep water colours - shows
// through near the shore, and writes depth so later transparents sort against it.
Shader "PG/Water"
{
    Properties
    {
        _BaseColor    ("Deep Water Colour", Color) = (0.03,0.12,0.26,1)
        _PlanetCenter ("Planet Centre (render space)", Vector) = (0,0,0,0)
        _AtmoRadius   ("Atmosphere Radius", Float) = 4800
        _AtmoColor    ("Atmosphere Colour", Color) = (0.4,0.6,1,1)
        _HazeDensity  ("Haze Density", Float) = 0.0002
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-50" "IgnoreProjector"="True" }

        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            sampler2D _PG_Detail;
            fixed4 _BaseColor;
            float4 _PlanetCenter;
            float  _AtmoRadius;
            float4 _AtmoColor;
            float  _HazeDensity;

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
                float3 rel = i.wpos - _PlanetCenter.xyz;
                float3 up = normalize(rel);
                float3 V = _WorldSpaceCameraPos - i.wpos;
                float dist = length(V);
                V /= max(dist, 1e-3);

                // Moving ripples, sampled in planet space (stable under origin
                // shifts), faded with distance so far water does not shimmer.
                float t = _Time.y;
                float a = tex2D(_PG_Detail, (rel.xz + rel.y * 0.37) / 41.0 + t * 0.010).r;
                float b = tex2D(_PG_Detail, (rel.zy - rel.x * 0.41) / 27.0 - t * 0.014).r;
                float c = tex2D(_PG_Detail, (rel.yx + rel.z * 0.29) / 63.0 + t * 0.006).r;
                float3 jitter = float3(a, b, c) - 0.5;
                jitter -= up * dot(jitter, up);
                float ripple = 0.35 * saturate(1.0 - dist / 4000.0) + 0.05;
                float3 n = normalize(up + jitter * ripple);

                float3 L = PG_SunDirAt(i.wpos);
                float day = PG_Day(up, L);
                float ndl = saturate(dot(n, L));

                float fres = pow(1.0 - saturate(dot(n, V)), 5.0) * 0.92 + 0.04;
                float3 sky = _AtmoColor.rgb * (0.08 + 0.92 * day) * _PG_SunColor.rgb * 0.75 + _PG_Ambient.rgb;
                float3 body = _BaseColor.rgb * (_PG_SunColor.rgb * ndl * 0.55 + sky * 0.45 + _PG_Ambient.rgb);

                float3 H = normalize(L + V);
                float nh = saturate(dot(n, H));
                float3 spec = _PG_SunColor.rgb * (pow(nh, 900.0) * 6.0 + pow(nh, 60.0) * 0.18) * day;

                float3 col = lerp(body, sky, fres) + spec;
                float alpha = lerp(0.70, 0.97, fres);

                // Same aerial haze as the terrain, so the coastline does not
                // change colour against the land behind it.
                float3 ro = _WorldSpaceCameraPos - _PlanetCenter.xyz;
                float3 rd = -V;
                float2 ta = PG_RaySphere(ro, rd, _AtmoRadius);
                float path = max(min(dist, ta.y) - max(ta.x, 0.0), 0.0);
                float haze = 1.0 - exp(-path * _HazeDensity);
                float3 hazeCol = _AtmoColor.rgb * _PG_SunColor.rgb * (0.04 + 0.86 * day);
                col = lerp(col, hazeCol, haze * 0.9);
                alpha = lerp(alpha, 1.0, haze);

                return fixed4(col, alpha);
            }
            ENDCG
        }
    }
}
