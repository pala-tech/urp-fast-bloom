Shader "Hidden/Bloom"
{
    Properties
    {
        _MainTex ("Bloom", 2D) = "black" {}
    }

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityInput.hlsl"

        // Textures
        // TEXTURE2D_X(_BlitTexture);
        TEXTURE2D_X(_SourceTex);
        TEXTURE2D_X(_NoiseTex);
        // Sampler is provided by included shader libraries

        // Parameters
        float2 _TexelSize;
        float _Intensity;
        float _Threshold;
        float3 _Curve;
        float2 _NoiseTexScale;

        // Box filter reading from _BlitTexture around uv using _TexelSize
        half4 BoxFilter(float2 uv)
        {
            half4 sum = 0.0;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(_TexelSize.x, 0.0));
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(_TexelSize.x, 0.0));
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(0.0, _TexelSize.y));
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0.0, _TexelSize.y));
            return sum * 0.25;
        }

        float GetNoise(float2 uv)
        {
            float noise = SAMPLE_TEXTURE2D_X(_NoiseTex, sampler_LinearClamp, uv * _NoiseTexScale).a;
            noise = noise * 2.0 - 1.0;
            return noise / 255.0;
        }

        half4 FragPrefilter(Varyings input) : SV_Target
        {
            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            half br = max(c.r, max(c.g, c.b));
            half rq = clamp(br - _Curve.x, 0.0, _Curve.y);
            rq = _Curve.z * rq * rq;
            c.rgb *= max(rq, br - _Threshold) / max(br, 0.0001);
            return half4(c.rgb, c.a);
        }

        half4 FragBlur(Varyings input) : SV_Target
        {
            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            return BoxFilter(uv);
        }

        half4 FragFinal(Varyings input) : SV_Target
        {
            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            half4 bloom = BoxFilter(uv);
            return bloom * _Intensity;
        }

        half4 FragCombine(Varyings input) : SV_Target
        {
            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            half3 bloom = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            bloom += GetNoise(uv);
            half4 source = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, uv);
            return half4(source.rgb + bloom, source.a);
        }

        ENDHLSL

        Pass
        {
            Name "Prefilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass 
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            ENDHLSL
        }

        Pass
        {
            Name "Upsample"
            Blend One One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            ENDHLSL
        }

        Pass
        {
            Name "Final"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFinal
            ENDHLSL
        }

        Pass
        {
            Name "Combine"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCombine
            ENDHLSL
        }
    }
}