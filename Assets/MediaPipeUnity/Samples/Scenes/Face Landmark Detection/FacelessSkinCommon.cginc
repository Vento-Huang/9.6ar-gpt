#ifndef FACELESS_SKIN_COMMON
#define FACELESS_SKIN_COMMON
#define FACELESS_REGION_COUNT 15
float4 _CameraSize;
float4 _FrameOrigin, _FrameU, _FrameV;
float4 _Regions[FACELESS_REGION_COUNT], _RegionAxes[FACELESS_REGION_COUNT], _Boundary[36], _Donors[6];
float _ContourInset;
sampler2D _InteriorTex;

float2 AtlasToPixel(float2 uv)
{
    return _FrameOrigin.xy + (uv.x - 0.5) * _FrameU.xy + (uv.y - 0.5) * _FrameV.xy;
}
float2 PixelToAtlas(float2 p)
{
    p -= _FrameOrigin.xy;
    return float2(dot(p, _FrameU.xy) / dot(_FrameU.xy, _FrameU.xy),
                  dot(p, _FrameV.xy) / dot(_FrameV.xy, _FrameV.xy)) + 0.5;
}
// Only enclosed gaps and a transition INSIDE already opaque region cores.
// A bilinear sample can fall to 1/4 at the corner of a one-cell hole. Restore
// its plateau without enlarging the texture's nonzero support into soft edges.
float InteriorCoverage(float2 p)
{
    return saturate(tex2D(_InteriorTex, PixelToAtlas(p)).r * 4.0);
}
float RegionDistance(float2 p, int n)
{
    float2 delta = p - _Regions[n].xy;
    float2 axis = _RegionAxes[n].xy;
    float2 q = abs(float2(dot(delta, axis), dot(delta, float2(-axis.y, axis.x))));
    float2 radius = max(_Regions[n].zw, float2(0.01, 0.01));
    q /= radius;
    float2 q2 = q * q;
    float k = pow(dot(q2, q2), 0.25);
    if (k < 0.0001) return -min(radius.x, radius.y);
    float2 gradient = (q * q2) / (k * k * k * radius);
    return (k - 1.0) / max(length(gradient), 0.0001);
}
float RegionAlpha(float2 p, int n)
{
    return 1.0 - smoothstep(0.0, max(_RegionAxes[n].z, 0.5), RegionDistance(p, n));
}
// Signed distance to the face outline, used solely as an EXCLUSION guard.
// Positive coverage is always the union of local feature/fold regions.
float BoundaryDistance(float2 p)
{
    float d2 = 1e20;
    float inside = 0.0;
    [loop] for (int i = 0; i < 36; i++)
    {
        float2 a = _Boundary[i].xy, b = _Boundary[(i + 1) % 36].xy;
        float2 e = b - a, v = p - a;
        float2 nearest = v - e * saturate(dot(v, e) / max(dot(e, e), 0.00001));
        d2 = min(d2, dot(nearest, nearest));
        if ((a.y > p.y) != (b.y > p.y))
        {
            float crossX = a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y);
            if (p.x < crossX) inside = 1.0 - inside;
        }
    }
    return sqrt(d2) * (inside * 2.0 - 1.0);
}
float BoundaryGuard(float2 p)
{
    return smoothstep(_ContourInset, _ContourInset * 2.0, BoundaryDistance(p));
}
// Shared donor/seed eligibility. Exclude features BEFORE filtering so the
// reconstruction cannot copy eyes, lips or the newly covered fold shadows.
float TrustedSkinWeight(float2 p)
{
    float confidence = BoundaryGuard(p);
    float uncovered = 1.0;
    [unroll] for (int n = 0; n < FACELESS_REGION_COUNT; n++)
    {
        float distanceToRegion = RegionDistance(p, n);
        confidence *= smoothstep(0.0, max(_RegionAxes[n].z, 1.0) * 0.45, distanceToRegion);
        uncovered *= smoothstep(0.0, max(_RegionAxes[n].z, 0.5), distanceToRegion);
    }
    // Soft regions can jointly cover a seam even when no single region owns
    // its core. Do not reconstruct from those original feature/highlight
    // pixels: >=95% combined coverage excludes them; <=85% preserves the old
    // eligibility. This changes source selection, never the outside mask.
    confidence *= smoothstep(0.05, 0.15, uncovered);
    float heightOnFace = dot(p - _FrameOrigin.xy, normalize(_FrameV.xy));
    return confidence * (1.0 - InteriorCoverage(p))
        * (1.0 - smoothstep(_FrameV.z, _FrameV.z + _FrameOrigin.w * 0.025, heightOnFace));
}
// Unique name: UnityCG.cginc already declares Luminance. On targets where
// half/fixed map to float, overloading that name can become a redefinition.
float FacelessSkinLuma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
#endif
