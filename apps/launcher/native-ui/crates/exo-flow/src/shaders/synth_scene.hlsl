cbuffer Params : register(b0) {
    uint width;
    uint height;
    float time_sec;
    uint pad0;
};

RWTexture2D<float4> out_rgba : register(u0);

[numthreads(8, 8, 1)]
void cs_synth_scene(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= width || tid.y >= height) return;

    float2 p = float2(tid.xy);
    float2 res = float2(width, height);
    float2 uv = p / res;

    float pan_x = time_sec * 420.0;
    float pan_y = sin(time_sec * 1.3) * 90.0;
    float2 wp = p + float2(pan_x, pan_y);

    float tile = (fmod(floor(wp.x / 64.0) + floor(wp.y / 64.0), 2.0) < 1.0) ? 0.16 : 0.24;
    float fine = 0.05 * sin(wp.x * 0.08) * cos(wp.y * 0.08);
    float3 col = float3(tile + fine, tile * 1.15 + fine, tile * 1.45 + 0.08 * uv.y);

    float2 obj1 = float2(fmod(time_sec * 340.0 + 300.0, res.x), res.y * 0.42 + sin(time_sec * 2.1) * 120.0);
    float d1 = length(p - obj1);
    if (d1 < 85.0) {
        float rim = smoothstep(85.0, 72.0, d1);
        float stripe = 0.5 + 0.5 * sin((p.x - obj1.x) * 0.18);
        col = lerp(col, float3(0.92, 0.35 + 0.3 * stripe, 0.18), rim);
    }

    float2 obj2 = float2(res.x - fmod(time_sec * 260.0 + 200.0, res.x), res.y * 0.65);
    float2 q2 = abs(p - obj2);
    if (q2.x < 95.0 && q2.y < 55.0) {
        float pat = 0.5 + 0.5 * cos((p.y - obj2.y) * 0.22);
        col = float3(0.18, 0.72 + 0.2 * pat, 0.88);
    }

    float2 center = res * 0.5;
    float2 cd = abs(p - center);
    bool crosshair = ((cd.x < 22.0 && cd.x > 4.0 && cd.y < 2.0) || (cd.y < 22.0 && cd.y > 4.0 && cd.x < 2.0));
    if (crosshair) {
        col = float3(0.1, 1.0, 0.35);
    }

    if (p.x >= 32.0 && p.x <= 272.0 && p.y >= 32.0 && p.y <= 212.0) {
        bool border = (p.x < 36.0 || p.x > 268.0 || p.y < 36.0 || p.y > 208.0);
        if (border) {
            col = float3(0.95, 0.95, 0.98);
        } else {
            float2 mp = p - float2(32.0, 32.0);
            bool grid = (fmod(mp.x, 30.0) < 2.0 || fmod(mp.y, 30.0) < 2.0);
            col = grid ? float3(0.22, 0.28, 0.34) : float3(0.06, 0.08, 0.11);
            if (length(mp - float2(120.0, 90.0)) < 7.0) {
                col = float3(1.0, 0.25, 0.25);
            }
        }
    }

    if (p.x >= 40.0 && p.x <= 360.0 && p.y >= res.y - 84.0 && p.y <= res.y - 40.0) {
        bool border = (p.x < 43.0 || p.x > 357.0 || p.y < res.y - 81.0 || p.y > res.y - 43.0);
        if (border) {
            col = float3(0.92, 0.92, 0.96);
        } else if (p.x < 280.0) {
            col = float3(0.18, 0.85, 0.45);
        } else {
            col = float3(0.12, 0.14, 0.16);
        }
    }

    out_rgba[tid.xy] = float4(saturate(col), 1.0);
}
