#![allow(clippy::not_unsafe_ptr_arg_deref)]

use crate::dsp::{DspConfig, DspEngine, MAX_CHANNELS};
use crate::shm_ipc::{ShmChannel, ShmScope};
use std::cell::UnsafeCell;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU32, AtomicU64, AtomicUsize, Ordering};

#[cfg(windows)]
use windows::Win32::Foundation::{
    CLASS_E_CLASSNOTAVAILABLE, CLASS_E_NOAGGREGATION, E_INVALIDARG, E_OUTOFMEMORY, E_POINTER,
    HANDLE, S_FALSE, S_OK,
};
#[cfg(windows)]
use windows::Win32::Media::Audio::Apo::{
    APO_CONNECTION_DESCRIPTOR, APO_CONNECTION_PROPERTY, APO_FLAG, APO_FLAG_DEFAULT,
    APO_FLAG_INPLACE, APO_REG_PROPERTIES, APOERR_ALREADY_INITIALIZED, APOERR_ALREADY_UNLOCKED,
    APOERR_APO_LOCKED, APOERR_FORMAT_NOT_SUPPORTED, APOERR_INVALID_CONNECTION_FORMAT,
    APOERR_NUM_CONNECTIONS_INVALID, APOInitBaseStruct, AUDIO_SYSTEMEFFECT,
    AUDIO_SYSTEMEFFECT_STATE, AUDIO_SYSTEMEFFECT_STATE_OFF, AUDIO_SYSTEMEFFECT_STATE_ON,
    BUFFER_SILENT, BUFFER_VALID, IAudioMediaType, IAudioMediaType_Impl, IAudioProcessingObject,
    IAudioProcessingObject_Impl, IAudioProcessingObjectConfiguration,
    IAudioProcessingObjectConfiguration_Impl, IAudioProcessingObjectRT,
    IAudioProcessingObjectRT_Impl, IAudioSystemEffects_Impl, IAudioSystemEffects2_Impl,
    IAudioSystemEffects3, IAudioSystemEffects3_Impl, UNCOMPRESSEDAUDIOFORMAT,
};
#[cfg(windows)]
use windows::Win32::Media::Audio::WAVEFORMATEX;
#[cfg(windows)]
use windows::Win32::System::Com::{CoTaskMemAlloc, IClassFactory, IClassFactory_Impl};
#[cfg(windows)]
use windows_core::{BOOL, Error, GUID, HRESULT, IUnknown, Interface, Ref, implement};

const WAVE_FORMAT_IEEE_FLOAT_TAG: u16 = 0x0003;

#[cfg(windows)]
pub const CLSID_EXO_AUDIO_APO_SFX: GUID = GUID::from_u128(0x7e3a8c14_4f2b_4a91_9d5e_2b8c6f1a4e01);
#[cfg(windows)]
pub const CLSID_EXO_AUDIO_APO_MFX: GUID = GUID::from_u128(0x7e3a8c14_4f2b_4a91_9d5e_2b8c6f1a4e02);
#[cfg(windows)]
pub const CLSID_EXO_AUDIO_APO_EFX: GUID = GUID::from_u128(0x7e3a8c14_4f2b_4a91_9d5e_2b8c6f1a4e03);
#[cfg(windows)]
pub const EFFECT_ID_EXO_EQ: GUID = GUID::from_u128(0x7e3a8c14_4f2b_4a91_9d5e_2b8c6f1a4e10);
#[cfg(windows)]
pub const KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: GUID =
    GUID::from_u128(0x00000003_0000_0010_8000_00aa00389b71);

const STATE_UNLOCKED: u8 = 0;
const STATE_CONFIGURING: u8 = 1;
const STATE_LOCKED: u8 = 2;
const STATE_IN_PROCESS: u8 = 3;

static LIVE_OBJECTS: AtomicUsize = AtomicUsize::new(0);
static SERVER_LOCKS: AtomicUsize = AtomicUsize::new(0);

pub fn can_unload_now() -> bool {
    LIVE_OBJECTS.load(Ordering::Acquire) == 0 && SERVER_LOCKS.load(Ordering::Acquire) == 0
}

pub fn live_object_count() -> usize {
    LIVE_OBJECTS.load(Ordering::Acquire)
}

#[cfg(windows)]
pub fn is_supported_clsid(clsid: &GUID) -> bool {
    *clsid == CLSID_EXO_AUDIO_APO_SFX
        || *clsid == CLSID_EXO_AUDIO_APO_MFX
        || *clsid == CLSID_EXO_AUDIO_APO_EFX
}

struct RtState {
    dsp: DspEngine,
    shm: Option<ShmChannel>,
    last_gen: u64,
    sample_rate_hz: u32,
    channels: u32,
}

#[cfg(windows)]
#[implement(
    IAudioProcessingObject,
    IAudioProcessingObjectConfiguration,
    IAudioProcessingObjectRT,
    IAudioSystemEffects3
)]
pub struct ExoAudioApo {
    clsid: GUID,
    initialized: AtomicBool,
    effect_enabled: AtomicBool,
    lock_state: AtomicU8,
    channels: AtomicU32,
    processed_frames: AtomicU64,
    rt: UnsafeCell<RtState>,
}

#[cfg(windows)]
unsafe impl Send for ExoAudioApo {}
#[cfg(windows)]
unsafe impl Sync for ExoAudioApo {}

#[cfg(windows)]
impl Drop for ExoAudioApo {
    fn drop(&mut self) {
        LIVE_OBJECTS.fetch_sub(1, Ordering::AcqRel);
    }
}

#[cfg(windows)]
impl ExoAudioApo {
    pub fn new(clsid: GUID) -> Self {
        LIVE_OBJECTS.fetch_add(1, Ordering::AcqRel);
        Self {
            clsid,
            initialized: AtomicBool::new(false),
            effect_enabled: AtomicBool::new(true),
            lock_state: AtomicU8::new(STATE_UNLOCKED),
            channels: AtomicU32::new(2),
            processed_frames: AtomicU64::new(0),
            rt: UnsafeCell::new(RtState {
                dsp: DspEngine::new(48_000.0, 2),
                shm: None,
                last_gen: 0,
                sample_rate_hz: 48_000,
                channels: 2,
            }),
        }
    }

    pub fn new_with_shm(clsid: GUID, shm: ShmChannel) -> Self {
        let apo = Self::new(clsid);
        unsafe {
            (*apo.rt.get()).shm = Some(shm);
        }
        apo
    }

    pub fn apply_config_direct(&self, cfg: &DspConfig) -> bool {
        if self
            .lock_state
            .compare_exchange(
                STATE_UNLOCKED,
                STATE_CONFIGURING,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .is_err()
        {
            return false;
        }
        unsafe {
            (*self.rt.get()).dsp.apply_config_immediate(cfg);
        }
        self.lock_state.store(STATE_UNLOCKED, Ordering::Release);
        true
    }

    pub fn processed_frames(&self) -> u64 {
        self.processed_frames.load(Ordering::Relaxed)
    }

    pub fn is_effect_enabled(&self) -> bool {
        self.effect_enabled.load(Ordering::Acquire)
    }

    fn validate_uncompressed_format(fmt: &UNCOMPRESSEDAUDIOFORMAT) -> bool {
        fmt.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            && fmt.dwBytesPerSampleContainer == 4
            && fmt.dwValidBitsPerSample == 32
            && fmt.dwSamplesPerFrame >= 1
            && (fmt.dwSamplesPerFrame as usize) <= MAX_CHANNELS
            && fmt.fFramesPerSecond >= 8_000.0
            && fmt.fFramesPerSecond <= 384_000.0
    }

    fn copy_wide_truncated(dst: &mut [u16; 256], text: &str) {
        for (slot, unit) in dst.iter_mut().zip(text.encode_utf16().take(255)) {
            *slot = unit;
        }
    }

    fn check_format_supported(
        requested: Ref<'_, IAudioMediaType>,
    ) -> windows_core::Result<IAudioMediaType> {
        let req = requested.ok()?;
        let compressed = unsafe { req.IsCompressedFormat()? };
        if compressed.as_bool() {
            return Err(Error::from(APOERR_FORMAT_NOT_SUPPORTED));
        }
        let mut fmt = UNCOMPRESSEDAUDIOFORMAT::default();
        unsafe { req.GetUncompressedAudioFormat(&mut fmt)? };
        if !Self::validate_uncompressed_format(&fmt) {
            return Err(Error::from(APOERR_FORMAT_NOT_SUPPORTED));
        }
        Ok(req.clone())
    }
}

#[cfg(windows)]
impl IAudioProcessingObject_Impl for ExoAudioApo_Impl {
    fn Reset(&self) -> windows_core::Result<()> {
        let state = self.lock_state.load(Ordering::Acquire);
        if state == STATE_IN_PROCESS {
            return Err(Error::from(APOERR_APO_LOCKED));
        }
        unsafe {
            (*self.rt.get()).dsp.reset();
        }
        Ok(())
    }

    fn GetLatency(&self) -> windows_core::Result<i64> {
        Ok(0)
    }

    fn GetRegistrationProperties(&self) -> windows_core::Result<*mut APO_REG_PROPERTIES> {
        let size = std::mem::size_of::<APO_REG_PROPERTIES>() + std::mem::size_of::<GUID>() * 3;
        let ptr = unsafe { CoTaskMemAlloc(size) as *mut APO_REG_PROPERTIES };
        if ptr.is_null() {
            return Err(Error::from(E_OUTOFMEMORY));
        }
        unsafe {
            std::ptr::write_bytes(ptr as *mut u8, 0, size);
            let props = &mut *ptr;
            props.clsid = self.clsid;
            props.Flags = APO_FLAG(APO_FLAG_DEFAULT.0 | APO_FLAG_INPLACE.0);
            ExoAudioApo::copy_wide_truncated(&mut props.szFriendlyName, "Exo Audio EQ APO");
            ExoAudioApo::copy_wide_truncated(
                &mut props.szCopyrightInfo,
                "Exo Launcher (MIT Clean-Room APO)",
            );
            props.u32MajorVersion = 1;
            props.u32MinorVersion = 0;
            props.u32MinInputConnections = 1;
            props.u32MaxInputConnections = 1;
            props.u32MinOutputConnections = 1;
            props.u32MaxOutputConnections = 1;
            props.u32MaxInstances = u32::MAX;
            props.u32NumAPOInterfaces = 4;
            let list = props.iidAPOInterfaceList.as_mut_ptr();
            *list.add(0) = IAudioProcessingObject::IID;
            *list.add(1) = IAudioProcessingObjectRT::IID;
            *list.add(2) = IAudioProcessingObjectConfiguration::IID;
            *list.add(3) = IAudioSystemEffects3::IID;
        }
        Ok(ptr)
    }

    fn Initialize(&self, cbdatasize: u32, pbydata: *const u8) -> windows_core::Result<()> {
        if self.initialized.swap(true, Ordering::AcqRel) {
            return Err(Error::from(APOERR_ALREADY_INITIALIZED));
        }
        if cbdatasize >= std::mem::size_of::<APOInitBaseStruct>() as u32 && !pbydata.is_null() {
            let base = unsafe { &*(pbydata as *const APOInitBaseStruct) };
            if base.cbSize < std::mem::size_of::<APOInitBaseStruct>() as u32 {
                self.initialized.store(false, Ordering::Release);
                return Err(Error::from(E_INVALIDARG));
            }
        }
        unsafe {
            let rt = &mut *self.rt.get();
            if rt.shm.is_none()
                && let Ok(ch) = ShmChannel::open_existing("default", ShmScope::Global)
                    .or_else(|_| ShmChannel::open_existing("default", ShmScope::Local))
            {
                rt.shm = Some(ch);
            }
        }
        Ok(())
    }

    fn IsInputFormatSupported(
        &self,
        _poppositeformat: Ref<'_, IAudioMediaType>,
        prequestedinputformat: Ref<'_, IAudioMediaType>,
    ) -> windows_core::Result<IAudioMediaType> {
        ExoAudioApo::check_format_supported(prequestedinputformat)
    }

    fn IsOutputFormatSupported(
        &self,
        _poppositeformat: Ref<'_, IAudioMediaType>,
        prequestedoutputformat: Ref<'_, IAudioMediaType>,
    ) -> windows_core::Result<IAudioMediaType> {
        ExoAudioApo::check_format_supported(prequestedoutputformat)
    }

    fn GetInputChannelCount(&self) -> windows_core::Result<u32> {
        Ok(self.channels.load(Ordering::Acquire))
    }
}

#[cfg(windows)]
impl IAudioProcessingObjectConfiguration_Impl for ExoAudioApo_Impl {
    fn LockForProcess(
        &self,
        u32numinputconnections: u32,
        ppinputconnections: *const *const APO_CONNECTION_DESCRIPTOR,
        u32numoutputconnections: u32,
        ppoutputconnections: *const *const APO_CONNECTION_DESCRIPTOR,
    ) -> windows_core::Result<()> {
        if u32numinputconnections != 1 || u32numoutputconnections != 1 {
            return Err(Error::from(APOERR_NUM_CONNECTIONS_INVALID));
        }
        if ppinputconnections.is_null() || ppoutputconnections.is_null() {
            return Err(Error::from(E_POINTER));
        }
        let in_desc = unsafe { *ppinputconnections };
        let out_desc = unsafe { *ppoutputconnections };
        if in_desc.is_null() || out_desc.is_null() {
            return Err(Error::from(E_POINTER));
        }

        let in_fmt = unsafe { (*in_desc).pFormat.as_ref() }
            .ok_or_else(|| Error::from(APOERR_INVALID_CONNECTION_FORMAT))?;
        let out_fmt = unsafe { (*out_desc).pFormat.as_ref() }
            .ok_or_else(|| Error::from(APOERR_INVALID_CONNECTION_FORMAT))?;

        let mut in_uncomp = UNCOMPRESSEDAUDIOFORMAT::default();
        let mut out_uncomp = UNCOMPRESSEDAUDIOFORMAT::default();
        unsafe {
            in_fmt.GetUncompressedAudioFormat(&mut in_uncomp)?;
            out_fmt.GetUncompressedAudioFormat(&mut out_uncomp)?;
        }
        if !ExoAudioApo::validate_uncompressed_format(&in_uncomp)
            || !ExoAudioApo::validate_uncompressed_format(&out_uncomp)
            || in_uncomp.dwSamplesPerFrame != out_uncomp.dwSamplesPerFrame
            || (in_uncomp.fFramesPerSecond - out_uncomp.fFramesPerSecond).abs() > 0.5
        {
            return Err(Error::from(APOERR_INVALID_CONNECTION_FORMAT));
        }

        self.lock_state
            .compare_exchange(
                STATE_UNLOCKED,
                STATE_CONFIGURING,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .map_err(|_| Error::from(APOERR_APO_LOCKED))?;

        let ch = in_uncomp.dwSamplesPerFrame;
        let fs = in_uncomp.fFramesPerSecond as f64;
        self.channels.store(ch, Ordering::Release);
        unsafe {
            let rt = &mut *self.rt.get();
            rt.sample_rate_hz = fs.round() as u32;
            rt.channels = ch;
            rt.dsp.set_format(fs, ch as usize);
            if let Some(ref shm) = rt.shm
                && let Some(cfg) = shm.try_read_if_newer(&mut rt.last_gen)
            {
                rt.dsp.apply_config_immediate(&cfg);
            }
        }
        self.lock_state.store(STATE_LOCKED, Ordering::Release);
        Ok(())
    }

    fn UnlockForProcess(&self) -> windows_core::Result<()> {
        self.lock_state
            .compare_exchange(
                STATE_LOCKED,
                STATE_UNLOCKED,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .map_err(|_| Error::from(APOERR_ALREADY_UNLOCKED))?;
        Ok(())
    }
}

#[cfg(windows)]
impl IAudioProcessingObjectRT_Impl for ExoAudioApo_Impl {
    fn APOProcess(
        &self,
        u32numinputconnections: u32,
        ppinputconnections: *const *const APO_CONNECTION_PROPERTY,
        u32numoutputconnections: u32,
        ppoutputconnections: *mut *mut APO_CONNECTION_PROPERTY,
    ) {
        if u32numinputconnections == 0
            || u32numoutputconnections == 0
            || ppinputconnections.is_null()
            || ppoutputconnections.is_null()
        {
            return;
        }
        let in_prop = unsafe { *ppinputconnections };
        let out_prop = unsafe { *ppoutputconnections };
        if in_prop.is_null() || out_prop.is_null() {
            return;
        }
        let in_conn = unsafe { &*in_prop };
        let out_conn = unsafe { &mut *out_prop };

        let frames = in_conn.u32ValidFrameCount as usize;
        out_conn.u32ValidFrameCount = in_conn.u32ValidFrameCount;
        out_conn.u32BufferFlags = in_conn.u32BufferFlags;

        let ch = self.channels.load(Ordering::Relaxed) as usize;
        let Some(total_samples) = frames.checked_mul(ch) else {
            return;
        };
        if total_samples == 0 || in_conn.pBuffer == 0 || out_conn.pBuffer == 0 {
            return;
        }

        if in_conn.u32BufferFlags == BUFFER_SILENT {
            if in_conn.pBuffer != out_conn.pBuffer {
                let dst = unsafe {
                    std::slice::from_raw_parts_mut(out_conn.pBuffer as *mut f32, total_samples)
                };
                dst.fill(0.0);
            }
            return;
        }

        if in_conn.u32BufferFlags != BUFFER_VALID {
            return;
        }

        if in_conn.pBuffer != out_conn.pBuffer {
            unsafe {
                std::ptr::copy_nonoverlapping(
                    in_conn.pBuffer as *const f32,
                    out_conn.pBuffer as *mut f32,
                    total_samples,
                );
            }
        }

        if !self.effect_enabled.load(Ordering::Relaxed) {
            return;
        }

        if self
            .lock_state
            .compare_exchange(
                STATE_LOCKED,
                STATE_IN_PROCESS,
                Ordering::AcqRel,
                Ordering::Relaxed,
            )
            .is_err()
        {
            return;
        }

        unsafe {
            let rt = &mut *self.rt.get();
            if let Some(ref shm) = rt.shm
                && let Some(cfg) = shm.try_read_if_newer(&mut rt.last_gen)
            {
                rt.dsp.apply_config_smooth(&cfg);
            }
            let buf = std::slice::from_raw_parts_mut(out_conn.pBuffer as *mut f32, total_samples);
            let metrics = rt.dsp.process_interleaved_f32(buf);
            self.processed_frames
                .fetch_add(frames as u64, Ordering::Relaxed);
            if let Some(ref shm) = rt.shm {
                shm.block().record_rt_telemetry(
                    frames as u64,
                    rt.last_gen,
                    metrics.peak_dbfs,
                    metrics.max_limiter_gr_db,
                    rt.sample_rate_hz,
                    rt.channels,
                );
            }
        }

        self.lock_state.store(STATE_LOCKED, Ordering::Release);
    }

    fn CalcInputFrames(&self, u32outputframecount: u32) -> u32 {
        u32outputframecount
    }

    fn CalcOutputFrames(&self, u32inputframecount: u32) -> u32 {
        u32inputframecount
    }
}

#[cfg(windows)]
impl IAudioSystemEffects_Impl for ExoAudioApo_Impl {}

#[cfg(windows)]
impl IAudioSystemEffects2_Impl for ExoAudioApo_Impl {
    fn GetEffectsList(
        &self,
        ppeffectsids: *mut *mut GUID,
        pceffects: *mut u32,
        _event: HANDLE,
    ) -> windows_core::Result<()> {
        if ppeffectsids.is_null() || pceffects.is_null() {
            return Err(Error::from(E_POINTER));
        }
        let mem = unsafe { CoTaskMemAlloc(std::mem::size_of::<GUID>()) as *mut GUID };
        if mem.is_null() {
            return Err(Error::from(E_OUTOFMEMORY));
        }
        unsafe {
            *mem = EFFECT_ID_EXO_EQ;
            *ppeffectsids = mem;
            *pceffects = 1;
        }
        Ok(())
    }
}

#[cfg(windows)]
impl IAudioSystemEffects3_Impl for ExoAudioApo_Impl {
    fn GetControllableSystemEffectsList(
        &self,
        effects: *mut *mut AUDIO_SYSTEMEFFECT,
        numeffects: *mut u32,
        _event: HANDLE,
    ) -> windows_core::Result<()> {
        if effects.is_null() || numeffects.is_null() {
            return Err(Error::from(E_POINTER));
        }
        let mem = unsafe {
            CoTaskMemAlloc(std::mem::size_of::<AUDIO_SYSTEMEFFECT>()) as *mut AUDIO_SYSTEMEFFECT
        };
        if mem.is_null() {
            return Err(Error::from(E_OUTOFMEMORY));
        }
        let state = if self.effect_enabled.load(Ordering::Acquire) {
            AUDIO_SYSTEMEFFECT_STATE_ON
        } else {
            AUDIO_SYSTEMEFFECT_STATE_OFF
        };
        unsafe {
            *mem = AUDIO_SYSTEMEFFECT {
                id: EFFECT_ID_EXO_EQ,
                canSetState: BOOL(1),
                state,
            };
            *effects = mem;
            *numeffects = 1;
        }
        Ok(())
    }

    fn SetAudioSystemEffectState(
        &self,
        effectid: &GUID,
        state: AUDIO_SYSTEMEFFECT_STATE,
    ) -> windows_core::Result<()> {
        if *effectid != EFFECT_ID_EXO_EQ {
            return Err(Error::from(E_INVALIDARG));
        }
        let enable = state == AUDIO_SYSTEMEFFECT_STATE_ON;
        self.effect_enabled.store(enable, Ordering::Release);
        Ok(())
    }
}

#[cfg(windows)]
#[implement(IClassFactory)]
pub struct ExoApoClassFactory {
    clsid: GUID,
}

#[cfg(windows)]
impl ExoApoClassFactory {
    pub fn new(clsid: GUID) -> Self {
        LIVE_OBJECTS.fetch_add(1, Ordering::AcqRel);
        Self { clsid }
    }
}

#[cfg(windows)]
impl Drop for ExoApoClassFactory {
    fn drop(&mut self) {
        LIVE_OBJECTS.fetch_sub(1, Ordering::AcqRel);
    }
}

#[cfg(windows)]
impl IClassFactory_Impl for ExoApoClassFactory_Impl {
    fn CreateInstance(
        &self,
        punkouter: Ref<'_, IUnknown>,
        riid: *const GUID,
        ppvobject: *mut *mut core::ffi::c_void,
    ) -> windows_core::Result<()> {
        if ppvobject.is_null() || riid.is_null() {
            return Err(Error::from(E_POINTER));
        }
        unsafe {
            *ppvobject = core::ptr::null_mut();
        }
        if punkouter.is_some() {
            return Err(Error::from(CLASS_E_NOAGGREGATION));
        }
        let apo: IAudioProcessingObject = ExoAudioApo::new(self.clsid).into();
        let hr = unsafe { apo.query(riid, ppvobject) };
        hr.ok()
    }

    fn LockServer(&self, flock: BOOL) -> windows_core::Result<()> {
        if flock.as_bool() {
            SERVER_LOCKS.fetch_add(1, Ordering::AcqRel);
        } else {
            SERVER_LOCKS.fetch_sub(1, Ordering::AcqRel);
        }
        Ok(())
    }
}

#[cfg(windows)]
pub unsafe fn dll_get_class_object(
    rclsid: *const GUID,
    riid: *const GUID,
    ppv: *mut *mut core::ffi::c_void,
) -> HRESULT {
    if ppv.is_null() || rclsid.is_null() || riid.is_null() {
        return E_POINTER;
    }
    unsafe {
        *ppv = core::ptr::null_mut();
        let clsid = &*rclsid;
        if !is_supported_clsid(clsid) {
            return CLASS_E_CLASSNOTAVAILABLE;
        }
        let factory: IClassFactory = ExoApoClassFactory::new(*clsid).into();
        factory.query(riid, ppv)
    }
}

#[cfg(windows)]
pub fn dll_can_unload_now() -> HRESULT {
    if can_unload_now() { S_OK } else { S_FALSE }
}

#[cfg(windows)]
#[implement(IAudioMediaType)]
pub struct TestAudioMediaType {
    uncompressed: UNCOMPRESSEDAUDIOFORMAT,
    wave: WAVEFORMATEX,
}

#[cfg(windows)]
impl TestAudioMediaType {
    pub fn ieee_float(sample_rate: f32, channels: u32) -> IAudioMediaType {
        let block_align = (channels * 4) as u16;
        let avg_bytes = (sample_rate as u32) * (block_align as u32);
        let obj = Self {
            uncompressed: UNCOMPRESSEDAUDIOFORMAT {
                guidFormatType: KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
                dwSamplesPerFrame: channels,
                dwBytesPerSampleContainer: 4,
                dwValidBitsPerSample: 32,
                fFramesPerSecond: sample_rate,
                dwChannelMask: if channels == 2 { 0x3 } else { 0x4 },
            },
            wave: WAVEFORMATEX {
                wFormatTag: WAVE_FORMAT_IEEE_FLOAT_TAG,
                nChannels: channels as u16,
                nSamplesPerSec: sample_rate as u32,
                nAvgBytesPerSec: avg_bytes,
                nBlockAlign: block_align,
                wBitsPerSample: 32,
                cbSize: 0,
            },
        };
        obj.into()
    }
}

#[cfg(windows)]
impl IAudioMediaType_Impl for TestAudioMediaType_Impl {
    fn IsCompressedFormat(&self) -> windows_core::Result<BOOL> {
        Ok(BOOL(0))
    }

    fn IsEqual(&self, _piaudiotype: Ref<'_, IAudioMediaType>) -> windows_core::Result<u32> {
        Ok(0)
    }

    fn GetAudioFormat(&self) -> *mut WAVEFORMATEX {
        (&self.wave as *const WAVEFORMATEX).cast_mut()
    }

    fn GetUncompressedAudioFormat(
        &self,
        puncompressedaudioformat: *mut UNCOMPRESSEDAUDIOFORMAT,
    ) -> windows_core::Result<()> {
        if puncompressedaudioformat.is_null() {
            return Err(Error::from(E_POINTER));
        }
        unsafe {
            *puncompressedaudioformat = self.uncompressed;
        }
        Ok(())
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use crate::dsp::ParametricBand;
    use core::mem::ManuallyDrop;
    use windows::Win32::Media::Audio::Apo::APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    use windows::Win32::System::Com::CoTaskMemFree;

    #[test]
    fn com_class_factory_creates_apo_and_exposes_all_interfaces() {
        let mut raw_factory: *mut core::ffi::c_void = core::ptr::null_mut();
        let hr = unsafe {
            dll_get_class_object(
                &CLSID_EXO_AUDIO_APO_EFX,
                &IClassFactory::IID,
                &mut raw_factory,
            )
        };
        assert_eq!(hr, S_OK);
        let factory: IClassFactory = unsafe { IClassFactory::from_raw(raw_factory) };
        let apo: IAudioProcessingObject =
            unsafe { factory.CreateInstance(None).expect("create apo") };

        assert_eq!(unsafe { apo.GetLatency().expect("latency") }, 0);
        let props_ptr = unsafe { apo.GetRegistrationProperties().expect("reg props") };
        unsafe {
            assert_eq!((*props_ptr).clsid, CLSID_EXO_AUDIO_APO_EFX);
            assert_eq!((*props_ptr).u32MinInputConnections, 1);
            assert_eq!((*props_ptr).u32NumAPOInterfaces, 4);
            CoTaskMemFree(Some(props_ptr as *const core::ffi::c_void));
        }

        let fx3: IAudioSystemEffects3 = apo.cast().expect("IAudioSystemEffects3");
        let mut effects_ptr: *mut AUDIO_SYSTEMEFFECT = core::ptr::null_mut();
        let mut count = 0u32;
        unsafe {
            fx3.GetControllableSystemEffectsList(&mut effects_ptr, &mut count, None)
                .expect("effects list");
            assert_eq!(count, 1);
            assert_eq!((*effects_ptr).id, EFFECT_ID_EXO_EQ);
            assert_eq!((*effects_ptr).state, AUDIO_SYSTEMEFFECT_STATE_ON);
            CoTaskMemFree(Some(effects_ptr as *const core::ffi::c_void));

            fx3.SetAudioSystemEffectState(EFFECT_ID_EXO_EQ, AUDIO_SYSTEMEFFECT_STATE_OFF)
                .expect("disable effect");
            fx3.GetControllableSystemEffectsList(&mut effects_ptr, &mut count, None)
                .expect("effects list off");
            assert_eq!((*effects_ptr).state, AUDIO_SYSTEMEFFECT_STATE_OFF);
            CoTaskMemFree(Some(effects_ptr as *const core::ffi::c_void));
        }
    }

    #[test]
    fn com_apo_fail_pass_through_when_unlocked_and_processes_when_locked() {
        let (writer, reader) = ShmChannel::in_memory("{com-test-ep}");
        let apo: IAudioProcessingObject =
            ExoAudioApo::new_with_shm(CLSID_EXO_AUDIO_APO_EFX, reader).into();
        let cfg_iface: IAudioProcessingObjectConfiguration = apo.cast().expect("config");
        let rt_iface: IAudioProcessingObjectRT = apo.cast().expect("rt");

        let mut samples = vec![0.25_f32; 480 * 2];
        let original = samples.clone();
        let in_prop = APO_CONNECTION_PROPERTY {
            pBuffer: samples.as_mut_ptr() as usize,
            u32ValidFrameCount: 480,
            u32BufferFlags: BUFFER_VALID,
            u32Signature: 0,
        };
        let mut out_prop = in_prop;
        let in_ptrs = [&in_prop as *const APO_CONNECTION_PROPERTY];
        let mut out_ptrs = [&mut out_prop as *mut APO_CONNECTION_PROPERTY];

        unsafe {
            rt_iface.APOProcess(1, in_ptrs.as_ptr(), 1, out_ptrs.as_mut_ptr());
        }
        assert_eq!(samples, original);

        writer.publish(&DspConfig::from_bands(&[ParametricBand::low_shelf(
            200.0, -12.0, 0.71,
        )]));

        let fmt = TestAudioMediaType::ieee_float(48_000.0, 2);
        let desc = APO_CONNECTION_DESCRIPTOR {
            Type: APO_CONNECTION_BUFFER_TYPE_EXTERNAL,
            pBuffer: samples.as_mut_ptr() as usize,
            u32MaxFrameCount: 480,
            pFormat: ManuallyDrop::new(Some(fmt.clone())),
            u32Signature: 0,
        };
        let descs = [&desc as *const APO_CONNECTION_DESCRIPTOR];
        unsafe {
            cfg_iface
                .LockForProcess(&descs, &descs)
                .expect("lock for process");
            rt_iface.APOProcess(1, in_ptrs.as_ptr(), 1, out_ptrs.as_mut_ptr());
            cfg_iface.UnlockForProcess().expect("unlock");
        }
        assert_ne!(samples, original);
        assert_eq!(unsafe { rt_iface.CalcInputFrames(480) }, 480);
        assert_eq!(unsafe { rt_iface.CalcOutputFrames(480) }, 480);
    }
}
