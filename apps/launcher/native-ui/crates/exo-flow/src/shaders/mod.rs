pub const LUMA_PACK_DXIL: &[u8] = include_bytes!("luma_pack.cso");
pub const DOWNSAMPLE_DXIL: &[u8] = include_bytes!("downsample.cso");
pub const FLOW_DP4A_DXIL: &[u8] = include_bytes!("flow_dp4a.cso");
pub const FLOW_FP16_DXIL: &[u8] = include_bytes!("flow_fp16.cso");
pub const FLOW_SMOOTH_DXIL: &[u8] = include_bytes!("flow_smooth.cso");
pub const HUD_MASK_DXIL: &[u8] = include_bytes!("hud_mask.cso");
pub const SYNTH_DXIL: &[u8] = include_bytes!("synth.cso");
pub const SYNTH_SCENE_DXIL: &[u8] = include_bytes!("synth_scene.cso");

#[cfg(test)]
pub fn is_valid_dxbc_container(bytes: &[u8]) -> bool {
    bytes.len() >= 32 && &bytes[0..4] == b"DXBC"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_embedded_dxil_containers_have_valid_dxbc_headers() {
        let shaders = [
            ("luma_pack", LUMA_PACK_DXIL),
            ("downsample", DOWNSAMPLE_DXIL),
            ("flow_dp4a", FLOW_DP4A_DXIL),
            ("flow_fp16", FLOW_FP16_DXIL),
            ("flow_smooth", FLOW_SMOOTH_DXIL),
            ("hud_mask", HUD_MASK_DXIL),
            ("synth", SYNTH_DXIL),
            ("synth_scene", SYNTH_SCENE_DXIL),
        ];
        for (name, blob) in shaders {
            assert!(
                is_valid_dxbc_container(blob),
                "shader {name} missing DXBC header (len={})",
                blob.len()
            );
        }
    }
}
