cbuffer SceneParams : register(b0) {
    uint width;
    uint height;
    float time_sec;
    float dt_sec;
    uint workload_iters;
    uint pad0;
    uint pad1;
    uint pad2;
};

RWTexture2D<float4> out_color : register(u0);
RWTexture2D<float2> out_mv : register(u1);

[numthreads(8, 8, 1)]
void cs_game_scene(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= width || tid.y >= height) return;

    float2 uv = (float2(tid.xy) + 0.5) / float2(width, height);
    float2 vel_px = float2(180.0 * dt_sec, 48.0 * cos(time_sec * 1.5) * dt_sec);
    float2 world = float2(tid.xy) - float2(180.0 * time_sec, 32.0 * sin(time_sec * 1.5));

    float acc = 0.0;
    float2 p = world * 0.015;
    for (uint i = 0; i < workload_iters; i++) {
        float fi = (float)i * 0.37;
        acc += sin(p.x * 1.3 + fi) * cos(p.y * 1.1 - fi * 0.7);
        p = float2(p.x * 0.99 - p.y * 0.14, p.x * 0.14 + p.y * 0.99);
    }
    float norm = saturate(0.5 + 0.5 * (acc / max((float)workload_iters, 1.0)));

    float3 col = float3(
        0.16 + 0.65 * norm,
        0.22 + 0.55 * frac(world.x * 0.008),
        0.34 + 0.50 * (1.0 - norm)
    );

    float2 mv = vel_px;
    float2 center = float2(0.5, 0.5);
    float2 d = abs(uv - center) * float2(width, height);
    if ((d.x < 14.0 && d.y < 1.5) || (d.y < 14.0 && d.x < 1.5)) {
        col = float3(0.1, 1.0, 0.35);
        mv = float2(0.0, 0.0);
    }

    out_color[tid.xy] = float4(col, 1.0);
    out_mv[tid.xy] = mv;
}
