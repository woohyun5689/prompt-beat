Shader "UI/TitleScreen/LogoSparkle"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Core ("Core", Range(0.001, 0.2)) = 0.045
        _RaySharpness ("Ray Sharpness", Range(8, 80)) = 34
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha One
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _Core;
            float _RaySharpness;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv - 0.5;
                float dist = length(p);
                float core = smoothstep(_Core, 0.0, dist);
                float horizontal = exp(-abs(p.y) * _RaySharpness) * smoothstep(0.5, 0.0, abs(p.x));
                float vertical = exp(-abs(p.x) * _RaySharpness) * smoothstep(0.5, 0.0, abs(p.y));
                float diamond = smoothstep(0.45, 0.0, abs(p.x) + abs(p.y)) * 0.35;
                float alpha = saturate(core + horizontal * 0.55 + vertical * 0.55 + diamond);

                fixed4 tex = tex2D(_MainTex, i.uv);
                return fixed4(i.color.rgb, alpha * i.color.a * tex.a);
            }
            ENDCG
        }
    }
}
