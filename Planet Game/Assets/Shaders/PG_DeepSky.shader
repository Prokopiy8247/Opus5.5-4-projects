// Deep-sky background: the Milky Way and nebulae baked by DeepSky.cs into the
// global cubemap _PG_SkyCube, drawn on a sphere of unit directions.
//
// Like PG/Stars the vertex shader rotates the directions by the view only, so
// the sky is infinitely far away and origin shifts cannot move it. Drawn
// before the stars and without depth, so every real object covers it; it
// fades with _PG_StarFade under a bright day sky.
Shader "PG/DeepSky"
{
    Properties
    {
        _Intensity ("Intensity", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "Queue"="Background-20" "IgnoreProjector"="True" }

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

            float _Intensity;
            samplerCUBE _PG_SkyCube;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                float3 viewDir = mul((float3x3)UNITY_MATRIX_V, v.vertex.xyz);
                o.pos = mul(UNITY_MATRIX_P, float4(viewDir * 100.0, 1.0));
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 c = texCUBE(_PG_SkyCube, normalize(i.dir)).rgb;
                return fixed4(c * _Intensity * _PG_StarFade, 0);
            }
            ENDCG
        }
    }
}
