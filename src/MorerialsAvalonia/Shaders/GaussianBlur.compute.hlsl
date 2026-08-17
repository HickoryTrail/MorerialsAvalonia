cbuffer GaussianBlurFrame : register(b0)
{
    float4 OutputSizeAndDirection;
    float4 KernelParameters;
    float4 PackedWeights[17];
};

Texture2D<float4> SourceTexture : register(t0);
SamplerState LinearClampSampler : register(s0);
RWTexture2D<float4> BlurOutput : register(u0);

float LoadWeight(int offset)
{
    float4 packed = PackedWeights[offset >> 2];
    int component = offset & 3;
    if (component == 0)
        return packed.x;
    if (component == 1)
        return packed.y;
    if (component == 2)
        return packed.z;
    return packed.w;
}

float4 SampleSource(float2 uv)
{
    return SourceTexture.SampleLevel(LinearClampSampler, saturate(uv), 0.0);
}

[numthreads(8, 8, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
    uint2 outputSize = (uint2)OutputSizeAndDirection.xy;
    if (dispatchId.x >= outputSize.x || dispatchId.y >= outputSize.y)
        return;

    float2 texelSize = 1.0 / OutputSizeAndDirection.xy;
    float2 uv = (float2(dispatchId.xy) + 0.5) * texelSize;
    float2 direction = OutputSizeAndDirection.zw * texelSize;
    int radius = (int)KernelParameters.x;
    float4 color = SampleSource(uv) * LoadWeight(0);

    [loop]
    for (int offset = 1; offset <= radius; offset++)
    {
        float2 delta = direction * offset;
        float weight = LoadWeight(offset);
        color += (SampleSource(uv - delta) + SampleSource(uv + delta)) * weight;
    }

    BlurOutput[dispatchId.xy] = color;
}
