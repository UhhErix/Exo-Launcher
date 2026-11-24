cbuffer SynthParams : register(b0) {
    uint width;
    uint height;
    float alpha_t;
    uint pad0;
};

Texture2D<float4> prev_color : register(t0);
Texture2D<float4> curr_color : register(t1);
Texture2D<float2> curr_mv : register(t2);
RWTexture2D<float4> out_color : register(u0);

SamplerState linear_clamp : register(s0);

[numthreads(8, 8, 1)]
void cs_mv_synth(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= width || tid.y >= height) return;

    float2 inv_dim = 1.0 / float2(width, height);
    float2 uv = (float2(tid.xy) + 0.5) * inv_dim;
    float2 mv = curr_mv.Load(int3(tid.xy, 0));

    float2 uv0 = saturate(uv - alpha_t * mv * inv_dim);
    float2 uv1 = saturate(uv + (1.0 - alpha_t) * mv * inv_dim);

    float3 c0 = prev_color.SampleLevel(linear_clamp, uv0, 0).rgb;
    float3 c1 = curr_color.SampleLevel(linear_clamp, uv1, 0).rgb;

    float mv_mag = length(mv);
    float3 blended = (mv_mag < 0.01) ? curr_color.Load(int3(tid.xy, 0)).rgb : lerp(c0, c1, alpha_t);
    out_color[tid.xy] = float4(blended, 1.0);
}
