Shader "RhythmGame/SpineAfterimage"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Afterimage Tint", Color) = (0.25, 0.9, 1.0, 0.25)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct VertexToFragment
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _TintColor;

            VertexToFragment vert(AppData input)
            {
                VertexToFragment output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color;
                return output;
            }

            fixed4 frag(VertexToFragment input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * input.color;
                fixed fade = saturate(_TintColor.a);
                color.rgb *= _TintColor.rgb * fade;
                color.a *= fade;
                return color;
            }
            ENDCG
        }
    }
}
