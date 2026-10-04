// Instanced ground props (flora, boulders, ice spikes, basalt) from the Blender
// prop kit. Albedo comes from the mesh's vertex colours (authored in Blender,
// sRGB) times a per-planet tint; lighting follows the PG contract: per-pixel sun
// direction from the star's position, hemispheric ambient around the local up,
// and the camera's atmospheric fog so props sit in the same haze as the terrain.
Shader "PG/Prop"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        _Wrap ("Light Wrap", Range(0,1)) = 0.25
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
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float4 _PG_FogColor;
            float4 _PG_FogParams;
            fixed4 _Tint;
            float  _Wrap;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color  : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float3 wpos  : TEXCOORD0;
                float3 wnor  : TEXCOORD1;
                float3 color : TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                float4 w = mul(unity_ObjectToWorld, v.vertex);
                o.pos  = mul(UNITY_MATRIX_VP, w);
                o.wpos = w.xyz;
                o.wnor = mul((float3x3)unity_ObjectToWorld, v.normal);
                float3 c = v.color.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                c = GammaToLinearSpace(c);
                #endif
                o.color = c * _Tint.rgb;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wnor);
                float3 L = PG_SunDirAt(i.wpos);
                float3 up = normalize(_PG_LocalUp.xyz + float3(0, 1e-4, 0));

                float ndl = saturate((dot(n, L) + _Wrap) / (1.0 + _Wrap));
                float hemi = saturate(dot(n, up) * 0.5 + 0.5);
                float3 ambient = lerp(_PG_Ground.rgb, _PG_Ambient.rgb * 2.2 + 0.015, hemi);
                float3 col = i.color * (_PG_SunColor.rgb * ndl + ambient);

                float dist = length(_WorldSpaceCameraPos - i.wpos);
                float fogAmt = saturate((1.0 - exp(-dist * _PG_FogParams.x)) * _PG_FogParams.w);
                col = lerp(col, _PG_FogColor.rgb, fogAmt);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
