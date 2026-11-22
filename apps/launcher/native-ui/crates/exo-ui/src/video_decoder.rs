use exo_rpc::DiscordVideoFrame;
use rune::Pixels;
use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Instant;

const VIDEO_QUEUE_CAPACITY: usize = 8;

#[derive(Debug, Clone, Default)]
pub struct VideoStreamStats {
    pub codec: String,
    pub width: u32,
    pub height: u32,
    pub decoded_frames: u64,
    pub dropped_frames: u64,
    pub fps: f32,
    pub hardware: bool,
    pub error: Option<String>,
}

pub struct VideoPacket {
    pub data: Vec<u8>,
    pub timestamp_ms: f64,
    pub keyframe: bool,
    pub flip: bool,
    pub codec: String,
}

struct QueueState {
    packets: VecDeque<VideoPacket>,
    closed: bool,
}

struct VideoPacketQueue {
    cap: usize,
    state: Mutex<QueueState>,
    cond: Condvar,
}

impl VideoPacketQueue {
    fn new(cap: usize) -> Self {
        Self {
            cap: cap.max(1),
            state: Mutex::new(QueueState {
                packets: VecDeque::with_capacity(cap.max(1)),
                closed: false,
            }),
            cond: Condvar::new(),
        }
    }

    fn push_drop_oldest(&self, pkt: VideoPacket) -> bool {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if guard.closed {
            return false;
        }
        let dropped = if guard.packets.len() >= self.cap {
            guard.packets.pop_front();
            true
        } else {
            false
        };
        guard.packets.push_back(pkt);
        self.cond.notify_one();
        dropped
    }

    fn pop_blocking(&self) -> Option<VideoPacket> {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(pkt) = guard.packets.pop_front() {
                return Some(pkt);
            }
            if guard.closed {
                return None;
            }
            guard = self.cond.wait(guard).unwrap_or_else(|e| e.into_inner());
        }
    }

    fn close(&self) {
        let mut guard = self.state.lock().unwrap_or_else(|e| e.into_inner());
        guard.closed = true;
        guard.packets.clear();
        self.cond.notify_all();
    }
}

struct QueueOwner(Arc<VideoPacketQueue>);

impl Drop for QueueOwner {
    fn drop(&mut self) {
        self.0.close();
    }
}

#[derive(Clone)]
pub struct VideoStreamHandle {
    pub pixels: Pixels,
    pub stats: Arc<Mutex<VideoStreamStats>>,
    queue: Arc<VideoPacketQueue>,
    _owner: Arc<QueueOwner>,
}

impl VideoStreamHandle {
    pub fn spawn(codec: &str) -> Self {
        let pixels = Pixels::new();
        let stats = Arc::new(Mutex::new(VideoStreamStats {
            codec: codec.to_string(),
            ..Default::default()
        }));
        let queue = Arc::new(VideoPacketQueue::new(VIDEO_QUEUE_CAPACITY));
        let owner = Arc::new(QueueOwner(Arc::clone(&queue)));

        let px_bg = pixels.clone();
        let st_bg = Arc::clone(&stats);
        let q_bg = Arc::clone(&queue);
        let init_codec = codec.to_string();
        std::thread::Builder::new()
            .name("exo-mf-video-dec".into())
            .spawn(move || decode_loop(q_bg, px_bg, st_bg, &init_codec))
            .ok();

        Self {
            pixels,
            stats,
            queue,
            _owner: owner,
        }
    }

    pub fn push(&self, pkt: VideoPacket) {
        if self.queue.push_drop_oldest(pkt)
            && let Ok(mut s) = self.stats.lock()
        {
            s.dropped_frames += 1;
        }
    }

    pub fn snapshot_stats(&self) -> VideoStreamStats {
        self.stats.lock().map(|s| s.clone()).unwrap_or_default()
    }
}

#[derive(Clone, Default)]
pub struct VideoFrameRouter {
    handles: Arc<Mutex<HashMap<(String, String), VideoStreamHandle>>>,
}

impl VideoFrameRouter {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register(&self, key: (String, String), handle: VideoStreamHandle) {
        self.handles
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(key, handle);
    }

    pub fn remove(&self, key: &(String, String)) {
        self.handles
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(key);
    }

    pub fn clear(&self) {
        self.handles
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clear();
    }

    pub fn try_push(&self, frame: DiscordVideoFrame) -> Result<(), DiscordVideoFrame> {
        let handle = self
            .handles
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .get(&(frame.source.clone(), frame.user_id.clone()))
            .cloned();
        if let Some(handle) = handle {
            handle.push(VideoPacket {
                data: frame.data,
                timestamp_ms: frame.timestamp,
                keyframe: frame.keyframe,
                flip: frame.flip,
                codec: frame.codec,
            });
            Ok(())
        } else {
            Err(frame)
        }
    }
}

pub fn nv12_to_rgba(
    nv12: &[u8],
    width: u32,
    height: u32,
    stride: usize,
    flip_x: bool,
    out: &mut Vec<u8>,
) -> bool {
    let w = width as usize;
    let h = height as usize;
    if w == 0 || h == 0 || stride < w {
        return false;
    }
    let y_plane_bytes = stride.saturating_mul(h);
    let uv_rows = h.div_ceil(2);
    let needed = y_plane_bytes.saturating_add(stride.saturating_mul(uv_rows));
    if nv12.len() < needed {
        return false;
    }
    out.resize(w * h * 4, 255);
    let uv_base = y_plane_bytes;
    for row in 0..h {
        let y_row = &nv12[row * stride..row * stride + w];
        let uv_row_off = uv_base + (row >> 1) * stride;
        let out_row_off = row * w * 4;
        for (col, &y_byte) in y_row.iter().enumerate() {
            let dst_col = if flip_x { w - 1 - col } else { col };
            let y = y_byte as i32;
            let uv_idx = uv_row_off + (col & !1);
            let u = nv12[uv_idx] as i32 - 128;
            let v = nv12[uv_idx + 1] as i32 - 128;

            let c = (y - 16).max(0);
            let r = (298 * c + 459 * v + 128) >> 8;
            let g = (298 * c - 55 * u - 136 * v + 128) >> 8;
            let b = (298 * c + 541 * u + 128) >> 8;

            let o = out_row_off + dst_col * 4;
            out[o] = r.clamp(0, 255) as u8;
            out[o + 1] = g.clamp(0, 255) as u8;
            out[o + 2] = b.clamp(0, 255) as u8;
            out[o + 3] = 255;
        }
    }
    true
}

pub fn make_demo_stream_pixels(width: u32, height: u32) -> Pixels {
    let w = width as usize;
    let h = height as usize;
    let mut nv12 = vec![0u8; w * h + w * (h / 2)];
    for y in 0..h {
        for x in 0..w {
            let fx = x as f32 / w as f32;
            let fy = y as f32 / h as f32;
            let horizon = ((fy - 0.56) * 4.5).clamp(-1.0, 1.0);
            let grid = ((x % 48 == 0) || (y % 48 == 0)) && fy > 0.52;
            let hud_bar = fy < 0.08 || fy > 0.90;
            let luma = if hud_bar {
                24
            } else if grid {
                68
            } else {
                (28.0 + (1.0 - horizon.abs()) * 42.0 + (fx * 22.0)) as u8
            };
            nv12[y * w + x] = luma.clamp(16, 235);
        }
    }
    let uv_off = w * h;
    for y in 0..(h / 2) {
        for x in (0..w).step_by(2) {
            let fx = x as f32 / w as f32;
            let fy = (y * 2) as f32 / h as f32;
            let u = (132.0 + (0.5 - fx) * 28.0) as u8;
            let v = (122.0 + (fy - 0.4) * 24.0) as u8;
            nv12[uv_off + y * w + x] = u;
            nv12[uv_off + y * w + x + 1] = v;
        }
    }
    let mut rgba = Vec::new();
    let pixels = Pixels::new();
    if nv12_to_rgba(&nv12, width, height, w, false, &mut rgba) {
        pixels.set(width, height, rgba);
    }
    pixels
}

#[cfg(windows)]
fn decode_loop(
    queue: Arc<VideoPacketQueue>,
    pixels: Pixels,
    stats: Arc<Mutex<VideoStreamStats>>,
    initial_codec: &str,
) {
    use windows::Win32::Media::MediaFoundation::{
        MF_VERSION, MFSTARTUP_LITE, MFShutdown, MFStartup,
    };
    use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};

    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        if let Err(e) = MFStartup(MF_VERSION, MFSTARTUP_LITE) {
            if let Ok(mut s) = stats.lock() {
                s.error = Some(format!("Media Foundation startup failed: {e}"));
            }
            CoUninitialize();
            return;
        }
    }

    let mut active_codec = initial_codec.to_string();
    let mut decoder = MfDecoder::create(&active_codec);
    if let Ok(ref dec) = decoder
        && let Ok(mut s) = stats.lock()
    {
        s.hardware = dec.hardware;
        s.codec = active_codec.clone();
    } else if let Err(ref e) = decoder
        && let Ok(mut s) = stats.lock()
    {
        s.error = Some(e.clone());
    }

    let mut rgba = Vec::new();
    let mut fps_window_start = Instant::now();
    let mut fps_window_frames = 0u32;

    while let Some(pkt) = queue.pop_blocking() {
        if pkt.codec != active_codec && !pkt.codec.is_empty() {
            active_codec = pkt.codec.clone();
            decoder = MfDecoder::create(&active_codec);
            if let Ok(mut s) = stats.lock() {
                s.codec = active_codec.clone();
                s.hardware = decoder.as_ref().map(|d| d.hardware).unwrap_or(false);
                s.error = decoder.as_ref().err().cloned();
            }
        }
        let Ok(ref mut dec) = decoder else {
            continue;
        };
        if let Ok(Some((w, h))) = dec.decode_into(&pkt, &mut rgba) {
            pixels.set(w, h, std::mem::take(&mut rgba));
            fps_window_frames += 1;
            let elapsed = fps_window_start.elapsed().as_secs_f32();
            if let Ok(mut s) = stats.lock() {
                s.width = w;
                s.height = h;
                s.decoded_frames += 1;
                if elapsed >= 1.0 {
                    s.fps = fps_window_frames as f32 / elapsed;
                    fps_window_frames = 0;
                    fps_window_start = Instant::now();
                }
            }
        }
    }

    drop(decoder);
    unsafe {
        let _ = MFShutdown();
        CoUninitialize();
    }
}

#[cfg(windows)]
struct MfDecoder {
    transform: windows::Win32::Media::MediaFoundation::IMFTransform,
    width: u32,
    height: u32,
    stride: usize,
    provides_samples: bool,
    output_buf_size: u32,
    hardware: bool,
    seen_keyframe: bool,
}

#[cfg(windows)]
impl MfDecoder {
    fn create(codec: &str) -> Result<Self, String> {
        use std::ptr::null_mut;
        use windows::Win32::Media::MediaFoundation::{
            CODECAPI_AVLowLatencyMode, IMFActivate, IMFTransform, MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE,
            MFCreateMediaType, MFMediaType_Video, MFT_CATEGORY_VIDEO_DECODER,
            MFT_ENUM_FLAG_HARDWARE, MFT_ENUM_FLAG_SORTANDFILTER, MFT_ENUM_FLAG_SYNCMFT,
            MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, MFT_MESSAGE_NOTIFY_START_OF_STREAM,
            MFT_REGISTER_TYPE_INFO, MFTEnumEx, MFVideoFormat_H264, MFVideoFormat_HEVC,
        };
        use windows::Win32::System::Com::CoTaskMemFree;

        let subtype = if codec.eq_ignore_ascii_case("h265") || codec.eq_ignore_ascii_case("hevc") {
            MFVideoFormat_HEVC
        } else {
            MFVideoFormat_H264
        };
        let input_info = MFT_REGISTER_TYPE_INFO {
            guidMajorType: MFMediaType_Video,
            guidSubtype: subtype,
        };

        let mut transform: Option<IMFTransform> = None;
        let mut hardware = false;

        unsafe {
            for (flags, is_hw) in [
                (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, true),
                (MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, false),
            ] {
                let mut activates: *mut Option<IMFActivate> = null_mut();
                let mut count = 0u32;
                if MFTEnumEx(
                    MFT_CATEGORY_VIDEO_DECODER,
                    flags,
                    Some(&input_info),
                    None,
                    &mut activates,
                    &mut count,
                )
                .is_ok()
                    && !activates.is_null()
                {
                    let slice = std::slice::from_raw_parts_mut(activates, count as usize);
                    for act in slice.iter_mut() {
                        if let Some(a) = act.take() {
                            if transform.is_none()
                                && let Ok(mft) = a.ActivateObject::<IMFTransform>()
                            {
                                transform = Some(mft);
                                hardware = is_hw;
                            }
                            let _ = a.ShutdownObject();
                        }
                    }
                    CoTaskMemFree(Some(activates as *const _));
                }
                if transform.is_some() {
                    break;
                }
            }
        }

        let transform =
            transform.ok_or_else(|| format!("No Media Foundation decoder for {codec}"))?;
        unsafe {
            if let Ok(attrs) = transform.GetAttributes() {
                let _ = attrs.SetUINT32(&CODECAPI_AVLowLatencyMode, 1);
            }
            let in_type = MFCreateMediaType().map_err(|e| e.to_string())?;
            in_type
                .SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                .map_err(|e| e.to_string())?;
            in_type
                .SetGUID(&MF_MT_SUBTYPE, &subtype)
                .map_err(|e| e.to_string())?;
            transform
                .SetInputType(0, &in_type, 0)
                .map_err(|e| e.to_string())?;
        }

        let mut dec = Self {
            transform,
            width: 0,
            height: 0,
            stride: 0,
            provides_samples: false,
            output_buf_size: 0,
            hardware,
            seen_keyframe: false,
        };
        dec.configure_output()?;
        unsafe {
            let _ = dec
                .transform
                .ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            let _ = dec
                .transform
                .ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
        Ok(dec)
    }

    fn configure_output(&mut self) -> Result<(), String> {
        use windows::Win32::Media::MediaFoundation::{
            MF_MT_DEFAULT_STRIDE, MF_MT_FRAME_SIZE, MF_MT_SUBTYPE,
            MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES,
            MFVideoFormat_NV12,
        };
        unsafe {
            let mut idx = 0u32;
            let mut chosen = None;
            while let Ok(mt) = self.transform.GetOutputAvailableType(0, idx) {
                if let Ok(sub) = mt.GetGUID(&MF_MT_SUBTYPE)
                    && sub == MFVideoFormat_NV12
                {
                    chosen = Some(mt);
                    break;
                }
                idx += 1;
            }
            let out_type =
                chosen.ok_or_else(|| "Decoder does not expose NV12 output".to_string())?;
            self.transform
                .SetOutputType(0, &out_type, 0)
                .map_err(|e| e.to_string())?;
            if let Ok(packed) = out_type.GetUINT64(&MF_MT_FRAME_SIZE) {
                self.width = (packed >> 32) as u32;
                self.height = (packed & 0xffff_ffff) as u32;
            }
            self.stride = out_type
                .GetUINT32(&MF_MT_DEFAULT_STRIDE)
                .map(|v| (v as i32).unsigned_abs() as usize)
                .unwrap_or(self.width as usize);
            if self.stride < self.width as usize {
                self.stride = self.width as usize;
            }
            if let Ok(info) = self.transform.GetOutputStreamInfo(0) {
                let flags = info.dwFlags;
                self.provides_samples = (flags
                    & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32
                        | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.0 as u32))
                    != 0;
                self.output_buf_size = info.cbSize;
            }
        }
        Ok(())
    }

    fn decode_into(
        &mut self,
        pkt: &VideoPacket,
        rgba_out: &mut Vec<u8>,
    ) -> Result<Option<(u32, u32)>, String> {
        use std::mem::ManuallyDrop;
        use windows::Win32::Media::MediaFoundation::{
            MF_E_TRANSFORM_NEED_MORE_INPUT, MF_E_TRANSFORM_STREAM_CHANGE, MFCreateMemoryBuffer,
            MFCreateSample, MFT_OUTPUT_DATA_BUFFER,
        };

        if !self.seen_keyframe {
            if !pkt.keyframe {
                return Ok(None);
            }
            self.seen_keyframe = true;
        }
        if pkt.data.is_empty() {
            return Ok(None);
        }

        unsafe {
            let buf = MFCreateMemoryBuffer(pkt.data.len() as u32).map_err(|e| e.to_string())?;
            let mut ptr = std::ptr::null_mut();
            buf.Lock(&mut ptr, None, None).map_err(|e| e.to_string())?;
            std::ptr::copy_nonoverlapping(pkt.data.as_ptr(), ptr, pkt.data.len());
            let _ = buf.Unlock();
            buf.SetCurrentLength(pkt.data.len() as u32)
                .map_err(|e| e.to_string())?;
            let sample = MFCreateSample().map_err(|e| e.to_string())?;
            sample.AddBuffer(&buf).map_err(|e| e.to_string())?;
            let ts_100ns = (pkt.timestamp_ms.max(0.0) * 10_000.0) as i64;
            let _ = sample.SetSampleTime(ts_100ns);
            self.transform
                .ProcessInput(0, &sample, 0)
                .map_err(|e| e.to_string())?;

            let mut decoded_dims = None;
            loop {
                let pre_sample = if self.provides_samples {
                    None
                } else {
                    let sz = self
                        .output_buf_size
                        .max(self.stride as u32 * self.height.max(16) * 2)
                        .max(64 * 1024);
                    let obuf = MFCreateMemoryBuffer(sz).map_err(|e| e.to_string())?;
                    let os = MFCreateSample().map_err(|e| e.to_string())?;
                    os.AddBuffer(&obuf).map_err(|e| e.to_string())?;
                    Some(os)
                };
                let mut out_buf = MFT_OUTPUT_DATA_BUFFER {
                    dwStreamID: 0,
                    pSample: ManuallyDrop::new(pre_sample),
                    dwStatus: 0,
                    pEvents: ManuallyDrop::new(None),
                };
                let mut status = 0u32;
                let res = self.transform.ProcessOutput(
                    0,
                    std::slice::from_mut(&mut out_buf),
                    &mut status,
                );
                let out_sample = ManuallyDrop::into_inner(out_buf.pSample);
                let _ = ManuallyDrop::into_inner(out_buf.pEvents);
                match res {
                    Ok(()) => {
                        if let Some(os) = out_sample
                            && let Ok(mbuf) = os.ConvertToContiguousBuffer()
                        {
                            let mut bptr = std::ptr::null_mut();
                            let mut cur_len = 0u32;
                            if mbuf.Lock(&mut bptr, None, Some(&mut cur_len)).is_ok() {
                                let slice = std::slice::from_raw_parts(bptr, cur_len as usize);
                                if nv12_to_rgba(
                                    slice,
                                    self.width,
                                    self.height,
                                    self.stride,
                                    pkt.flip,
                                    rgba_out,
                                ) {
                                    decoded_dims = Some((self.width, self.height));
                                }
                                let _ = mbuf.Unlock();
                            }
                        }
                    }
                    Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                        self.configure_output()?;
                    }
                    Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => {
                        break;
                    }
                    Err(_) => {
                        break;
                    }
                }
            }
            Ok(decoded_dims)
        }
    }
}

#[cfg(not(windows))]
fn decode_loop(
    queue: Arc<VideoPacketQueue>,
    _pixels: Pixels,
    _stats: Arc<Mutex<VideoStreamStats>>,
    _initial_codec: &str,
) {
    while queue.pop_blocking().is_some() {}
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_packet(ts: f64) -> VideoPacket {
        VideoPacket {
            data: vec![0, 0, 0, 1, 0x67],
            timestamp_ms: ts,
            keyframe: ts == 0.0,
            flip: false,
            codec: "h264".into(),
        }
    }

    #[test]
    fn converts_bt709_nv12_black_and_white_with_stride_padding() {
        let width = 4u32;
        let height = 2u32;
        let stride = 8usize;
        let mut nv12 = vec![0u8; stride * 3];
        for r in 0..2 {
            nv12[r * stride] = 16;
            nv12[r * stride + 1] = 235;
            nv12[r * stride + 2] = 16;
            nv12[r * stride + 3] = 235;
        }
        let uv_off = stride * 2;
        for c in 0..4 {
            nv12[uv_off + c] = 128;
        }
        let mut rgba = Vec::new();
        assert!(nv12_to_rgba(&nv12, width, height, stride, false, &mut rgba));
        assert_eq!(rgba.len(), 4 * 2 * 4);
        assert!(rgba[0] <= 2 && rgba[1] <= 2 && rgba[2] <= 2 && rgba[3] == 255);
        assert!(rgba[4] >= 253 && rgba[5] >= 253 && rgba[6] >= 253 && rgba[7] == 255);
    }

    #[test]
    fn video_packet_queue_drops_oldest_when_full_and_keeps_latest_frames() {
        let q = VideoPacketQueue::new(3);
        assert!(!q.push_drop_oldest(sample_packet(1.0)));
        assert!(!q.push_drop_oldest(sample_packet(2.0)));
        assert!(!q.push_drop_oldest(sample_packet(3.0)));
        assert!(q.push_drop_oldest(sample_packet(4.0)));
        assert!(q.push_drop_oldest(sample_packet(5.0)));

        let mut popped = Vec::new();
        for _ in 0..3 {
            popped.push(q.pop_blocking().unwrap().timestamp_ms);
        }
        assert_eq!(popped, vec![3.0, 4.0, 5.0]);
        q.close();
        assert!(q.pop_blocking().is_none());
    }

    #[test]
    fn video_frame_router_routes_registered_streams_and_returns_unregistered_frames() {
        let router = VideoFrameRouter::new();
        let make_frame = |uid: &str, ts: f64| DiscordVideoFrame {
            source: "stream".into(),
            user_id: uid.into(),
            timestamp: ts,
            keyframe: true,
            rotation: 0,
            flip: false,
            codec: "h264".into(),
            data: vec![0, 0, 0, 1, 0x67],
        };

        let unrouted = router
            .try_push(make_frame("u1", 10.0))
            .expect_err("unregistered stream should fall back");
        assert_eq!(unrouted.user_id, "u1");

        let queue = Arc::new(VideoPacketQueue::new(4));
        let stats = Arc::new(Mutex::new(VideoStreamStats::default()));
        let handle = VideoStreamHandle {
            pixels: Pixels::new(),
            stats,
            queue: Arc::clone(&queue),
            _owner: Arc::new(QueueOwner(Arc::clone(&queue))),
        };
        router.register(("stream".into(), "u1".into()), handle);
        assert!(router.try_push(make_frame("u1", 20.0)).is_ok());
        assert_eq!(queue.pop_blocking().unwrap().timestamp_ms, 20.0);

        router.remove(&("stream".into(), "u1".into()));
        assert!(router.try_push(make_frame("u1", 30.0)).is_err());
    }
}
