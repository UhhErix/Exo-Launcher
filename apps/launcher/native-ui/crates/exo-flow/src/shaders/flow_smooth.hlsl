cbuffer Params : register(b0) {
    uint width;
    uint height;
    float edge_sigma;
    float temporal_alpha;
    float occ_threshold;
    uint has_prev_mv;
    uint pad0;
    uint pad1;
};

Texture2D<float2> raw_mv_fwd : register(t0);
Texture2D<float2> raw_mv_bwd : register(t1);
Texture2D<float> luma_tex : register(t2);
Texture2D<float2> prev_smooth_mv : register(t3);

RWTexture2D<float2> out_smooth_fwd : register(u0);
RWTexture2D<float> out_occ : register(u1);

SamplerState linear_clamp : register(s0);

[numthreads(8, 8, 1)]
void cs_flow_smooth(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= width || tid.y >= height) return;

    float center_l = luma_tex.Load(int3(tid.xy, 0));
    float2 mv_sum = float2(0.0, 0.0);
    float w_sum = 0.0;

    [unroll]
    for (int dy = -1; dy <= 1; dy++) {
        [unroll]
        for (int dx = -1; dx <= 1; dx++) {
            int2 p = clamp(int2(tid.xy) + int2(dx, dy), int2(0, 0), int2((int)width - 1, (int)height - 1));
            float nl = luma_tex.Load(int3(p, 0));
            float dl = nl - center_l;
            float w = exp(-(dl * dl) / max(edge_sigma * edge_sigma, 1e-4)) * ((dx == 0 && dy == 0) ? 2.0 : 1.0);
            mv_sum += raw_mv_fwd.Load(int3(p, 0)) * w;
            w_sum += w;
        }
    }

    float2 smooth = mv_sum / max(w_sum, 1e-5);
    if (has_prev_mv != 0) {
        float2 prev = prev_smooth_mv.Load(int3(tid.xy, 0));
        float jump = length(smooth - prev);
        float blend = jump < 4.0 ? temporal_alpha : 0.0;
        smooth = lerp(smooth, prev, blend);
    }

    float2 fwd_uv = (float2(tid.xy) + smooth + 0.5) / float2(width, height);
    float2 bwd = raw_mv_bwd.SampleLevel(linear_clamp, fwd_uv, 0);
    float fb_err = length(smooth + bwd);
    float occ = saturate(1.0 - fb_err / max(occ_threshold, 0.25));

    out_smooth_fwd[tid.xy] = smooth;
    out_occ[tid.xy] = occ;
}
