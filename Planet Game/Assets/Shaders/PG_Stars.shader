// Background stars as screen-space point sprites.
//
// Each star is four vertices sharing one unit direction; the vertex shader
// rotates the direction by the view only (stars are infinitely far, origin
// shifts cannot move them), projects it, then offsets the corners by a fixed
// number of PIXELS. A star therefore covers ~1-3 pixels at any resolution or
// field of view, with a soft round gaussian - it can never read as a square.
// Drawn first, without depth, so every real object covers it.
Shader "PG/Stars"
{
    Properties
    {
        _Brightness ("Brightness", Float) = 1.6
    }
    SubShader
    {
        Tags { "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "Queue"="Background" "IgnoreProjector"="True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "PGCommon.cginc"

            float _Brightness;

            struct appdata
            {
                float4 vertex : POSITION;   // unit direction
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;  // corner in [-1, 1]
                float2 uv2    : TEXCOORD1;  // x: half size in pixels
            };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float3 viewDir = mul((float3x3)UNITY_MATRIX_V, v.vertex.xyz);
                float4 clip = mul(UNITY_MATRIX_P, float4(viewDir * 100.0, 1.0));
                clip.xy += v.uv * v.uv2.x * (2.0 / _ScreenParams.xy) * clip.w;
                o.pos = clip;
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float d2 = dot(i.uv, i.uv);
                float a = exp(-d2 * 4.0) * (1.0 - smoothstep(0.75, 1.0, d2));
                return fixed4(i.color.rgb * a * _Brightness * _PG_StarFade, 0);
            }
            ENDCG
        }
    }
}
