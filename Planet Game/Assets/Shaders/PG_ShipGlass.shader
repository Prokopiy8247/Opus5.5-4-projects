// Cockpit glass: tinted, fresnel-reflective, sun glint. Transparent and drawn
// after the far-away transparents so it composites over the sky and planets.
// Back faces are culled, so from the cockpit camera (inside the canopy) the
// glass does not obstruct the view.
Shader "PG/ShipGlass"
{
    Properties
    {
        _Tint     ("Tint", Color) = (0.10,0.22,0.30,1)
        _Opacity  ("Base Opacity", Range(0,1)) = 0.45
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+40" "IgnoreProjector"="True" }

        Pass
        {
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            fixed4 _Tint;
            float  _Opacity;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wnor : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnor = mul((float3x3)unity_ObjectToWorld, v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wnor);
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 L = PG_SunDirAt(i.wpos);
                float3 up = normalize(_PG_LocalUp.xyz + float3(0, 1e-4, 0));

                float fres = pow(1.0 - saturate(dot(n, V)), 4.0);
                float3 R = reflect(-V, n);
                float3 env = lerp(_PG_Ground.rgb, _PG_Ambient.rgb * 4.0 + 0.04, saturate(dot(R, up) * 0.5 + 0.5));
                float3 H = normalize(L + V);
                float glint = pow(saturate(dot(n, H)), 300.0) * 4.0;

                float3 col = _Tint.rgb * (_PG_SunColor.rgb * saturate(dot(n, L)) * 0.3 + 0.05)
                           + env * (0.25 + fres) + _PG_SunColor.rgb * glint;
                float a = saturate(_Opacity + fres * 0.5 + glint);
                return fixed4(col, a);
            }
            ENDCG
        }
    }
}
