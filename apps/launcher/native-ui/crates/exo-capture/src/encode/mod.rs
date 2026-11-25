pub mod mf_mft;
pub mod nvenc;

use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;

use crate::mux_mp4::VideoCodec;

#[derive(Debug, Clone, Copy)]
pub struct EncoderConfig {
    pub codec: VideoCodec,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_bps: u32,
    pub gop_frames: u32,
    pub hdr10: bool,
    pub low_latency: bool,
}

#[derive(Debug, Clone)]
pub struct EncodedVideoFrame {
    pub annex_b: Vec<u8>,
    pub timestamp_100ns: i64,
    pub duration_100ns: i64,
    pub is_keyframe: bool,
}

#[derive(Debug, Clone)]
pub struct EncoderCapabilities {
    pub nvenc_api_version: Option<(u32, u32)>,
    pub av1_hw: bool,
    pub hevc_hw: bool,
    pub h264_hw: bool,
    pub preferred_backend: String,
}

pub fn select_best_codec(caps: &EncoderCapabilities, want_hdr10: bool) -> VideoCodec {
    if caps.av1_hw {
        VideoCodec::Av1
    } else if caps.hevc_hw || (want_hdr10 && caps.hevc_hw) {
        VideoCodec::Hevc
    } else {
        VideoCodec::H264
    }
}

pub trait HardwareVideoEncoder {
    fn encoder_name(&self) -> &str;
    fn codec(&self) -> VideoCodec;
    fn encode_gpu_surface(
        &mut self,
        surface: &ID3D11Texture2D,
        timestamp_100ns: i64,
        duration_100ns: i64,
        force_keyframe: bool,
    ) -> Result<Vec<EncodedVideoFrame>, String>;
    fn drain(&mut self) -> Result<Vec<EncodedVideoFrame>, String>;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codec_selection_prefers_av1_then_hevc_then_h264() {
        let all = EncoderCapabilities {
            nvenc_api_version: Some((12, 2)),
            av1_hw: true,
            hevc_hw: true,
            h264_hw: true,
            preferred_backend: "nvenc".to_string(),
        };
        assert_eq!(select_best_codec(&all, false), VideoCodec::Av1);

        let rtx3070 = EncoderCapabilities {
            nvenc_api_version: Some((12, 2)),
            av1_hw: false,
            hevc_hw: true,
            h264_hw: true,
            preferred_backend: "nvenc".to_string(),
        };
        assert_eq!(select_best_codec(&rtx3070, true), VideoCodec::Hevc);

        let legacy = EncoderCapabilities {
            nvenc_api_version: None,
            av1_hw: false,
            hevc_hw: false,
            h264_hw: true,
            preferred_backend: "mf_mft".to_string(),
        };
        assert_eq!(select_best_codec(&legacy, false), VideoCodec::H264);
    }
}
