// Material Studio export helper: renders one face of a cubemap (or one direction, for the
// orientation check) so compressed, GPU-only cubemaps from a bundle can be read back.
Shader "Hidden/MaterialStudio/CubeFace"
{
    Properties
    {
        _Cube ("Cube", Cube) = "" {}
        _Face ("Face", Float) = 0
        _Flip ("Flip", Float) = 0
        _Dir ("Direction", Vector) = (0, 0, 1, 0)
        _Probe ("Probe", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            samplerCUBE _Cube;
            float _Face, _Flip, _Probe;
            float4 _Dir;

            float4 frag (v2f_img i) : SV_Target
            {
                if (_Probe > 0.5) return texCUBElod(_Cube, float4(normalize(_Dir.xyz), 0));
                float2 uv = i.uv;
                if (_Flip > 0.5) uv.y = 1 - uv.y;
                float u = uv.x * 2 - 1;
                float v = uv.y * 2 - 1;
                int f = (int)round(_Face);
                float3 d;
                if (f == 0) d = float3(1, -v, -u);
                else if (f == 1) d = float3(-1, -v, u);
                else if (f == 2) d = float3(u, 1, v);
                else if (f == 3) d = float3(u, -1, -v);
                else if (f == 4) d = float3(u, -v, 1);
                else d = float3(-u, -v, -1);
                return texCUBElod(_Cube, float4(d, 0));
            }
            ENDCG
        }
    }
}
