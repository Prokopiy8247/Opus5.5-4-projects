// Ship / prop surface shader with the PG global lighting contract:
// per-pixel sun direction from the star's position, ambient from the local up of
// the body the camera is near, a cheap sky reflection for metals, and optional
// object-space panel lines so large hull surfaces read as plated, not plastic.
Shader "PG/Ship"
{
    Properties
    {
        _BaseColor  ("Base Colour", Color) = (0.62,0.65,0.70,1)
        _Metallic   ("Metallic", Range(0,1)) = 0.7
        _Smoothness ("Smoothness", Range(0,1)) = 0.55
        _Emissive   ("Emissive", Color) = (0,0,0,1)
        _EmissiveBoost ("Emissive Boost", Range(0,16)) = 1
        _RimBoost   ("Rim Boost", Range(0,2)) = 0.15
        _PanelLines ("Panel Lines", Range(0,1)) = 0
        _PanelSize  ("Panel Size (m)", Float) = 1.1
        _LineWidth  ("Line Width (cells)", Range(0.001,0.1)) = 0.012
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

            float4 _PG_ShipGlow;   // a: engine load, boosts emissive
            float4 _PG_FogColor;
            float4 _PG_FogParams;

            fixed4 _BaseColor;
            float  _Metallic;
            float  _Smoothness;
            fixed4 _Emissive;
            float  _EmissiveBoost;
            float  _RimBoost;
            float  _PanelLines;
            float  _PanelSize;
            float  _LineWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wnor : TEXCOORD1;
                float3 opos : TEXCOORD2;
                float3 onor : TEXCOORD3;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnor = mul((float3x3)unity_ObjectToWorld, v.normal);
                o.opos = v.vertex.xyz;
                o.onor = v.normal;
                return o;
            }

            float PanelGrid(float2 uv)
            {
                uv.x += floor(uv.y) * 0.5;               // staggered plating
                float2 d = 0.5 - abs(frac(uv) - 0.5);    // distance to the cell edge
                float2 aa = fwidth(uv) * 0.75 + 1e-4;
                float2 l = 1.0 - smoothstep(_LineWidth - aa, _LineWidth + aa, d);
                return max(l.x, l.y);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wnor);
                float3 L = PG_SunDirAt(i.wpos);
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 up = normalize(_PG_LocalUp.xyz + float3(0, 1e-4, 0));

                float3 albedo = _BaseColor.rgb;
                float emissiveMask = 1.0;
                if (_PanelLines > 0.001)
                {
                    float3 on = normalize(i.onor);
                    float3 w = pow(abs(on), 4.0); w /= (w.x + w.y + w.z);
                    float3 p = i.opos / _PanelSize;
                    float g = PanelGrid(p.yz) * w.x + PanelGrid(p.xz) * w.y + PanelGrid(p.xy) * w.z;
                    float3 cell = floor(p + 0.5 * on);
                    float tint = PG_Hash(cell) - 0.5;
                    albedo *= (1.0 - g * 0.55 * _PanelLines) * (1.0 + tint * 0.07 * _PanelLines);
                    emissiveMask = (1.0 - g * 0.8 * _PanelLines) * (1.0 + tint * 0.5 * _PanelLines);
                }

                float ndl = saturate(dot(n, L));
                float hemi = saturate(dot(n, up) * 0.5 + 0.5);
                float3 ambient = lerp(_PG_Ground.rgb, _PG_Ambient.rgb * 2.2 + 0.015, hemi);

                float3 diffuse = albedo * (_PG_SunColor.rgb * ndl * (1.0 - _Metallic * 0.6) + ambient);

                float3 H = normalize(L + V);
                float specPow = exp2(_Smoothness * 10.0 + 1.0);
                float spec = pow(saturate(dot(n, H)), specPow) * (specPow + 8.0) / 25.0;
                float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, _Metallic);
                float fres = pow(1.0 - saturate(dot(n, V)), 5.0);
                float3 F = F0 + (1.0 - F0) * fres;
                float3 specular = _PG_SunColor.rgb * spec * F * ndl;

                // Environment: reflect the hemisphere (sky above, ground below).
                float3 R = reflect(-V, n);
                float skyAmt = saturate(dot(R, up) * 0.5 + 0.5);
                float3 env = lerp(_PG_Ground.rgb * 0.6, _PG_Ambient.rgb * 3.0 + 0.02, skyAmt);
                float3 reflection = env * F * _Smoothness;

                float3 rim = _PG_SunColor.rgb * fres * _RimBoost * saturate(dot(n, L) + 0.3);

                float3 col = diffuse + specular + reflection + rim;
                col += _Emissive.rgb * _EmissiveBoost * (1.0 + _PG_ShipGlow.a) * emissiveMask;

                float dist = length(_WorldSpaceCameraPos - i.wpos);
                float fogAmt = saturate((1.0 - exp(-dist * _PG_FogParams.x)) * _PG_FogParams.w);
                col = lerp(col, _PG_FogColor.rgb, fogAmt);

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
