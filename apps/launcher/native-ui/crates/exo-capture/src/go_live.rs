use std::io::{self, Write};

use crate::mux_mp4::VideoCodec;

pub fn fit_within(width: u32, height: u32, max_width: u32, max_height: u32) -> (u32, u32) {
    let w_in = width.max(64) & !1;
    let h_in = height.max(64) & !1;
    let scale = (max_width as f64 / w_in as f64)
        .min(max_height as f64 / h_in as f64)
        .min(1.0);
    let w = (((w_in as f64 * scale).round() as u32) & !1).clamp(64, max_width & !1);
    let h = (((h_in as f64 * scale).round() as u32) & !1).clamp(64, max_height & !1);
    (w, h)
}

pub fn build_stream_plan(
    width: u32,
    height: u32,
    fps: u32,
    codec: VideoCodec,
    allow_fallback: bool,
) -> (Vec<(u32, u32, u32)>, Vec<VideoCodec>) {
    let w = width.max(64) & !1;
    let h = height.max(64) & !1;
    let f = fps.clamp(5, 120);
    let mut steps = vec![(w, h, f)];
    if f > 60 {
        steps.push((w, h, 60));
    }
    if f > 30 {
        steps.push((w, h, 30));
    }
    if h > 1080 {
        let scaled_w = (((w as f64 * 1080.0 / h as f64).round() as u32) & !1).max(64);
        steps.push((scaled_w, 1080, f.min(60)));
    }
    let (sw, sh) = fit_within(w, h, 1280, 720);
    steps.push((sw, sh, f.min(30)));
    steps.dedup();
    let codecs = if codec == VideoCodec::H264 || !allow_fallback {
        vec![codec]
    } else {
        vec![codec, VideoCodec::H264]
    };
    (steps, codecs)
}

pub fn write_go_live_frame<W: Write>(
    writer: &mut W,
    annex_b: &[u8],
    timestamp_100ns: i64,
    is_keyframe: bool,
) -> io::Result<()> {
    let mut header = [0u8; 16];
    header[0..4].copy_from_slice(&(annex_b.len() as u32).to_le_bytes());
    header[4..8].copy_from_slice(&(if is_keyframe { 1u32 } else { 0u32 }).to_le_bytes());
    header[8..16].copy_from_slice(&timestamp_100ns.to_le_bytes());
    writer.write_all(&header)?;
    writer.write_all(annex_b)?;
    writer.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn go_live_frame_header_matches_exo_capture_pipe_contract() {
        let mut buf = Vec::new();
        let payload = [0u8, 0, 0, 1, 0x65, 0x88, 0x84];
        write_go_live_frame(&mut buf, &payload, 123_456_789, true).expect("write ok");
        assert_eq!(buf.len(), 16 + payload.len());
        assert_eq!(u32::from_le_bytes(buf[0..4].try_into().unwrap()), 7);
        assert_eq!(u32::from_le_bytes(buf[4..8].try_into().unwrap()), 1);
        assert_eq!(
            i64::from_le_bytes(buf[8..16].try_into().unwrap()),
            123_456_789
        );
        assert_eq!(&buf[16..], &payload);
    }

    #[test]
    fn stream_step_down_plan_caps_resolution_and_fps() {
        let (steps, codecs) = build_stream_plan(2560, 1440, 120, VideoCodec::Hevc, true);
        assert_eq!(steps[0], (2560, 1440, 120));
        assert!(steps.contains(&(1920, 1080, 60)));
        assert_eq!(*steps.last().unwrap(), (1280, 720, 30));
        assert_eq!(codecs, vec![VideoCodec::Hevc, VideoCodec::H264]);
    }
}
