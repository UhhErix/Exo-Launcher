cbuffer Params : register(b0) {
    uint level_w;
    uint level_h;
    uint coarse_w;
    uint coarse_h;
    uint has_coarse;
    int search_radius;
    float reg_weight;
    uint pad0;
};

Texture2D<float> prev_luma : register(t0);
Texture2D<float> curr_luma : register(t1);
Texture2D<float2> coarse_mv : register(t2);

RWTexture2D<float2> out_mv : register(u0);

SamplerState linear_clamp : register(s0);

min16float load_luma(Texture2D<float> tex, int2 p) {
    int2 c = clamp(p, int2(0, 0), int2((int)level_w - 1, (int)level_h - 1));
    return (min16float)tex.Load(int3(c, 0));
}

[numthreads(8, 8, 1)]
void cs_flow_fp16(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= level_w || tid.y >= level_h) return;

    float2 prior = float2(0.0, 0.0);
    if (has_coarse != 0) {
        float2 uv = (float2(tid.xy) + 0.5) / float2(level_w, level_h);
        prior = coarse_mv.SampleLevel(linear_clamp, uv, 0) * 2.0;
    }

    int2 base = int2(tid.xy);
    int2 center = base + int2(round(prior));

    min16float4 row0 = min16float4(
        load_luma(prev_luma, base + int2(-1, -1)),
        load_luma(prev_luma, base + int2( 0, -1)),
        load_luma(prev_luma, base + int2( 1, -1)),
        load_luma(prev_luma, base + int2( 2, -1)));
    min16float4 row1 = min16float4(
        load_luma(prev_luma, base + int2(-1, 0)),
        load_luma(prev_luma, base + int2( 0, 0)),
        load_luma(prev_luma, base + int2( 1, 0)),
        load_luma(prev_luma, base + int2( 2, 0)));
    min16float4 row2 = min16float4(
        load_luma(prev_luma, base + int2(-1, 1)),
        load_luma(prev_luma, base + int2( 0, 1)),
        load_luma(prev_luma, base + int2( 1, 1)),
        load_luma(prev_luma, base + int2( 2, 1)));
    min16float4 row3 = min16float4(
        load_luma(prev_luma, base + int2(-1, 2)),
        load_luma(prev_luma, base + int2( 0, 2)),
        load_luma(prev_luma, base + int2( 1, 2)),
        load_luma(prev_luma, base + int2( 2, 2)));

    float best_cost = 1e30;
    int2 best_off = int2(0, 0);

    int r = search_radius;
    for (int dy = -r; dy <= r; dy++) {
        for (int dx = -r; dx <= r; dx++) {
            int2 cand = center + int2(dx, dy);
            min16float4 q0 = min16float4(
                load_luma(curr_luma, cand + int2(-1, -1)),
                load_luma(curr_luma, cand + int2( 0, -1)),
                load_luma(curr_luma, cand + int2( 1, -1)),
                load_luma(curr_luma, cand + int2( 2, -1)));
            min16float4 q1 = min16float4(
                load_luma(curr_luma, cand + int2(-1, 0)),
                load_luma(curr_luma, cand + int2( 0, 0)),
                load_luma(curr_luma, cand + int2( 1, 0)),
                load_luma(curr_luma, cand + int2( 2, 0)));
            min16float4 q2 = min16float4(
                load_luma(curr_luma, cand + int2(-1, 1)),
                load_luma(curr_luma, cand + int2( 0, 1)),
                load_luma(curr_luma, cand + int2( 1, 1)),
                load_luma(curr_luma, cand + int2( 2, 1)));
            min16float4 q3 = min16float4(
                load_luma(curr_luma, cand + int2(-1, 2)),
                load_luma(curr_luma, cand + int2( 0, 2)),
                load_luma(curr_luma, cand + int2( 1, 2)),
                load_luma(curr_luma, cand + int2( 2, 2)));

            min16float4 d0 = row0 - q0;
            min16float4 d1 = row1 - q1;
            min16float4 d2 = row2 - q2;
            min16float4 d3 = row3 - q3;

            min16float ssd = dot(d0, d0) + dot(d1, d1) + dot(d2, d2) + dot(d3, d3);
            float cost = (float)ssd * 65025.0 + reg_weight * (float)(dx * dx + dy * dy);
            if (cost < best_cost) {
                best_cost = cost;
                best_off = int2(dx, dy);
            }
        }
    }

    int2 best_pos = center + best_off;
    out_mv[tid.xy] = float2(best_pos - base);
}
