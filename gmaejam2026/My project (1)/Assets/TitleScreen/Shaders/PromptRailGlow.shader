Shader "UI/TitleScreen/PromptRailGlow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _BaseColor ("Base Color", Color) = (0.018, 0.012, 0.035, 1)
        _GlowColor ("Violet Glow", Color) = (0.95, 0.18, 1, 0.22)
        _CyanGlow ("Cyan Glow", Color) = (0.15, 0.92, 1, 0.12)
        _TopAlpha ("Top Alpha", Range(0, 1)) = 0.10
        _CenterAlpha ("Center Alpha", Range(0, 1)) = 0.30
        _BottomAlpha ("Bottom Alpha", Range(0, 1)) = 0.12
        _ScanStrength ("Scan Strength", Range(0, 0.2)) = 0.045
        _SheenStrength ("Sheen Strength", Range(0, 0.35)) = 0.12
        _EdgeFade ("Edge Fade", Range(0.001, 0.5)) = 0.18
        _ScrollSpeed ("Scroll Speed", Range(-2, 2)) = 0.10
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
        Blend SrcAlpha OneMinusSrcAlpha
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
            fixed4 _BaseColor;
            fixed4 _GlowColor;
            fixed4 _CyanGlow;
            float _TopAlpha;
            float _CenterAlpha;
            float _BottomAlpha;
            float _ScanStrength;
            float _SheenStrength;
            float _EdgeFade;
            float _ScrollSpeed;

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
                float2 uv = saturate(i.uv);
                float edgeFade = smoothstep(0.0, _EdgeFade, uv.y) * smoothstep(0.0, _EdgeFade, 1.0 - uv.y);
                float centerWeight = 1.0 - saturate(abs(uv.y - 0.46) * 2.25);
                centerWeight = smoothstep(0.0, 1.0, centerWeight);

                float verticalAlpha = lerp(_BottomAlpha, _TopAlpha, uv.y);
                verticalAlpha = max(verticalAlpha, _CenterAlpha * centerWeight);

                float timeOffset = _Time.y * _ScrollSpeed;
                float scan = (sin((uv.y + timeOffset) * 96.0) * 0.5 + 0.5) * _ScanStrength;
                float sheenLine = abs(frac(uv.x * 1.15 + uv.y * 0.45 - timeOffset) - 0.5);
                float sheen = smoothstep(0.10, 0.0, sheenLine) * _SheenStrength * edgeFade;
                float topGlow = smoothstep(0.56, 1.0, uv.y) * edgeFade;
                float bottomGlow = smoothstep(0.28, 0.0, uv.y) * edgeFade;

                fixed3 color = _BaseColor.rgb;
                color += _GlowColor.rgb * (topGlow * _GlowColor.a + sheen * _GlowColor.a);
                color += _CyanGlow.rgb * (bottomGlow * _CyanGlow.a + scan);

                fixed4 tex = tex2D(_MainTex, uv);
                fixed alpha = saturate((verticalAlpha + scan + sheen * 0.25) * edgeFade) * tex.a * i.color.a;
                return fixed4(color * i.color.rgb, alpha);
            }
            ENDCG
        }
    }
}
