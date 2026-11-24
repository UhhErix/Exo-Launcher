use std::collections::VecDeque;
use std::sync::Arc;
use std::time::{Duration, Instant};

use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;
use windows::Win32::Graphics::Dxgi::IDXGISurface;
use windows::Win32::Media::MediaFoundation::{
    IMFActivate, IMFAttributes, IMFMediaBuffer, IMFMediaEventGenerator, IMFMediaType, IMFSample,
    IMFTransform, MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS, METransformDrainComplete,
    METransformHaveOutput, METransformNeedInput, MF_E_TRANSFORM_NEED_MORE_INPUT,
    MF_E_TRANSFORM_STREAM_CHANGE, MF_EVENT_FLAG_NO_WAIT, MF_MT_AVG_BITRATE, MF_MT_FRAME_RATE,
    MF_MT_FRAME_SIZE, MF_MT_INTERLACE_MODE, MF_MT_MAJOR_TYPE, MF_MT_MPEG2_PROFILE, MF_MT_SUBTYPE,
    MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, MF_TRANSFORM_ASYNC, MF_TRANSFORM_ASYNC_UNLOCK,
    MFCreateDXGISurfaceBuffer, MFCreateMediaType, MFCreateSample, MFMediaType_Video,
    MFSTARTUP_LITE, MFSampleExtension_CleanPoint, MFStartup, MFT_CATEGORY_VIDEO_ENCODER,
    MFT_ENUM_FLAG_ASYNCMFT, MFT_ENUM_FLAG_HARDWARE, MFT_ENUM_FLAG_SORTANDFILTER,
    MFT_FRIENDLY_NAME_Attribute, MFT_MESSAGE_COMMAND_DRAIN, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,
    MFT_MESSAGE_NOTIFY_END_OF_STREAM, MFT_MESSAGE_NOTIFY_START_OF_STREAM,
    MFT_MESSAGE_SET_D3D_MANAGER, MFT_OUTPUT_DATA_BUFFER, MFT_REGISTER_TYPE_INFO, MFTEnumEx,
    MFVideoFormat_AV1, MFVideoFormat_H264, MFVideoFormat_HEVC, MFVideoFormat_NV12,
    MFVideoFormat_P010, MFVideoInterlace_Progressive,
};
use windows::Win32::System::Com::CoTaskMemFree;
use windows::core::{GUID, Interface};

use crate::encode::nvenc::probe_nvenc_runtime;
use crate::encode::{EncodedVideoFrame, EncoderCapabilities, EncoderConfig, HardwareVideoEncoder};
use crate::gpu::GpuDevice;
use crate::mux_mp4::VideoCodec;

const MF_VERSION_VAL: u32 = (0x0002 << 16) | 0x0070;
const E_AV1_PROFILE_MAIN: u32 = 0;
const E_H264_PROFILE_HIGH: u32 = 100;
const E_HEVC_PROFILE_MAIN: u32 = 1;
const E_HEVC_PROFILE_MAIN10: u32 = 2;

pub fn ensure_mf_started() -> Result<(), String> {
    unsafe { MFStartup(MF_VERSION_VAL, MFSTARTUP_LITE) }
        .map_err(|e| format!("MFStartup failed: {e}"))
}

fn codec_subtype(codec: VideoCodec) -> GUID {
    match codec {
        VideoCodec::H264 => MFVideoFormat_H264,
        VideoCodec::Hevc => MFVideoFormat_HEVC,
        VideoCodec::Av1 => MFVideoFormat_AV1,
    }
}

fn pack_u64(high: u32, low: u32) -> u64 {
    ((high as u64) << 32) | (low as u64)
}

fn enumerate_hw_encoders(codec: VideoCodec) -> Result<Vec<IMFActivate>, String> {
    ensure_mf_started()?;
    let out_info = MFT_REGISTER_TYPE_INFO {
        guidMajorType: MFMediaType_Video,
        guidSubtype: codec_subtype(codec),
    };
    let flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    let mut ptr: *mut Option<IMFActivate> = std::ptr::null_mut();
    let mut count = 0u32;
    unsafe {
        MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            flags,
            None,
            Some(&out_info),
            &mut ptr,
            &mut count,
        )
    }
    .map_err(|e| format!("MFTEnumEx({codec:?}) failed: {e}"))?;

    let mut result = Vec::with_capacity(count as usize);
    if !ptr.is_null() {
        let slice = unsafe { std::slice::from_raw_parts(ptr, count as usize) };
        for act in slice.iter().flatten() {
            result.push(act.clone());
        }
        unsafe { CoTaskMemFree(Some(ptr.cast())) };
    }
    Ok(result)
}

fn friendly_name(activate: &IMFActivate) -> String {
    let mut buf = vec![0u16; 256];
    let mut len = 0u32;
    if unsafe { activate.GetString(&MFT_FRIENDLY_NAME_Attribute, &mut buf, Some(&mut len)) }.is_ok()
    {
        let used = (len as usize).min(buf.len());
        return String::from_utf16_lossy(&buf[..used]).trim().to_string();
    }
    "Hardware MFT Encoder".to_string()
}

pub fn probe_hardware_encoders() -> EncoderCapabilities {
    let nv = probe_nvenc_runtime();
    let av1_hw = enumerate_hw_encoders(VideoCodec::Av1)
        .map(|v| !v.is_empty())
        .unwrap_or(false);
    let hevc_hw = enumerate_hw_encoders(VideoCodec::Hevc)
        .map(|v| !v.is_empty())
        .unwrap_or(false);
    let h264_hw = enumerate_hw_encoders(VideoCodec::H264)
        .map(|v| !v.is_empty())
        .unwrap_or(false);
    let preferred_backend = if nv.available {
        format!("NVENC {}.{} (zero-copy D3D11 MFT)", nv.major, nv.minor)
    } else if av1_hw || hevc_hw || h264_hw {
        "Hardware MFT (zero-copy D3D11)".to_string()
    } else {
        "none".to_string()
    };
    EncoderCapabilities {
        nvenc_api_version: if nv.available {
            Some((nv.major, nv.minor))
        } else {
            None
        },
        av1_hw,
        hevc_hw,
        h264_hw,
        preferred_backend,
    }
}

pub struct MfHardwareEncoder {
    _gpu: Arc<GpuDevice>,
    transform: IMFTransform,
    event_gen: Option<IMFMediaEventGenerator>,
    is_async: bool,
    config: EncoderConfig,
    name: String,
    need_input_tokens: usize,
    pending_frames: VecDeque<EncodedVideoFrame>,
    output_provides_samples: bool,
}

impl MfHardwareEncoder {
    pub fn open(gpu: Arc<GpuDevice>, config: EncoderConfig) -> Result<Self, String> {
        ensure_mf_started()?;
        let activates = enumerate_hw_encoders(config.codec)?;
        if activates.is_empty() {
            return Err(format!(
                "no hardware video encoder found for {:?}",
                config.codec
            ));
        }

        let mut last_err = String::new();
        for activate in activates {
            let name = friendly_name(&activate);
            match Self::try_activate(Arc::clone(&gpu), &activate, name, config) {
                Ok(enc) => return Ok(enc),
                Err(e) => last_err = e,
            }
        }
        Err(format!(
            "failed to initialize any {:?} hardware encoder: {last_err}",
            config.codec
        ))
    }

    fn try_activate(
        gpu: Arc<GpuDevice>,
        activate: &IMFActivate,
        name: String,
        config: EncoderConfig,
    ) -> Result<Self, String> {
        let transform: IMFTransform = unsafe { activate.ActivateObject() }
            .map_err(|e| format!("ActivateObject({name}) failed: {e}"))?;

        let attrs: IMFAttributes = unsafe { transform.GetAttributes() }
            .map_err(|e| format!("GetAttributes({name}) failed: {e}"))?;
        let is_async = unsafe { attrs.GetUINT32(&MF_TRANSFORM_ASYNC) }.unwrap_or(0) != 0;
        if is_async {
            unsafe { attrs.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1) }
                .map_err(|e| format!("MF_TRANSFORM_ASYNC_UNLOCK({name}) failed: {e}"))?;
        }
        let _ = unsafe { attrs.SetUINT32(&MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1) };

        let d3d_ptr = gpu.dxgi_manager.as_raw() as usize;
        unsafe { transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, d3d_ptr) }
            .map_err(|e| format!("MFT_MESSAGE_SET_D3D_MANAGER({name}) failed: {e}"))?;

        let out_type: IMFMediaType =
            unsafe { MFCreateMediaType() }.map_err(|e| format!("MFCreateMediaType out: {e}"))?;
        unsafe {
            out_type
                .SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                .map_err(|e| format!("SetGUID major out: {e}"))?;
            out_type
                .SetGUID(&MF_MT_SUBTYPE, &codec_subtype(config.codec))
                .map_err(|e| format!("SetGUID subtype out: {e}"))?;
            out_type
                .SetUINT32(&MF_MT_AVG_BITRATE, config.bitrate_bps)
                .map_err(|e| format!("SetUINT32 bitrate: {e}"))?;
            out_type
                .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)
                .map_err(|e| format!("SetUINT32 interlace: {e}"))?;
            out_type
                .SetUINT64(&MF_MT_FRAME_SIZE, pack_u64(config.width, config.height))
                .map_err(|e| format!("SetUINT64 frame size out: {e}"))?;
            out_type
                .SetUINT64(&MF_MT_FRAME_RATE, pack_u64(config.fps.max(1), 1))
                .map_err(|e| format!("SetUINT64 fps out: {e}"))?;
            let profile = match config.codec {
                VideoCodec::H264 => E_H264_PROFILE_HIGH,
                VideoCodec::Hevc => {
                    if config.hdr10 {
                        E_HEVC_PROFILE_MAIN10
                    } else {
                        E_HEVC_PROFILE_MAIN
                    }
                }
                VideoCodec::Av1 => E_AV1_PROFILE_MAIN,
            };
            let _ = out_type.SetUINT32(&MF_MT_MPEG2_PROFILE, profile);
            transform
                .SetOutputType(0, &out_type, 0)
                .map_err(|e| format!("SetOutputType({name}) failed: {e}"))?;
        }

        let in_subtype = if config.hdr10 {
            MFVideoFormat_P010
        } else {
            MFVideoFormat_NV12
        };
        let in_type: IMFMediaType =
            unsafe { MFCreateMediaType() }.map_err(|e| format!("MFCreateMediaType in: {e}"))?;
        unsafe {
            in_type
                .SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                .map_err(|e| format!("SetGUID major in: {e}"))?;
            in_type
                .SetGUID(&MF_MT_SUBTYPE, &in_subtype)
                .map_err(|e| format!("SetGUID subtype in: {e}"))?;
            in_type
                .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)
                .map_err(|e| format!("SetUINT32 interlace in: {e}"))?;
            in_type
                .SetUINT64(&MF_MT_FRAME_SIZE, pack_u64(config.width, config.height))
                .map_err(|e| format!("SetUINT64 frame size in: {e}"))?;
            in_type
                .SetUINT64(&MF_MT_FRAME_RATE, pack_u64(config.fps.max(1), 1))
                .map_err(|e| format!("SetUINT64 fps in: {e}"))?;
            transform
                .SetInputType(0, &in_type, 0)
                .map_err(|e| format!("SetInputType({name}) failed: {e}"))?;
        }

        let out_info = unsafe { transform.GetOutputStreamInfo(0) }.unwrap_or_default();
        let output_provides_samples = (out_info.dwFlags & 0x0000_0100) != 0;

        let event_gen = if is_async {
            Some(
                transform
                    .cast::<IMFMediaEventGenerator>()
                    .map_err(|e| format!("cast IMFMediaEventGenerator({name}) failed: {e}"))?,
            )
        } else {
            None
        };

        unsafe {
            let _ = transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            let _ = transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }

        Ok(Self {
            _gpu: gpu,
            transform,
            event_gen,
            is_async,
            config,
            name,
            need_input_tokens: 0,
            pending_frames: VecDeque::new(),
            output_provides_samples,
        })
    }

    fn pump_async_events(&mut self, wait_for_input: bool, timeout: Duration) -> Result<(), String> {
        let Some(ev_gen) = self.event_gen.clone() else {
            return Ok(());
        };
        let deadline = Instant::now() + timeout;
        loop {
            let flags = if wait_for_input && self.need_input_tokens == 0 {
                MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS(0)
            } else {
                MF_EVENT_FLAG_NO_WAIT
            };
            match unsafe { ev_gen.GetEvent(flags) } {
                Ok(evt) => {
                    let etype = unsafe { evt.GetType() }.unwrap_or(0);
                    if etype == METransformNeedInput.0 as u32 {
                        self.need_input_tokens += 1;
                    } else if etype == METransformHaveOutput.0 as u32 {
                        if let Some(frame) = self.pull_one_output()? {
                            self.pending_frames.push_back(frame);
                        }
                    } else if etype == METransformDrainComplete.0 as u32 {
                        break;
                    }
                    if wait_for_input && self.need_input_tokens > 0 {
                        break;
                    }
                }
                Err(_) => {
                    if !wait_for_input || self.need_input_tokens > 0 || Instant::now() >= deadline {
                        break;
                    }
                    std::thread::sleep(Duration::from_millis(1));
                }
            }
        }
        Ok(())
    }

    fn pull_one_output(&mut self) -> Result<Option<EncodedVideoFrame>, String> {
        let sample = if self.output_provides_samples {
            None
        } else {
            let s: IMFSample =
                unsafe { MFCreateSample() }.map_err(|e| format!("MFCreateSample out: {e}"))?;
            let buf: IMFMediaBuffer = unsafe {
                windows::Win32::Media::MediaFoundation::MFCreateMemoryBuffer(
                    (self.config.width * self.config.height).max(256 * 1024),
                )
            }
            .map_err(|e| format!("MFCreateMemoryBuffer out: {e}"))?;
            unsafe { s.AddBuffer(&buf) }.map_err(|e| format!("AddBuffer out: {e}"))?;
            Some(s)
        };

        let mut out_buf = [MFT_OUTPUT_DATA_BUFFER {
            dwStreamID: 0,
            pSample: std::mem::ManuallyDrop::new(sample),
            dwStatus: 0,
            pEvents: std::mem::ManuallyDrop::new(None),
        }];
        let mut status = 0u32;
        let res = unsafe { self.transform.ProcessOutput(0, &mut out_buf, &mut status) };
        let extracted_sample = unsafe { std::mem::ManuallyDrop::take(&mut out_buf[0].pSample) };
        let _events = unsafe { std::mem::ManuallyDrop::take(&mut out_buf[0].pEvents) };

        match res {
            Ok(()) => {
                let Some(s) = extracted_sample else {
                    return Ok(None);
                };
                let buf: IMFMediaBuffer = unsafe { s.ConvertToContiguousBuffer() }
                    .map_err(|e| format!("ConvertToContiguousBuffer: {e}"))?;
                let mut ptr: *mut u8 = std::ptr::null_mut();
                let mut cur_len = 0u32;
                unsafe { buf.Lock(&mut ptr, None, Some(&mut cur_len)) }
                    .map_err(|e| format!("IMFMediaBuffer::Lock bitstream: {e}"))?;
                let data = if !ptr.is_null() && cur_len > 0 {
                    unsafe { std::slice::from_raw_parts(ptr, cur_len as usize) }.to_vec()
                } else {
                    Vec::new()
                };
                unsafe {
                    let _ = buf.Unlock();
                }
                let ts = unsafe { s.GetSampleTime() }.unwrap_or(0);
                let dur = unsafe { s.GetSampleDuration() }.unwrap_or(0);
                let clean = unsafe { s.GetUINT32(&MFSampleExtension_CleanPoint) }.unwrap_or(0);
                Ok(Some(EncodedVideoFrame {
                    annex_b: data,
                    timestamp_100ns: ts,
                    duration_100ns: dur,
                    is_keyframe: clean != 0,
                }))
            }
            Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => Ok(None),
            Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                if let Ok(new_out) = unsafe { self.transform.GetOutputAvailableType(0, 0) } {
                    let _ = unsafe { self.transform.SetOutputType(0, &new_out, 0) };
                }
                Ok(None)
            }
            Err(e) => Err(format!("ProcessOutput({}) failed: {e}", self.name)),
        }
    }
}

impl HardwareVideoEncoder for MfHardwareEncoder {
    fn encoder_name(&self) -> &str {
        &self.name
    }

    fn codec(&self) -> VideoCodec {
        self.config.codec
    }

    fn encode_gpu_surface(
        &mut self,
        surface: &ID3D11Texture2D,
        timestamp_100ns: i64,
        duration_100ns: i64,
        _force_keyframe: bool,
    ) -> Result<Vec<EncodedVideoFrame>, String> {
        if self.is_async {
            self.pump_async_events(true, Duration::from_millis(50))?;
            if self.need_input_tokens == 0 {
                return Ok(self.pending_frames.drain(..).collect());
            }
            self.need_input_tokens -= 1;
        }

        let dxgi_surface: IDXGISurface = surface
            .cast()
            .map_err(|e| format!("cast ID3D11Texture2D to IDXGISurface failed: {e}"))?;
        let media_buf: IMFMediaBuffer =
            unsafe { MFCreateDXGISurfaceBuffer(&ID3D11Texture2D::IID, &dxgi_surface, 0, false) }
                .map_err(|e| format!("MFCreateDXGISurfaceBuffer failed: {e}"))?;
        let sample: IMFSample =
            unsafe { MFCreateSample() }.map_err(|e| format!("MFCreateSample in failed: {e}"))?;
        unsafe {
            sample
                .AddBuffer(&media_buf)
                .map_err(|e| format!("AddBuffer in failed: {e}"))?;
            sample
                .SetSampleTime(timestamp_100ns)
                .map_err(|e| format!("SetSampleTime failed: {e}"))?;
            sample
                .SetSampleDuration(duration_100ns)
                .map_err(|e| format!("SetSampleDuration failed: {e}"))?;
            self.transform
                .ProcessInput(0, &sample, 0)
                .map_err(|e| format!("ProcessInput({}) failed: {e}", self.name))?;
        }

        if self.is_async {
            self.pump_async_events(false, Duration::from_millis(5))?;
        } else {
            while let Some(frame) = self.pull_one_output()? {
                self.pending_frames.push_back(frame);
            }
        }
        Ok(self.pending_frames.drain(..).collect())
    }

    fn drain(&mut self) -> Result<Vec<EncodedVideoFrame>, String> {
        unsafe {
            let _ = self
                .transform
                .ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            let _ = self.transform.ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        }
        if self.is_async {
            let deadline = Instant::now() + Duration::from_millis(250);
            while Instant::now() < deadline {
                if let Some(ev_gen) = self.event_gen.clone() {
                    match unsafe { ev_gen.GetEvent(MF_EVENT_FLAG_NO_WAIT) } {
                        Ok(evt) => {
                            let etype = unsafe { evt.GetType() }.unwrap_or(0);
                            if etype == METransformHaveOutput.0 as u32 {
                                if let Some(frame) = self.pull_one_output()? {
                                    self.pending_frames.push_back(frame);
                                }
                            } else if etype == METransformDrainComplete.0 as u32 {
                                break;
                            }
                        }
                        Err(_) => std::thread::sleep(Duration::from_millis(2)),
                    }
                } else {
                    break;
                }
            }
        } else {
            while let Some(frame) = self.pull_one_output()? {
                self.pending_frames.push_back(frame);
            }
        }
        Ok(self.pending_frames.drain(..).collect())
    }
}
