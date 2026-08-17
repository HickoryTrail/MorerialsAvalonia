Texture2D<float4> CaptureTexture : register(t0);
SamplerState LinearSampler : register(s0);
RWStructuredBuffer<float> LuminanceOutput : register(u0);

cbuffer ForegroundLuminanceFrameConstants : register(b0)
{
    float4 CaptureSizeAndProbeCount;
    float4 ProbeBounds[128];
};

float ToLinear(float component)
{
    return component <= 0.04045f
        ? component / 12.92f
        : pow((component + 0.055f) / 1.055f, 2.4f);
}

float GetRelativeLuminance(float2 pixelPosition)
{
    float2 textureSize = max(CaptureSizeAndProbeCount.xy, float2(1.0f, 1.0f));
    float2 uv = saturate((pixelPosition + 0.5f) / textureSize);
    float3 srgb = CaptureTexture.SampleLevel(LinearSampler, uv, 0).rgb;
    float3 linearRgb = float3(
        ToLinear(srgb.r),
        ToLinear(srgb.g),
        ToLinear(srgb.b));
    return dot(linearRgb, float3(0.2126f, 0.7152f, 0.0722f));
}

[numthreads(1, 1, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
    uint index = dispatchId.x;
    uint probeCount = (uint)CaptureSizeAndProbeCount.z;
    if (index >= probeCount)
        return;

    float4 bounds = ProbeBounds[index];
    float2 minimum = min(bounds.xy, bounds.zw);
    float2 maximum = max(bounds.xy, bounds.zw);
    float2 size = max(maximum - minimum, float2(1.0f, 1.0f));
    float luminance = 0.0f;

    // 固定 3x3 小样本：每个控件仅产生一个 float 亮度结果。
    [unroll]
    for (uint row = 0; row < 3; row++)
    {
        [unroll]
        for (uint column = 0; column < 3; column++)
        {
            float2 fraction = (float2(column, row) + 0.5f) / 3.0f;
            luminance += GetRelativeLuminance(minimum + fraction * size);
        }
    }

    LuminanceOutput[index] = luminance / 9.0f;
}
