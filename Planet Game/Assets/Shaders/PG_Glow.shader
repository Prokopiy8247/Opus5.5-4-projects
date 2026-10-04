// Soft additive glow. Used for Helion's corona billboard, engine exhaust
// particles and atmospheric-entry plasma. The falloff is computed from the
// quad's UVs, so every particle is a soft round blob - never a square - and
// no texture asset is needed. Vertex colour (particle colour over lifetime)
// multiplies the result, alpha fading it out.
Shader "PG/Glow"
{
    Properties
    {
        _Color     ("Colour", Color) = (1,1,1,1)
        _Intensity ("Intensity", Float) = 1
        _Falloff   ("Falloff Power", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+30" "IgnoreProjector"="True" "PreviewType"="Plane" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;
            float  _Intensity;
            float  _Falloff;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 d = i.uv * 2.0 - 1.0;
                float r = saturate(length(d));
                float a = pow(1.0 - r, _Falloff);
                float3 col = _Color.rgb * i.color.rgb * (i.color.a * _Color.a * _Intensity * a);
                return fixed4(col, 0);
            }
            ENDCG
        }
    }
}
