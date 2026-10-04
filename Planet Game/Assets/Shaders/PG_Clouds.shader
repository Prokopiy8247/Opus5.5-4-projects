// Cloud shell at radius + cloudHeight. Coverage is procedural noise over the
// planet-local direction (the mesh is a unit sphere), so it is the same
// clouds from orbit and from below, slowly drifting. Two-sided so the layer is
// seen from above and from underneath; faded near the camera's own altitude so
// flying through it never shows a hard plane.
Shader "PG/Clouds"
{
    Properties
    {
        _PlanetCenter ("Planet Centre (render space)", Vector) = (0,0,0,0)
        _CloudRadius  ("Cloud Radius", Float) = 4000
        _Coverage     ("Coverage", Range(0,1)) = 0.5
        _CloudScale   ("Scale", Float) = 11
        _CloudColor   ("Colour", Color) = (1,1,1,1)
        _Seed         ("Seed Offset", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+20" "IgnoreProjector"="True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float4 _PlanetCenter;
            float  _CloudRadius;
            float  _Coverage;
            float  _CloudScale;
            float4 _CloudColor;
            float4 _Seed;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 dir  : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.dir  = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float t = _Time.y * 0.0035;
                float3 q = dir * _CloudScale + _Seed.xyz;
                float warp = PG_Fbm(q * 0.45 + float3(t, 0.0, -t), 3);
                float n = PG_Fbm(q + warp * 1.7 + float3(t * 2.0, 0.0, -t), 5);

                float thr = lerp(0.68, 0.44, _Coverage);
                float cov = smoothstep(thr, thr + 0.11, n);
                float dense = smoothstep(thr + 0.05, thr + 0.24, n);

                float3 L = PG_SunDirAt(i.wpos);
                float day = PG_Day(dir, L);
                float3 lightCol = _PG_SunColor.rgb * day * lerp(0.75, 1.05, dense) + _PG_Ambient.rgb * 3.0;
                float3 col = _CloudColor.rgb * lightCol * lerp(1.0, 0.78, dense * (1.0 - day * 0.5));

                float camR = length(_WorldSpaceCameraPos - _PlanetCenter.xyz);
                float nearFade = saturate(abs(camR - _CloudRadius) / 160.0);
                // Grazing view: thin the deck toward the limb (from space) and
                // toward the horizon (from below) so the layer never reads as a
                // ring of flakes floating off the planet.
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float grazing = smoothstep(0.02, 0.30, abs(dot(V, dir)));
                float a = cov * lerp(0.55, 0.92, dense) * nearFade * grazing;
                return fixed4(col, a);
            }
            ENDCG
        }
    }
}
