use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{
    AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
    AUDIOCLIENT_ACTIVATION_PARAMS, AUDIOCLIENT_ACTIVATION_PARAMS_0,
    AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS,
    ActivateAudioInterfaceAsync, IActivateAudioInterfaceAsyncOperation,
    IActivateAudioInterfaceCompletionHandler, IActivateAudioInterfaceCompletionHandler_Impl,
    IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator, MMDeviceEnumerator,
    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE, VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
    WAVEFORMATEX, eCapture, eConsole,
};
use windows::Win32::System::Com::StructuredStorage::{
    PROPVARIANT, PROPVARIANT_0, PROPVARIANT_0_0, PROPVARIANT_0_0_0,
};
use windows::Win32::System::Com::{
    BLOB, CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
};
use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
use windows::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};
use windows::Win32::System::Variant::VT_BLOB;
use windows::core::{IUnknown, Interface, Result as WinResult, implement};

use crate::replay_ring::{EncodedPacket, TrackKind};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AudioSourceSpec {
    ProcessLoopbackInclude { pid: u32, track: TrackKind },
    ProcessLoopbackExclude { pid: u32, track: TrackKind },
    DefaultMicrophone,
}

impl AudioSourceSpec {
    pub fn track_kind(self) -> TrackKind {
        match self {
            Self::ProcessLoopbackInclude { track, .. }
            | Self::ProcessLoopbackExclude { track, .. } => track,
            Self::DefaultMicrophone => TrackKind::AudioMic,
        }
    }
}

pub fn build_three_track_plan(
    game_pid: Option<u32>,
    discord_pid: Option<u32>,
    include_mic: bool,
    include_voice: bool,
) -> Vec<AudioSourceSpec> {
    let mut specs = Vec::new();
    if let Some(pid) = game_pid {
        specs.push(AudioSourceSpec::ProcessLoopbackInclude {
            pid,
            track: TrackKind::AudioGame,
        });
    }
    if include_mic {
        specs.push(AudioSourceSpec::DefaultMicrophone);
    }
    if include_voice {
        if let Some(dpid) = discord_pid {
            specs.push(AudioSourceSpec::ProcessLoopbackInclude {
                pid: dpid,
                track: TrackKind::AudioVoice,
            });
        } else if let Some(gpid) = game_pid {
            specs.push(AudioSourceSpec::ProcessLoopbackExclude {
                pid: gpid,
                track: TrackKind::AudioVoice,
            });
        }
    }
    specs
}

#[implement(IActivateAudioInterfaceCompletionHandler)]
struct ActivationCompletionHandler {
    event: HANDLE,
}

impl IActivateAudioInterfaceCompletionHandler_Impl for ActivationCompletionHandler_Impl {
    fn ActivateCompleted(
        &self,
        _activateoperation: windows_core::Ref<'_, IActivateAudioInterfaceAsyncOperation>,
    ) -> WinResult<()> {
        unsafe {
            let _ = SetEvent(self.event);
        }
        Ok(())
    }
}

pub struct WasapiTrackCapture {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl WasapiTrackCapture {
    pub fn start(spec: AudioSourceSpec, tx: mpsc::Sender<EncodedPacket>) -> Result<Self, String> {
        let stop = Arc::new(AtomicBool::new(false));
        let stop_clone = Arc::clone(&stop);
        let handle = thread::Builder::new()
            .name(format!("exo-audio-{:?}", spec.track_kind()))
            .spawn(move || {
                let _ = run_audio_loop(spec, stop_clone, tx);
            })
            .map_err(|e| format!("spawn audio thread failed: {e}"))?;
        Ok(Self {
            stop,
            handle: Some(handle),
        })
    }

    pub fn stop(mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

impl Drop for WasapiTrackCapture {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

fn qpc_100ns() -> i64 {
    let mut counter = 0i64;
    let mut freq = 1i64;
    unsafe {
        let _ = QueryPerformanceCounter(&mut counter);
        let _ = QueryPerformanceFrequency(&mut freq);
    }
    ((counter as i128 * 10_000_000) / (freq.max(1) as i128)) as i64
}

fn activate_process_loopback_client(pid: u32, include: bool) -> Result<IAudioClient, String> {
    let event = unsafe { CreateEventW(None, true, false, None) }
        .map_err(|e| format!("CreateEventW failed: {e}"))?;
    let handler: IActivateAudioInterfaceCompletionHandler =
        ActivationCompletionHandler { event }.into();

    let mut params = AUDIOCLIENT_ACTIVATION_PARAMS {
        ActivationType: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        Anonymous: AUDIOCLIENT_ACTIVATION_PARAMS_0 {
            ProcessLoopbackParams: AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
                TargetProcessId: pid,
                ProcessLoopbackMode: if include {
                    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                } else {
                    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                },
            },
        },
    };

    let pv = PROPVARIANT {
        Anonymous: PROPVARIANT_0 {
            Anonymous: std::mem::ManuallyDrop::new(PROPVARIANT_0_0 {
                vt: VT_BLOB,
                wReserved1: 0,
                wReserved2: 0,
                wReserved3: 0,
                Anonymous: PROPVARIANT_0_0_0 {
                    blob: BLOB {
                        cbSize: std::mem::size_of::<AUDIOCLIENT_ACTIVATION_PARAMS>() as u32,
                        pBlobData: (&mut params as *mut AUDIOCLIENT_ACTIVATION_PARAMS).cast(),
                    },
                },
            }),
        },
    };

    let op = unsafe {
        ActivateAudioInterfaceAsync(
            VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            &IAudioClient::IID,
            Some(&pv),
            &handler,
        )
    }
    .map_err(|e| {
        unsafe {
            let _ = CloseHandle(event);
        }
        format!("ActivateAudioInterfaceAsync(pid={pid}) failed: {e}")
    })?;

    let waited = unsafe { WaitForSingleObject(event, 3_000) };
    unsafe {
        let _ = CloseHandle(event);
    }
    if waited != WAIT_OBJECT_0 {
        return Err(format!("ActivateAudioInterfaceAsync(pid={pid}) timed out"));
    }

    let mut activate_hr = windows::core::HRESULT(0);
    let mut activated: Option<IUnknown> = None;
    unsafe { op.GetActivateResult(&mut activate_hr, &mut activated) }
        .map_err(|e| format!("GetActivateResult failed: {e}"))?;
    activate_hr
        .ok()
        .map_err(|e| format!("process loopback activate HRESULT failed: {e}"))?;

    let unk = activated.ok_or("process loopback returned null IAudioClient")?;
    unk.cast::<IAudioClient>()
        .map_err(|e| format!("cast IAudioClient failed: {e}"))
}

fn activate_default_mic_client() -> Result<IAudioClient, String> {
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
            .map_err(|e| format!("CoCreateInstance MMDeviceEnumerator failed: {e}"))?;
    let device = unsafe { enumerator.GetDefaultAudioEndpoint(eCapture, eConsole) }
        .map_err(|e| format!("GetDefaultAudioEndpoint(eCapture) failed: {e}"))?;
    unsafe { device.Activate::<IAudioClient>(CLSCTX_ALL, None) }
        .map_err(|e| format!("IMMDevice::Activate(IAudioClient) failed: {e}"))
}

fn run_audio_loop(
    spec: AudioSourceSpec,
    stop: Arc<AtomicBool>,
    tx: mpsc::Sender<EncodedPacket>,
) -> Result<(), String> {
    let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    let res = run_audio_loop_inner(spec, stop, tx);
    unsafe { CoUninitialize() };
    res
}

fn run_audio_loop_inner(
    spec: AudioSourceSpec,
    stop: Arc<AtomicBool>,
    tx: mpsc::Sender<EncodedPacket>,
) -> Result<(), String> {
    let (client, is_loopback) = match spec {
        AudioSourceSpec::ProcessLoopbackInclude { pid, .. } => {
            (activate_process_loopback_client(pid, true)?, true)
        }
        AudioSourceSpec::ProcessLoopbackExclude { pid, .. } => {
            (activate_process_loopback_client(pid, false)?, true)
        }
        AudioSourceSpec::DefaultMicrophone => (activate_default_mic_client()?, false),
    };

    let format = WAVEFORMATEX {
        wFormatTag: 1,
        nChannels: 2,
        nSamplesPerSec: 48_000,
        nAvgBytesPerSec: 48_000 * 4,
        nBlockAlign: 4,
        wBitsPerSample: 16,
        cbSize: 0,
    };

    let stream_flags = if is_loopback {
        AUDCLNT_STREAMFLAGS_LOOPBACK
    } else {
        0
    };
    unsafe {
        client
            .Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                stream_flags,
                2_000_000,
                0,
                &format,
                None,
            )
            .map_err(|e| format!("IAudioClient::Initialize failed: {e}"))?;
    }
    let capture: IAudioCaptureClient = unsafe { client.GetService() }
        .map_err(|e| format!("IAudioClient::GetService(IAudioCaptureClient) failed: {e}"))?;
    unsafe { client.Start() }.map_err(|e| format!("IAudioClient::Start failed: {e}"))?;

    let track = spec.track_kind();
    let bytes_per_frame = format.nBlockAlign as usize;

    while !stop.load(Ordering::Acquire) {
        let mut next_len = unsafe { capture.GetNextPacketSize() }.unwrap_or(0);
        if next_len == 0 {
            thread::sleep(Duration::from_millis(5));
            continue;
        }
        while next_len > 0 && !stop.load(Ordering::Acquire) {
            let mut data_ptr: *mut u8 = std::ptr::null_mut();
            let mut frames = 0u32;
            let mut flags = 0u32;
            if unsafe { capture.GetBuffer(&mut data_ptr, &mut frames, &mut flags, None, None) }
                .is_err()
            {
                break;
            }
            let byte_count = (frames as usize) * bytes_per_frame;
            let pcm = if (flags & (AUDCLNT_BUFFERFLAGS_SILENT.0 as u32)) != 0 || data_ptr.is_null()
            {
                vec![0u8; byte_count]
            } else {
                unsafe { std::slice::from_raw_parts(data_ptr, byte_count) }.to_vec()
            };
            unsafe {
                let _ = capture.ReleaseBuffer(frames);
            }
            let duration_100ns = ((frames as i64) * 10_000_000) / (format.nSamplesPerSec as i64);
            let _ = tx.send(EncodedPacket {
                track,
                data: pcm,
                timestamp_100ns: qpc_100ns(),
                duration_100ns,
                is_keyframe: true,
            });
            next_len = unsafe { capture.GetNextPacketSize() }.unwrap_or(0);
        }
    }

    unsafe {
        let _ = client.Stop();
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn three_track_plan_isolates_game_mic_and_discord_voice() {
        let plan = build_three_track_plan(Some(4242), Some(9090), true, true);
        assert_eq!(plan.len(), 3);
        assert_eq!(
            plan[0],
            AudioSourceSpec::ProcessLoopbackInclude {
                pid: 4242,
                track: TrackKind::AudioGame
            }
        );
        assert_eq!(plan[1], AudioSourceSpec::DefaultMicrophone);
        assert_eq!(
            plan[2],
            AudioSourceSpec::ProcessLoopbackInclude {
                pid: 9090,
                track: TrackKind::AudioVoice
            }
        );

        let fallback = build_three_track_plan(Some(4242), None, false, true);
        assert_eq!(fallback.len(), 2);
        assert_eq!(
            fallback[1],
            AudioSourceSpec::ProcessLoopbackExclude {
                pid: 4242,
                track: TrackKind::AudioVoice
            }
        );
    }
}
