cbuffer Params : register(b0) {
    uint width;
    uint height;
    float diff_thresh;
    float grad_thresh;
    float ema_decay;
    float mask_threshold;
    uint pass_dilate;
    uint pad0;
};

Texture2D<float4> prev_rgba : register(t0);
Texture2D<float4> curr_rgba : register(t1);
Texture2D<float2> prev_state : register(t2);
Texture2D<float> raw_mask_in : register(t3);

RWTexture2D<float2> next_state : register(u0);
RWTexture2D<float> out_mask : register(u1);

float luma(float3 c) {
    return dot(c, float3(0.299, 0.587, 0.114));
}

[numthreads(8, 8, 1)]
void cs_hud_mask(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= width || tid.y >= height) return;

    if (pass_dilate != 0) {
        float m = 0.0;
        [unroll]
        for (int dy = -1; dy <= 1; dy++) {
            [unroll]
            for (int dx = -1; dx <= 1; dx++) {
                int2 p = clamp(int2(tid.xy) + int2(dx, dy), int2(0, 0), int2((int)width - 1, (int)height - 1));
                m = max(m, raw_mask_in.Load(int3(p, 0)));
            }
        }
        out_mask[tid.xy] = m;
        return;
    }

    float3 c0 = prev_rgba.Load(int3(tid.xy, 0)).rgb;
    float3 c1 = curr_rgba.Load(int3(tid.xy, 0)).rgb;
    float diff = max(abs(c1.r - c0.r), max(abs(c1.g - c0.g), abs(c1.b - c0.b)));

    int2 pr = clamp(int2(tid.xy) + int2(1, 0), int2(0, 0), int2((int)width - 1, (int)height - 1));
    int2 pl = clamp(int2(tid.xy) + int2(-1, 0), int2(0, 0), int2((int)width - 1, (int)height - 1));
    int2 pd = clamp(int2(tid.xy) + int2(0, 1), int2(0, 0), int2((int)width - 1, (int)height - 1));
    int2 pu = clamp(int2(tid.xy) + int2(0, -1), int2(0, 0), int2((int)width - 1, (int)height - 1));
    float gx = abs(luma(curr_rgba.Load(int3(pr, 0)).rgb) - luma(curr_rgba.Load(int3(pl, 0)).rgb));
    float gy = abs(luma(curr_rgba.Load(int3(pd, 0)).rgb) - luma(curr_rgba.Load(int3(pu, 0)).rgb));
    float grad = max(gx, gy);

    float2 st = prev_state.Load(int3(tid.xy, 0));
    float is_static = (diff < diff_thresh) ? 1.0 : 0.0;
    float ema_static = lerp(is_static, st.x, ema_decay);
    float ema_grad = lerp(grad, st.y, ema_decay);

    float base_static = smoothstep(mask_threshold - 0.10, mask_threshold + 0.04, ema_static);
    float edge_boost = smoothstep(mask_threshold - 0.18, mask_threshold - 0.02, ema_static)
        * saturate(ema_grad / max(grad_thresh, 0.01));
    float hud_conf = max(base_static, edge_boost);

    next_state[tid.xy] = float2(ema_static, ema_grad);
    out_mask[tid.xy] = saturate(hud_conf);
}
