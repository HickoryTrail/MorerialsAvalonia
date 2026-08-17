struct LiquidGlassRegion
{
    // Avalonia supplies center.xy, half-size.zw, and the corner radius.
    float4 Bounds;
    float4 Geometry;
    // x = f(x) power, yzw = a/b/c.
    float4 RefractionCurve;
    // x = d, y = noise, z = use blurred backdrop.
    float4 OpticalEffects;
    // x = weight, y = bias, z = edge0, w = edge1.
    float4 Glow;
    // x = intensity, y = border width, z = reflected-light falloff width.
    float4 Highlight;
};

cbuffer CompositeFrame : register(b0)
{
    float4 OutputAndCaptureSize;
    float4 RegionCountAndPadding;
    LiquidGlassRegion Regions[16];
};

Texture2D<float4> CaptureTexture : register(t0);
Texture2D<float4> BlurTexture : register(t1);
SamplerState LinearClampSampler : register(s0);
RWTexture2D<float4> CompositeOutput : register(u0);

float RoundedRectangleDistance(float2 position, float2 halfSize, float radius)
{
    float2 corner = abs(position) - (halfSize - radius);
    float outside = length(max(corner, 0.0)) - radius;
    float inside = min(max(corner.x, corner.y), 0.0);
    return outside + inside;
}

float2 SafeNormalize(float2 value)
{
    float magnitudeSquared = dot(value, value);
    return magnitudeSquared > 0.000001
        ? value * rsqrt(magnitudeSquared)
        : float2(0.0, -1.0);
}

float2 RoundedRectangleNormal(float2 position, float2 halfSize, float radius)
{
    float2 corner = abs(position) - (halfSize - radius);
    float2 outsideCorner = max(corner, 0.0);
    float2 direction = sign(position);

    if (dot(outsideCorner, outsideCorner) > 0.000001)
        return direction * SafeNormalize(outsideCorner);

    float useHorizontalNormal = step(corner.y, corner.x);
    return direction * float2(useHorizontalNormal, 1.0 - useHorizontalNormal);
}

float3 SampleBackdrop(float2 windowPixel, bool useBlur)
{
    float2 size = OutputAndCaptureSize.zw;
    float2 uv = clamp(windowPixel / size, 0.5 / size, 1.0 - 0.5 / size);
    float3 result = 0.0;
    [branch]
    if (useBlur)
        result = BlurTexture.SampleLevel(LinearClampSampler, uv, 0.0).rgb;
    else
        result = CaptureTexture.SampleLevel(LinearClampSampler, uv, 0.0).rgb;
    return result;
}

float RefractionCurve(float distanceInside, LiquidGlassRegion region)
{
    const float e = 2.718281828459045;
    float a = region.RefractionCurve.y;
    float b = region.RefractionCurve.z;
    float c = max(region.RefractionCurve.w, 0.0001);
    float d = region.OpticalEffects.x;
    return 1.0 - b * pow(c * e, -d * distanceInside - a);
}

float ReferenceNoise(float2 coordinate)
{
    return frac(sin(dot(coordinate, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
}

[numthreads(8, 8, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= (uint)OutputAndCaptureSize.x ||
        dispatchId.y >= (uint)OutputAndCaptureSize.y)
        return;

    float2 pixel = float2(dispatchId.xy) + 0.5;
    int regionCount = (int)RegionCountAndPadding.x;
    int selected = -1;
    float selectedDistance = 0.0;
    float selectedCoverage = 0.0;
    float2 selectedLocal = 0.0;

    [loop]
    for (int index = 0; index < regionCount; index++)
    {
        LiquidGlassRegion region = Regions[index];
        float2 local = pixel - region.Bounds.xy;
        float2 halfSize = region.Bounds.zw;
        if (all(halfSize > 0.0) && any(abs(local) > halfSize + 1.0))
            continue;

        float signedDistance = RoundedRectangleDistance(
            local,
            halfSize,
            min(region.Geometry.x, min(halfSize.x, halfSize.y)));
        float coverage = saturate(0.75 - signedDistance);
        if (coverage > 0.0)
        {
            selected = index;
            selectedDistance = signedDistance;
            selectedCoverage = coverage;
            selectedLocal = local;
        }
    }

    float4 result = 0.0;
    if (selected >= 0)
    {
        LiquidGlassRegion glass = Regions[selected];
        float2 halfSize = max(glass.Bounds.zw, 0.001);
        float2 normalizedLocal = selectedLocal / halfSize;
        float radius = min(glass.Geometry.x, min(halfSize.x, halfSize.y));
        float refractionScale = max(min(halfSize.x, halfSize.y), 0.001);
        float distanceInside = max(-selectedDistance, 0.0) / refractionScale;

        float curve = RefractionCurve(distanceInside, glass);
        float coordinateScale = pow(max(curve, 0.0001), glass.RefractionCurve.x);
        float displacement = clamp(
            (coordinateScale - 1.0) * refractionScale,
            -refractionScale,
            refractionScale);
        float2 refractionNormal = RoundedRectangleNormal(
            selectedLocal,
            halfSize,
            radius);
        float2 samplePixel = pixel + refractionNormal * displacement;
        float3 color = SampleBackdrop(samplePixel, glass.OpticalEffects.z > 0.5);

        [branch]
        if (abs(glass.OpticalEffects.y) > 0.001)
            color += ReferenceNoise(pixel * 0.001) * glass.OpticalEffects.y;

        float glowMultiplier = 1.0;
        [branch]
        if (abs(glass.Glow.x) > 0.001 || abs(glass.Glow.y) > 0.001)
        {
            float angularGlow = sin(atan2(normalizedLocal.y, normalizedLocal.x) - 0.5);
            float glowMask = smoothstep(glass.Glow.z, glass.Glow.w, distanceInside);
            glowMultiplier = angularGlow * glass.Glow.x * glowMask + 1.0 + glass.Glow.y;
        }

        color *= glowMultiplier;

        [branch]
        if (glass.Highlight.x > 0.001)
        {
            float edgeDistance = max(-selectedDistance, 0.0);
            float borderWidth = max(glass.Highlight.y, 0.001);
            float reflectionFalloffWidth = max(glass.Highlight.z, 0.001);
            float borderMask = 1.0 - smoothstep(
                max(borderWidth - 0.5, 0.0),
                borderWidth + 0.5,
                edgeDistance);

            float reflectionFalloff = (1.0 - borderMask) *
                (1.0 - smoothstep(
                    borderWidth,
                    borderWidth + reflectionFalloffWidth,
                    edgeDistance));
            // Screen-space Y grows downward. Opposed 45-degree lights make the upper-left
            // and lower-right edges reflect symmetrically.
            float2 lightDirection = SafeNormalize(float2(-1.0, -1.0));
            float lightFacing = abs(dot(refractionNormal, lightDirection));
            // A quadratic lobe keeps the 45-degree reflection band broad without becoming uniform.
            float specularReflection = lightFacing * lightFacing;
            float highlightAmount = saturate(glass.Highlight.x) *
                saturate((borderMask + reflectionFalloff * 0.35) * specularReflection);
            color = lerp(color, 1.0, highlightAmount);
        }

        result = float4(color * selectedCoverage, selectedCoverage);
    }

    // Avalonia imports the shared texture bottom-up. Keep the single final flip.
    uint outputY = (uint)OutputAndCaptureSize.y - 1 - dispatchId.y;
    CompositeOutput[uint2(dispatchId.x, outputY)] = result;
}
