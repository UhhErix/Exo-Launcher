cbuffer Params : register(b0) {
    uint dst_w;
    uint dst_h;
    uint src_w;
    uint src_h;
    uint pass_mode;
    uint pad0;
    uint pad1;
    uint pad2;
};

Texture2D<float4> src_rgba : register(t0);
Texture2D<float> src_luma : register(t1);

RWTexture2D<float> dst_luma : register(u0);
RWTexture2D<uint> dst_packed : register(u1);

float rgb_luma(float3 c) {
    return saturate(dot(c, float3(0.299, 0.587, 0.114)));
}

[numthreads(8, 8, 1)]
void cs_luma_pack(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= dst_w || tid.y >= dst_h) return;
    float l0 = rgb_luma(src_rgba.Load(int3(min(tid.x, src_w - 1), tid.y, 0)).rgb);
    float l1 = rgb_luma(src_rgba.Load(int3(min(tid.x + 1, src_w - 1), tid.y, 0)).rgb);
    float l2 = rgb_luma(src_rgba.Load(int3(min(tid.x + 2, src_w - 1), tid.y, 0)).rgb);
    float l3 = rgb_luma(src_rgba.Load(int3(min(tid.x + 3, src_w - 1), tid.y, 0)).rgb);
    dst_luma[tid.xy] = l0;
    uint b0 = (uint)(l0 * 255.0 + 0.5);
    uint b1 = (uint)(l1 * 255.0 + 0.5);
    uint b2 = (uint)(l2 * 255.0 + 0.5);
    uint b3 = (uint)(l3 * 255.0 + 0.5);
    dst_packed[tid.xy] = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
}

[numthreads(8, 8, 1)]
void cs_downsample(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= dst_w || tid.y >= dst_h) return;
    uint sx = tid.x * 2;
    uint sy = tid.y * 2;
    float acc = 0.0;
    [unroll]
    for (uint oy = 0; oy < 2; oy++) {
        [unroll]
        for (uint ox = 0; ox < 2; ox++) {
            uint px = min(sx + ox, src_w - 1);
            uint py = min(sy + oy, src_h - 1);
            acc += src_luma.Load(int3(px, py, 0));
        }
    }
    float l0 = acc * 0.25;
    dst_luma[tid.xy] = l0;

    uint b0 = (uint)(saturate(l0) * 255.0 + 0.5);
    uint packed = b0;
    [unroll]
    for (uint k = 1; k < 4; k++) {
        uint bx = min((tid.x + k) * 2, src_w - 1);
        float a = 0.0;
        [unroll]
        for (uint oy = 0; oy < 2; oy++) {
            [unroll]
            for (uint ox = 0; ox < 2; ox++) {
                uint px = min(bx + ox, src_w - 1);
                uint py = min(sy + oy, src_h - 1);
                a += src_luma.Load(int3(px, py, 0));
            }
        }
        uint bk = (uint)(saturate(a * 0.25) * 255.0 + 0.5);
        packed |= (bk << (k * 8));
    }
    dst_packed[tid.xy] = packed;
}
