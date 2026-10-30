// Material Studio "Pick on model" highlight: a soft fill plus a constant-width screen-space
// outline (inverted hull pushed along the normal in clip space), drawn on a baked copy of
// the hovered/selected part with Graphics.DrawMesh. Never assigned to the avatar's materials.
Shader "Hidden/MaterialStudio/PickOutline"
{
    Properties
    {
        _Color ("Outline", Color) = (1, 0.8, 0.2, 1)
        _Fill ("Fill", Color) = (1, 0.8, 0.2, 0.15)
        _Width ("Width (clip space)", Float) = 0.004
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "FILL"
            Cull Back ZWrite Off ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Fill;
            float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
            float4 frag () : SV_Target { return _Fill; }
            ENDCG
        }

        Pass
        {
            Name "OUTLINE"
            Cull Front ZWrite Off ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float _Width;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            float4 vert (appdata v) : SV_POSITION
            {
                float4 pos = UnityObjectToClipPos(v.vertex);
                float3 wn = UnityObjectToWorldNormal(v.normal);
                float2 cn = mul((float3x3)UNITY_MATRIX_VP, wn).xy;
                float len = length(cn);
                if (len > 1e-5) pos.xy += (cn / len) * _Width * pos.w;
                return pos;
            }
            float4 frag () : SV_Target { return _Color; }
            ENDCG
        }
    }
}
