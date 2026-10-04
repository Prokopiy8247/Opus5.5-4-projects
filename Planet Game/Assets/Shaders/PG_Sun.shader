// Helion's photosphere. HDR emissive with limb darkening and slow granulation.
// Drawn after the atmosphere shells (it is behind them, they do not write
// depth) so a day sky does not dim the disc; planets still occlude it through
// the depth buffer.
Shader "PG/Sun"
{
    Properties
    {
        _Color     ("Colour", Color) = (1.0,0.83,0.58,1)
        _Intensity ("Intensity", Float) = 7
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "IgnoreProjector"="True" }

        Pass
        {
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float4 _Color;
            float  _Intensity;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wnor : TEXCOORD1;
                float3 dir  : TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnor = mul((float3x3)unity_ObjectToWorld, v.normal);
                o.dir  = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wnor);
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float mu = saturate(dot(n, V));
                float limb = 0.42 + 0.58 * pow(mu, 0.55);
                float gran = PG_Fbm(normalize(i.dir) * 38.0 + _Time.y * 0.05, 3);
                float3 col = _Color.rgb * _Intensity * limb * lerp(0.86, 1.12, gran);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
