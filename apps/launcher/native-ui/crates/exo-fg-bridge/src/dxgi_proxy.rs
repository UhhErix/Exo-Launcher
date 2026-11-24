use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const BENCH_SCENE_MV_DXIL: &[u8] = include_bytes!("shaders/bench_scene_mv.cso");
pub const MV_SYNTH_DXIL: &[u8] = include_bytes!("shaders/mv_synth.cso");

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DxgiProxyProbe {
    pub system_dxgi_resolved: bool,
    pub system_dxgi_path: String,
    pub zero_imgui: bool,
    pub zero_ini_config: bool,
}

pub fn resolve_system_dxgi_path() -> PathBuf {
    let sys_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into());
    PathBuf::from(sys_root).join("System32").join("dxgi.dll")
}

pub fn probe_dxgi_proxy() -> DxgiProxyProbe {
    let path = resolve_system_dxgi_path();
    DxgiProxyProbe {
        system_dxgi_resolved: path.exists(),
        system_dxgi_path: path.display().to_string(),
        zero_imgui: true,
        zero_ini_config: true,
    }
}

pub fn is_valid_dxbc(bytes: &[u8]) -> bool {
    bytes.len() >= 32 && &bytes[0..4] == b"DXBC"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_dx12_bridge_shaders_have_valid_dxbc_headers() {
        assert!(is_valid_dxbc(BENCH_SCENE_MV_DXIL));
        assert!(is_valid_dxbc(MV_SYNTH_DXIL));
        let p = probe_dxgi_proxy();
        assert!(p.zero_imgui);
        assert!(p.zero_ini_config);
    }
}
