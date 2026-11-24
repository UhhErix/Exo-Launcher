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

Texture2D<uint> prev_packed : register(t0);
Texture2D<uint> curr_packed : register(t1);
Texture2D<float2> coarse_mv : register(t2);

RWTexture2D<float2> out_mv : register(u0);

SamplerState linear_clamp : register(s0);

uint load_clamped(Texture2D<uint> tex, int2 p) {
    int2 c = clamp(p, int2(0, 0), int2((int)level_w - 1, (int)level_h - 1));
    return tex.Load(int3(c, 0));
}

[numthreads(8, 8, 1)]
void cs_flow_dp4a(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= level_w || tid.y >= level_h) return;

    float2 prior = float2(0.0, 0.0);
    if (has_coarse != 0) {
        float2 uv = (float2(tid.xy) + 0.5) / float2(level_w, level_h);
        prior = coarse_mv.SampleLevel(linear_clamp, uv, 0) * 2.0;
    }

    int2 base = int2(tid.xy);
    int2 center = base + int2(round(prior));

    uint p0 = load_clamped(prev_packed, base + int2(-1, -1));
    uint p1 = load_clamped(prev_packed, base + int2(-1,  0));
    uint p2 = load_clamped(prev_packed, base + int2(-1,  1));
    uint p3 = load_clamped(prev_packed, base + int2(-1,  2));

    uint sum_aa = 0;
    sum_aa = dot4add_u8packed(p0, p0, sum_aa);
    sum_aa = dot4add_u8packed(p1, p1, sum_aa);
    sum_aa = dot4add_u8packed(p2, p2, sum_aa);
    sum_aa = dot4add_u8packed(p3, p3, sum_aa);

    float best_cost = 1e30;
    int2 best_off = int2(0, 0);
    float c_left = 1e30;
    float c_right = 1e30;
    float c_up = 1e30;
    float c_down = 1e30;

    int r = search_radius;
    for (int dy = -r; dy <= r; dy++) {
        for (int dx = -r; dx <= r; dx++) {
            int2 cand = center + int2(dx, dy);
            uint q0 = load_clamped(curr_packed, cand + int2(-1, -1));
            uint q1 = load_clamped(curr_packed, cand + int2(-1,  0));
            uint q2 = load_clamped(curr_packed, cand + int2(-1,  1));
            uint q3 = load_clamped(curr_packed, cand + int2(-1,  2));

            uint sum_bb = 0;
            sum_bb = dot4add_u8packed(q0, q0, sum_bb);
            sum_bb = dot4add_u8packed(q1, q1, sum_bb);
            sum_bb = dot4add_u8packed(q2, q2, sum_bb);
            sum_bb = dot4add_u8packed(q3, q3, sum_bb);

            uint sum_ab = 0;
            sum_ab = dot4add_u8packed(p0, q0, sum_ab);
            sum_ab = dot4add_u8packed(p1, q1, sum_ab);
            sum_ab = dot4add_u8packed(p2, q2, sum_ab);
            sum_ab = dot4add_u8packed(p3, q3, sum_ab);

            float ssd = max(0.0, (float)sum_aa + (float)sum_bb - 2.0 * (float)sum_ab);
            float reg = reg_weight * (float)(dx * dx + dy * dy);
            float cost = ssd + reg;
            if (cost < best_cost) {
                best_cost = cost;
                best_off = int2(dx, dy);
            }
        }
    }

    int2 best_pos = center + best_off;
    [unroll]
    for (int dir = 0; dir < 4; dir++) {
        int2 d = (dir == 0) ? int2(-1, 0) : (dir == 1) ? int2(1, 0) : (dir == 2) ? int2(0, -1) : int2(0, 1);
        int2 cand = best_pos + d;
        uint q0 = load_clamped(curr_packed, cand + int2(-1, -1));
        uint q1 = load_clamped(curr_packed, cand + int2(-1,  0));
        uint q2 = load_clamped(curr_packed, cand + int2(-1,  1));
        uint q3 = load_clamped(curr_packed, cand + int2(-1,  2));
        uint sum_bb = 0;
        sum_bb = dot4add_u8packed(q0, q0, sum_bb);
        sum_bb = dot4add_u8packed(q1, q1, sum_bb);
        sum_bb = dot4add_u8packed(q2, q2, sum_bb);
        sum_bb = dot4add_u8packed(q3, q3, sum_bb);
        uint sum_ab = 0;
        sum_ab = dot4add_u8packed(p0, q0, sum_ab);
        sum_ab = dot4add_u8packed(p1, q1, sum_ab);
        sum_ab = dot4add_u8packed(p2, q2, sum_ab);
        sum_ab = dot4add_u8packed(p3, q3, sum_ab);
        float c = max(0.0, (float)sum_aa + (float)sum_bb - 2.0 * (float)sum_ab);
        if (dir == 0) c_left = c;
        else if (dir == 1) c_right = c;
        else if (dir == 2) c_up = c;
        else c_down = c;
    }

    float denom_x = (c_left + c_right - 2.0 * best_cost);
    float denom_y = (c_up + c_down - 2.0 * best_cost);
    float sub_x = abs(denom_x) > 1e-3 ? clamp(0.5 * (c_left - c_right) / denom_x, -0.5, 0.5) : 0.0;
    float sub_y = abs(denom_y) > 1e-3 ? clamp(0.5 * (c_up - c_down) / denom_y, -0.5, 0.5) : 0.0;

    float2 final_mv = float2(best_pos - base) + float2(sub_x, sub_y);
    out_mv[tid.xy] = final_mv;
}
