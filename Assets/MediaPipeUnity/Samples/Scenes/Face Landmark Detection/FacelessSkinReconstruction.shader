Shader "Hidden/Faceless/SkinReconstruction"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "FacelessSkinCommon.cginc"
        sampler2D _MainTex, _KnownTex, _DonorTex, _HistoryTex, _GuideTex;
        float4 _MainTex_TexelSize;
        float2 _Direction;
        float _TemporalWeight, _LocalColorStrength, _HighlightSuppression;

        float4 Gaussian(float2 uv)
        {
            // Five bilinear taps = nine contiguous Gaussian texel taps.
            // Never jump across a large radius in the original video.
            float2 d = _Direction * abs(_MainTex_TexelSize.xy);
            float4 c = tex2D(_MainTex, uv) * 0.2270270270;
            c += (tex2D(_MainTex, uv + d * 1.3846153846) +
                  tex2D(_MainTex, uv - d * 1.3846153846)) * 0.3162162162;
            c += (tex2D(_MainTex, uv + d * 3.2307692308) +
                  tex2D(_MainTex, uv - d * 3.2307692308)) * 0.0702702703;
            return c;
        }
        float4 SkinDonorTap(float2 pixelPosition, float gaussianWeight)
        {
            float2 cameraUV = pixelPosition * _CameraSize.zw;
            float weight = TrustedSkinWeight(pixelPosition) * gaussianWeight;
            if (any(cameraUV < 0.0) || any(cameraUV > 1.0)) weight = 0.0;
            return float4(tex2D(_MainTex, cameraUV).rgb * weight, weight);
        }
        float4 fragDonors(v2f_img i) : SV_Target
        {
            int n = min((int)(i.uv.x * 6), 5);
            float2 donorPixel = _Donors[n].xy;
            float radius = _Donors[n].z;
            float4 moments = SkinDonorTap(donorPixel, 4.0);
            moments += SkinDonorTap(donorPixel+float2(radius,0),2.0) + SkinDonorTap(donorPixel-float2(radius,0),2.0)
                + SkinDonorTap(donorPixel+float2(0,radius),2.0) + SkinDonorTap(donorPixel-float2(0,radius),2.0);
            moments += SkinDonorTap(donorPixel+float2(radius,radius),1.0) + SkinDonorTap(donorPixel-float2(radius,radius),1.0)
                + SkinDonorTap(donorPixel+float2(radius,-radius),1.0) + SkinDonorTap(donorPixel+float2(-radius,radius),1.0);
            return float4(moments.rgb / max(moments.a, 0.00001), moments.a / 16.0);
        }
        float4 fragGuide(v2f_img i) : SV_Target
        {
            // A locally weighted color plane follows the left/right AND
            // upper/lower cheek illumination. It is a low-frequency trend,
            // not a single averaged color used to paint the entire face.
            float2 p = AtlasToPixel(i.uv);
            float total = 0.0;
            float2 sumDelta = float2(0,0);
            float3 sumColor = float3(0,0,0), sumXX = float3(0,0,0);
            float3 sumXColor = float3(0,0,0), sumYColor = float3(0,0,0);
            float3 minimumColor = float3(1,1,1), maximumColor = float3(0,0,0);
            [unroll] for (int n = 0; n < 6; n++)
            {
                float4 donor = tex2D(_DonorTex, float2((n+0.5)/6.0,0.5));
                float2 delta = (_Donors[n].xy - p) / max(_FrameOrigin.zw, float2(1,1));
                float donorConfidence = smoothstep(0.02, 0.35, donor.a);
                float weight = donorConfidence / pow(0.06 + dot(delta,delta), 1.5);
                total += weight; sumDelta += delta * weight; sumColor += donor.rgb * weight;
                sumXX += float3(delta.x*delta.x, delta.x*delta.y, delta.y*delta.y) * weight;
                sumXColor += donor.rgb * (delta.x * weight); sumYColor += donor.rgb * (delta.y * weight);
                if (donorConfidence > 0.0) { minimumColor = min(minimumColor,donor.rgb); maximumColor = max(maximumColor,donor.rgb); }
            }
            if (total < 0.0001) return float4(0,0,0,0);
            float2 meanDelta = sumDelta / total;
            float3 meanColor = sumColor / total;
            float3 covariance = sumXX / total - float3(meanDelta.x*meanDelta.x, meanDelta.x*meanDelta.y, meanDelta.y*meanDelta.y);
            // Ridge stabilizes near-profile donors whose projections coincide.
            covariance.x += 0.0001; covariance.z += 0.0001;
            float determinant = max(covariance.x*covariance.z-covariance.y*covariance.y,0.00000001);
            float3 covXColor = sumXColor/total - meanDelta.x*meanColor;
            float3 covYColor = sumYColor/total - meanDelta.y*meanColor;
            float3 slopeX = (covXColor*covariance.z-covYColor*covariance.y)/determinant;
            float3 slopeY = (covYColor*covariance.x-covXColor*covariance.y)/determinant;
            float3 guide = meanColor - slopeX*meanDelta.x - slopeY*meanDelta.y;
            float3 allowance = (maximumColor-minimumColor)*0.25 + 0.025;
            guide = clamp(guide, minimumColor-allowance, maximumColor+allowance);
            return float4(guide * _LocalColorStrength, 1);
        }
        float4 fragSeeds(v2f_img i) : SV_Target
        {
            float2 p = AtlasToPixel(i.uv);
            float2 uv = p * _CameraSize.zw;
            if (any(uv < 0.0) || any(uv > 1.0)) return 0;
            float confidence = TrustedSkinWeight(p);
            float3 source = tex2D(_MainTex, uv).rgb;
            float4 illumination = tex2D(_GuideTex, i.uv);
            float3 cheek = float3(0,0,0);
            float cheekWeight = 0.0;
            [unroll] for (int n = 0; n < 6; n++)
            {
                float4 donor = tex2D(_DonorTex, float2((n+0.5)/6.0,0.5));
                cheek += donor.rgb * donor.a; cheekWeight += donor.a;
            }
            // Luminance-relative rejection, no fixed skin-tone threshold.
            // Cheek color is a validity reference, never an opaque color overlay.
            if (cheekWeight > 0.0001)
            {
                float3 localReference = lerp(cheek/cheekWeight,
                    illumination.rgb / max(_LocalColorStrength, 0.01),
                    _LocalColorStrength * illumination.a * 0.7);
                float ratio = FacelessSkinLuma(source) / max(FacelessSkinLuma(localReference), 0.005);
                confidence *= smoothstep(0.24, 0.52, ratio) * (1.0 - smoothstep(2.5, 4.0, ratio));
            }
            // Small specular patches should not be stretched into the missing
            // skin as bright islands. Preserve the fitted broad lighting and
            // all negative residuals; use a smooth shoulder only for positive
            // outliers. Scaling RGB together preserves the sample's hue.
            float sourceLuma = FacelessSkinLuma(source);
            float referenceLuma = FacelessSkinLuma(illumination.rgb / max(_LocalColorStrength, 0.01));
            float shoulder = 0.035 + max(referenceLuma, 0.0) * 0.18;
            float excess = max(sourceLuma - referenceLuma - shoulder, 0.0);
            float compressedExcess = shoulder * (1.0 - exp(-excess / shoulder));
            float correction = (excess - compressedExcess) * saturate(_HighlightSuppression)
                * _LocalColorStrength * illumination.a;
            source *= max(sourceLuma - correction, 0.0) / max(sourceLuma, 0.00001);
            // Reconstruct the deviation from local illumination; adding the
            // guide back later preserves gradients across wide missing regions.
            float3 residual = source - illumination.rgb;
            return float4(residual * confidence, confidence);
        }
        float4 fragGaussian(v2f_img i) : SV_Target { return Gaussian(i.uv); }
        float4 fragNormalize(v2f_img i) : SV_Target
        {
            float4 moments = tex2D(_MainTex, i.uv);
            float3 fallback = float3(0,0,0);
            return float4(moments.a > 0.00001 ? moments.rgb / moments.a : fallback, 1);
        }
        float4 fragPull(v2f_img i) : SV_Target
        {
            float4 known = tex2D(_KnownTex, i.uv);
            float3 smoothColor = tex2D(_MainTex, i.uv).rgb;
            return float4(lerp(smoothColor, known.rgb / max(known.a, 0.00001),
                smoothstep(0.0, 0.85, known.a)), 1);
        }
        float4 fragRelax(v2f_img i) : SV_Target
        {
            float4 known = tex2D(_KnownTex, i.uv);
            float3 smoothColor = Gaussian(i.uv).rgb;
            return float4(lerp(smoothColor, known.rgb / max(known.a, 0.00001),
                smoothstep(0.0, 0.85, known.a)), 1);
        }
        float4 fragTemporal(v2f_img i) : SV_Target
        {
            float3 current = tex2D(_MainTex, i.uv).rgb;
            float3 previous = tex2D(_HistoryTex, i.uv).rgb;
            float change = abs(FacelessSkinLuma(current) - FacelessSkinLuma(previous));
            // Face-local history, reset on loss/reacquisition. Exposure changes
            // accelerate convergence rather than leaving a gray trail.
            float t = lerp(_TemporalWeight, 1.0, smoothstep(0.035, 0.16, change));
            // Use ONE temporal rule across the entire skin field. Resetting
            // only InteriorCoverage stamps its raster edges into the color:
            // a smooth exposure change becomes little squares / mouth arcs.
            // Soft-limit stale RGB history instead, independently of the mask.
            // Scaling the whole delta keeps its color direction and also
            // catches chromatic islands invisible to the luminance gate.
            // The current reconstructed gradient and all coverage stay intact.
            float3 historyDelta = previous - current;
            float3 absoluteDelta = abs(historyDelta);
            float peakDelta = max(absoluteDelta.x, max(absoluteDelta.y, absoluteDelta.z));
            previous = current + historyDelta / (1.0 + peakDelta / 0.008);
            return float4(lerp(previous, current, t), 1);
        }
        float4 fragRestoreColor(v2f_img i) : SV_Target
        {
            return float4(tex2D(_MainTex,i.uv).rgb + tex2D(_GuideTex,i.uv).rgb, 1);
        }
        ENDCG
        Pass // 0: compact cheek samples
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragDonors
            ENDCG
        }
        Pass // 1: premultiplied trusted skin + confidence, feature pixels excluded
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragSeeds
            ENDCG
        }
        Pass // 2: contiguous separable Gaussian for the confidence pyramid
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragGaussian
            ENDCG
        }
        Pass // 3: normalize coarsest reliable color
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragNormalize
            ENDCG
        }
        Pass // 4: pull reliable boundaries into finer scales
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragPull
            ENDCG
        }
        Pass // 5: relax interpolation while preserving reliable boundaries
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragRelax
            ENDCG
        }
        Pass // 6: stabilize only the reconstructed, face-local skin field
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragTemporal
            ENDCG
        }
        Pass // 7: spatially varying illumination from valid cheek patches
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragGuide
            ENDCG
        }
        Pass // 8: restore local illumination after residual reconstruction
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragRestoreColor
            ENDCG
        }
    }
    Fallback Off
}
