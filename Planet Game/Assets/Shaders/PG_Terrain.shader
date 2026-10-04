// Planetary terrain (URP, unlit pass with PG global lighting).
//
// Vertices are planet-centre relative and the object transform is translation
// only, so object-space position IS the planet-local position: the local up is
// normalize(objectPos), detail is sampled in planet space (stable under origin
// shifts and identical at every LOD), and the sun direction is per pixel.
Shader "PG/Terrain"
{
    Properties
    {
        _Tint         ("Tint", Color) = (1,1,1,1)
        _PlanetCenter ("Planet Centre (render space)", Vector) = (0,0,0,0)
        _AtmoRadius   ("Atmosphere Radius", Float) = 4800
        _AtmoColor    ("Atmosphere Colour", Color) = (0.4,0.6,1,1)
        _HazeDensity  ("Haze Density", Float) = 0.0002
        _AmbientSky   ("Sky Ambient", Float) = 0.30
        _SpecAmount   ("Specular", Range(0,1)) = 0.04
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Cull Back
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            sampler2D _PG_Detail;

            fixed4 _Tint;
            float4 _PlanetCenter;
            float  _AtmoRadius;
            float4 _AtmoColor;
            float  _HazeDensity;
            float  _AmbientSky;
            float  _SpecAmount;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wnor : TEXCOORD1;
                float3 lpos : TEXCOORD2;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.wpos  = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnor  = mul((float3x3)unity_ObjectToWorld, v.normal);
                o.lpos  = v.vertex.xyz;
                o.color = v.color;
                return o;
            }

            float Detail(float3 p, float3 w, float scale)
            {
                return tex2D(_PG_Detail, p.yz / scale).r * w.x
                     + tex2D(_PG_Detail, p.xz / scale).r * w.y
                     + tex2D(_PG_Detail, p.xy / scale).r * w.z;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n  = normalize(i.wnor);
                float3 up = normalize(i.lpos);
                float3 L  = PG_SunDirAt(i.wpos);

                float ndl = saturate(dot(n, L));
                float day = PG_Day(up, L);

                // Two-scale planar-projected detail, weighted by the local up so
                // it never stretches on a slope.
                float3 w = abs(up); w /= (w.x + w.y + w.z);
                float detail = Detail(i.lpos, w, 21.0) * 0.55 + Detail(i.lpos, w, 173.0) * 0.45;
                float3 albedo = i.color.rgb * _Tint.rgb * (0.80 + detail * 0.40);

                float3 sky = _AtmoColor.rgb * _AmbientSky * day + _PG_Ambient.rgb;
                float3 ambient = lerp(_PG_Ground.rgb + sky * 0.30, sky, saturate(dot(n, up) * 0.5 + 0.5));

                float3 col = albedo * (_PG_SunColor.rgb * ndl + ambient);

                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 H = normalize(L + V);
                col += _PG_SunColor.rgb * pow(saturate(dot(n, H)), 48.0) * _SpecAmount * ndl;

                // Aerial haze: the length of the view ray that lies inside this
                // planet's atmosphere. From orbit it thickens toward the limb;
                // inside the air it is ordinary distance haze. One function for
                // both, so the transition is continuous.
                float3 ro = _WorldSpaceCameraPos - _PlanetCenter.xyz;
                float3 rel = i.wpos - _PlanetCenter.xyz;
                float3 rd = rel - ro;
                float dist = length(rd);
                rd /= max(dist, 1e-3);
                float2 ta = PG_RaySphere(ro, rd, _AtmoRadius);
                float path = max(min(dist, ta.y) - max(ta.x, 0.0), 0.0);
                float haze = 1.0 - exp(-path * _HazeDensity);
                float3 hazeCol = _AtmoColor.rgb * _PG_SunColor.rgb * (0.04 + 0.86 * day);
                col = lerp(col, hazeCol, haze * 0.9);

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
