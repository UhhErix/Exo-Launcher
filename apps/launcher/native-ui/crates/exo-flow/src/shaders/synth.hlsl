cbuffer Params : register(b0) {
    uint dst_w;
    uint dst_h;
    uint src_w;
    uint src_h;
    float alpha_t;
    uint hud_mask_enabled;
    uint scaler_mode;
    float sharpness;
    float mv_scale_x;
    float mv_scale_y;
    uint pad0;
    uint pad1;
};

Texture2D<float4> prev_rgba : register(t0);
Texture2D<float4> curr_rgba : register(t1);
Texture2D<float2> mv_fwd : register(t2);
Texture2D<float2> mv_bwd : register(t3);
Texture2D<float> occ_tex : register(t4);
Texture2D<float> hud_mask_tex : register(t5);

RWTexture2D<float4> out_rgba : register(u0);

SamplerState linear_clamp : register(s0);
SamplerState point_clamp : register(s1);

float3 sample_warped(float2 uv) {
    float2 mv_f = mv_fwd.SampleLevel(linear_clamp, uv, 0) * float2(mv_scale_x, mv_scale_y);
    float2 mv_b = mv_bwd.SampleLevel(linear_clamp, uv, 0) * float2(mv_scale_x, mv_scale_y);
    float2 inv_src = 1.0 / float2(src_w, src_h);

    float2 uv0 = saturate(uv - alpha_t * mv_f * inv_src);
    float2 uv1 = saturate(uv + (1.0 - alpha_t) * mv_f * inv_src);
    float2 uv1_b = saturate(uv - (1.0 - alpha_t) * mv_b * inv_src);

    float3 c0 = prev_rgba.SampleLevel(linear_clamp, uv0, 0).rgb;
    float3 c1 = curr_rgba.SampleLevel(linear_clamp, lerp(uv1, uv1_b, 0.5), 0).rgb;
    float occ = occ_tex.SampleLevel(linear_clamp, uv, 0);

    float w1 = alpha_t;
    float w0 = 1.0 - alpha_t;
    float3 blended = lerp(c0, c1, w1);
    float3 disocc = (w0 > w1) ? c0 : c1;
    float3 synth = lerp(disocc, blended, saturate(occ));

    if (hud_mask_enabled != 0) {
        float hm = hud_mask_tex.SampleLevel(linear_clamp, uv, 0);
        float3 pinned = curr_rgba.SampleLevel(point_clamp, uv, 0).rgb;
        synth = lerp(synth, pinned, hm);
    }
    return synth;
}

float lanczos2(float x) {
    x = abs(x);
    if (x < 1e-5) return 1.0;
    if (x >= 2.0) return 0.0;
    const float PI = 3.14159265;
    float px = PI * x;
    return (sin(px) / px) * (sin(px * 0.5) / (px * 0.5));
}

[numthreads(8, 8, 1)]
void cs_synth(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= dst_w || tid.y >= dst_h) return;

    float2 uv = (float2(tid.xy) + 0.5) / float2(dst_w, dst_h);
    float3 col;

    if (scaler_mode == 2) {
        uint scale = max(1u, min(dst_w / max(src_w, 1u), dst_h / max(src_h, 1u)));
        float2 p = (floor(float2(tid.xy) / (float)scale) + 0.5) / float2(src_w, src_h);
        col = sample_warped(saturate(p));
    } else if (scaler_mode == 3) {
        float2 src_pos = uv * float2(src_w, src_h) - 0.5;
        float2 base = floor(src_pos);
        float2 frac_p = src_pos - base;
        float3 acc = float3(0.0, 0.0, 0.0);
        float wsum = 0.0;
        [unroll]
        for (int j = -1; j <= 2; j++) {
            float wy = lanczos2(frac_p.y - (float)j);
            [unroll]
            for (int i = -1; i <= 2; i++) {
                float w = lanczos2(frac_p.x - (float)i) * wy;
                float2 suv = saturate((base + float2(i, j) + 0.5) / float2(src_w, src_h));
                acc += sample_warped(suv) * w;
                wsum += w;
            }
        }
        col = saturate(acc / max(wsum, 1e-5));
    } else {
        col = sample_warped(uv);
        if (scaler_mode == 1 && sharpness > 0.001) {
            float2 step = 1.0 / float2(src_w, src_h);
            float3 n = sample_warped(saturate(uv + float2(0.0, -step.y)));
            float3 s = sample_warped(saturate(uv + float2(0.0,  step.y)));
            float3 e = sample_warped(saturate(uv + float2( step.x, 0.0)));
            float3 w = sample_warped(saturate(uv + float2(-step.x, 0.0)));
            float3 mn = min(col, min(min(n, s), min(e, w)));
            float3 mx = max(col, max(max(n, s), max(e, w)));
            float3 amp = saturate(min(mn, 1.0 - mx) / max(mx, 1e-4));
            float3 weight = -sqrt(amp) * lerp(0.125, 0.2, saturate(sharpness));
            col = saturate((col + (n + s + e + w) * weight) / (1.0 + 4.0 * weight));
        }
    }

    out_rgba[tid.xy] = float4(saturate(col), 1.0);
}
