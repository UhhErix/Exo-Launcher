use crate::dsp::{DspConfig, FilterKind, MAX_BANDS, ParametricBand, SpatialMode};
use std::sync::Arc;
use std::sync::atomic::{AtomicI32, AtomicU32, AtomicU64, Ordering};

pub const SHM_MAGIC: u64 = 0x4558_4F41_504F_0001;
pub const SHM_VERSION: u32 = 1;
pub const SHM_GLOBAL_PREFIX: &str = "Global\\ExoApoCfg_";
pub const SHM_LOCAL_PREFIX: &str = "Local\\ExoApoCfg_";

#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct ShmBand {
    pub kind: u8,
    pub _pad: [u8; 7],
    pub freq_hz: f64,
    pub gain_db: f64,
    pub q: f64,
}

impl From<ParametricBand> for ShmBand {
    fn from(b: ParametricBand) -> Self {
        Self {
            kind: b.kind as u8,
            _pad: [0; 7],
            freq_hz: b.freq_hz,
            gain_db: b.gain_db,
            q: b.q,
        }
    }
}

impl From<ShmBand> for ParametricBand {
    fn from(b: ShmBand) -> Self {
        Self {
            kind: FilterKind::from_u8(b.kind),
            freq_hz: b.freq_hz,
            gain_db: b.gain_db,
            q: b.q,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct ShmConfigPayload {
    pub bypass: u8,
    pub auto_preamp: u8,
    pub limiter_enabled: u8,
    pub spatial_mode: u8,
    pub band_count: u32,
    pub preamp_db: f64,
    pub spatial_strength: f64,
    pub bands: [ShmBand; MAX_BANDS],
    pub crc32: u32,
    pub _reserved: u32,
}

impl Default for ShmConfigPayload {
    fn default() -> Self {
        let mut p = Self {
            bypass: 0,
            auto_preamp: 1,
            limiter_enabled: 1,
            spatial_mode: 0,
            band_count: 0,
            preamp_db: 0.0,
            spatial_strength: 0.5,
            bands: [ShmBand::default(); MAX_BANDS],
            crc32: 0,
            _reserved: 0,
        };
        p.crc32 = p.compute_crc32();
        p
    }
}

impl ShmConfigPayload {
    pub fn from_dsp(cfg: &DspConfig) -> Self {
        let mut bands = [ShmBand::default(); MAX_BANDS];
        let count = cfg.band_count.min(MAX_BANDS);
        for (dst, &src) in bands.iter_mut().zip(cfg.bands.iter()).take(count) {
            *dst = ShmBand::from(src);
        }
        let mut payload = Self {
            bypass: u8::from(cfg.bypass),
            auto_preamp: u8::from(cfg.auto_preamp),
            limiter_enabled: u8::from(cfg.limiter_enabled),
            spatial_mode: cfg.spatial_mode as u8,
            band_count: count as u32,
            preamp_db: cfg.preamp_db,
            spatial_strength: cfg.spatial_strength,
            bands,
            crc32: 0,
            _reserved: 0,
        };
        payload.crc32 = payload.compute_crc32();
        payload
    }

    pub fn to_dsp(&self) -> Option<DspConfig> {
        if self.crc32 != self.compute_crc32() {
            return None;
        }
        let count = (self.band_count as usize).min(MAX_BANDS);
        let mut bands = [ParametricBand::off(); MAX_BANDS];
        for (dst, &src) in bands.iter_mut().zip(self.bands.iter()).take(count) {
            *dst = ParametricBand::from(src);
        }
        Some(DspConfig {
            bypass: self.bypass != 0,
            preamp_db: self.preamp_db,
            auto_preamp: self.auto_preamp != 0,
            limiter_enabled: self.limiter_enabled != 0,
            spatial_mode: SpatialMode::from_u8(self.spatial_mode),
            spatial_strength: self.spatial_strength,
            band_count: count,
            bands,
        })
    }

    pub fn compute_crc32(&self) -> u32 {
        let mut hasher = crc32fast::Hasher::new();
        hasher.update(&[
            self.bypass,
            self.auto_preamp,
            self.limiter_enabled,
            self.spatial_mode,
        ]);
        hasher.update(&self.band_count.to_le_bytes());
        hasher.update(&self.preamp_db.to_bits().to_le_bytes());
        hasher.update(&self.spatial_strength.to_bits().to_le_bytes());
        for b in &self.bands {
            hasher.update(&[b.kind]);
            hasher.update(&b.freq_hz.to_bits().to_le_bytes());
            hasher.update(&b.gain_db.to_bits().to_le_bytes());
            hasher.update(&b.q.to_bits().to_le_bytes());
        }
        hasher.finalize()
    }
}

#[repr(C, align(64))]
pub struct ShmSlot {
    pub seqlock: AtomicU32,
    pub _pad: u32,
    pub payload: std::cell::UnsafeCell<ShmConfigPayload>,
}

unsafe impl Sync for ShmSlot {}

impl Default for ShmSlot {
    fn default() -> Self {
        Self::new()
    }
}

impl ShmSlot {
    pub fn new() -> Self {
        Self {
            seqlock: AtomicU32::new(0),
            _pad: 0,
            payload: std::cell::UnsafeCell::new(ShmConfigPayload::default()),
        }
    }
}

#[repr(C, align(64))]
pub struct ShmTelemetry {
    pub frames_processed: AtomicU64,
    pub active_generation: AtomicU64,
    pub peak_centi_dbfs: AtomicI32,
    pub limiter_gr_centi_db: AtomicU32,
    pub sample_rate_hz: AtomicU32,
    pub channels: AtomicU32,
}

impl Default for ShmTelemetry {
    fn default() -> Self {
        Self::new()
    }
}

impl ShmTelemetry {
    pub const fn new() -> Self {
        Self {
            frames_processed: AtomicU64::new(0),
            active_generation: AtomicU64::new(0),
            peak_centi_dbfs: AtomicI32::new(-12_000),
            limiter_gr_centi_db: AtomicU32::new(0),
            sample_rate_hz: AtomicU32::new(48_000),
            channels: AtomicU32::new(2),
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct TelemetrySnapshot {
    pub frames_processed: u64,
    pub active_generation: u64,
    pub peak_dbfs: f64,
    pub limiter_gr_db: f64,
    pub sample_rate_hz: u32,
    pub channels: u32,
}

#[repr(C, align(64))]
pub struct ShmBlock {
    pub magic: AtomicU64,
    pub version: AtomicU32,
    pub active_slot: AtomicU32,
    pub generation: AtomicU64,
    pub global_bypass: AtomicU32,
    pub _pad: u32,
    pub telemetry: ShmTelemetry,
    pub slots: [ShmSlot; 2],
}

impl Default for ShmBlock {
    fn default() -> Self {
        Self::new()
    }
}

impl ShmBlock {
    pub fn new() -> Self {
        Self {
            magic: AtomicU64::new(SHM_MAGIC),
            version: AtomicU32::new(SHM_VERSION),
            active_slot: AtomicU32::new(0),
            generation: AtomicU64::new(1),
            global_bypass: AtomicU32::new(0),
            _pad: 0,
            telemetry: ShmTelemetry::new(),
            slots: [ShmSlot::new(), ShmSlot::new()],
        }
    }

    pub fn is_valid_header(&self) -> bool {
        self.magic.load(Ordering::Acquire) == SHM_MAGIC
            && self.version.load(Ordering::Acquire) == SHM_VERSION
    }

    pub fn publish(&self, cfg: &DspConfig) -> u64 {
        let payload = ShmConfigPayload::from_dsp(cfg);
        let cur = self.active_slot.load(Ordering::Acquire) & 1;
        let next = cur ^ 1;
        let slot = &self.slots[next as usize];

        let seq = slot.seqlock.load(Ordering::Relaxed);
        let writing_seq = (seq & !1).wrapping_add(1);
        slot.seqlock.store(writing_seq, Ordering::Release);
        std::sync::atomic::fence(Ordering::Release);
        unsafe {
            std::ptr::write(slot.payload.get(), payload);
        }
        std::sync::atomic::fence(Ordering::Release);
        slot.seqlock
            .store(writing_seq.wrapping_add(1), Ordering::Release);

        self.active_slot.store(next, Ordering::Release);
        self.generation.fetch_add(1, Ordering::Release) + 1
    }

    pub fn try_read_if_newer(&self, last_seen_gen: &mut u64) -> Option<DspConfig> {
        if !self.is_valid_header() {
            return None;
        }
        let generation = self.generation.load(Ordering::Acquire);
        if generation == *last_seen_gen {
            return None;
        }
        let idx = (self.active_slot.load(Ordering::Acquire) & 1) as usize;
        let slot = &self.slots[idx];
        let seq1 = slot.seqlock.load(Ordering::Acquire);
        if (seq1 & 1) != 0 {
            return None;
        }
        let payload = unsafe { std::ptr::read(slot.payload.get()) };
        std::sync::atomic::fence(Ordering::Acquire);
        let seq2 = slot.seqlock.load(Ordering::Acquire);
        if seq1 != seq2 {
            return None;
        }
        let mut cfg = payload.to_dsp()?;
        if self.global_bypass.load(Ordering::Relaxed) != 0 {
            cfg.bypass = true;
        }
        *last_seen_gen = generation;
        Some(cfg)
    }

    pub fn record_rt_telemetry(
        &self,
        frames: u64,
        active_gen: u64,
        peak_dbfs: f64,
        limiter_gr_db: f64,
        sample_rate_hz: u32,
        channels: u32,
    ) {
        self.telemetry
            .frames_processed
            .fetch_add(frames, Ordering::Relaxed);
        self.telemetry
            .active_generation
            .store(active_gen, Ordering::Relaxed);
        let peak_centi = (peak_dbfs.clamp(-120.0, 24.0) * 100.0).round() as i32;
        self.telemetry
            .peak_centi_dbfs
            .store(peak_centi, Ordering::Relaxed);
        let gr_centi = (limiter_gr_db.clamp(0.0, 60.0) * 100.0).round() as u32;
        self.telemetry
            .limiter_gr_centi_db
            .store(gr_centi, Ordering::Relaxed);
        self.telemetry
            .sample_rate_hz
            .store(sample_rate_hz, Ordering::Relaxed);
        self.telemetry.channels.store(channels, Ordering::Relaxed);
    }

    pub fn read_telemetry(&self) -> TelemetrySnapshot {
        TelemetrySnapshot {
            frames_processed: self.telemetry.frames_processed.load(Ordering::Relaxed),
            active_generation: self.telemetry.active_generation.load(Ordering::Relaxed),
            peak_dbfs: self.telemetry.peak_centi_dbfs.load(Ordering::Relaxed) as f64 / 100.0,
            limiter_gr_db: self.telemetry.limiter_gr_centi_db.load(Ordering::Relaxed) as f64
                / 100.0,
            sample_rate_hz: self.telemetry.sample_rate_hz.load(Ordering::Relaxed),
            channels: self.telemetry.channels.load(Ordering::Relaxed),
        }
    }
}

pub fn sanitize_endpoint_id(endpoint_id: &str) -> String {
    let tail = endpoint_id.rsplit('.').next().unwrap_or(endpoint_id).trim();
    let mut out = String::with_capacity(tail.len().min(64));
    for ch in tail.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch.to_ascii_lowercase());
            if out.len() >= 64 {
                break;
            }
        }
    }
    if out.is_empty() {
        "default".to_string()
    } else {
        out
    }
}

pub fn shm_global_name(endpoint_id: &str) -> String {
    format!("{SHM_GLOBAL_PREFIX}{}", sanitize_endpoint_id(endpoint_id))
}

pub fn shm_local_name(endpoint_id: &str) -> String {
    format!("{SHM_LOCAL_PREFIX}{}", sanitize_endpoint_id(endpoint_id))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShmScope {
    Global,
    Local,
}

enum ShmBacking {
    InMemory(Arc<ShmBlock>),
    #[cfg(windows)]
    Win32 {
        handle: windows::Win32::Foundation::HANDLE,
        view: windows::Win32::System::Memory::MEMORY_MAPPED_VIEW_ADDRESS,
    },
}

unsafe impl Send for ShmBacking {}
unsafe impl Sync for ShmBacking {}

impl Drop for ShmBacking {
    fn drop(&mut self) {
        #[cfg(windows)]
        if let Self::Win32 { handle, view } = self {
            unsafe {
                let _ = windows::Win32::System::Memory::UnmapViewOfFile(*view);
                let _ = windows::Win32::Foundation::CloseHandle(*handle);
            }
        }
    }
}

pub struct ShmChannel {
    backing: ShmBacking,
    name: String,
}

impl ShmChannel {
    pub fn in_memory(endpoint_id: &str) -> (Self, Self) {
        let block = Arc::new(ShmBlock::new());
        let name = shm_local_name(endpoint_id);
        let writer = Self {
            backing: ShmBacking::InMemory(Arc::clone(&block)),
            name: name.clone(),
        };
        let reader = Self {
            backing: ShmBacking::InMemory(block),
            name,
        };
        (writer, reader)
    }

    pub fn from_shared_block(endpoint_id: &str, block: Arc<ShmBlock>) -> Self {
        Self {
            backing: ShmBacking::InMemory(block),
            name: shm_local_name(endpoint_id),
        }
    }

    #[cfg(windows)]
    pub fn create_or_open(endpoint_id: &str, scope: ShmScope) -> std::io::Result<Self> {
        use windows::Win32::Foundation::{
            CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, INVALID_HANDLE_VALUE,
        };
        use windows::Win32::System::Memory::{
            CreateFileMappingW, FILE_MAP_ALL_ACCESS, MapViewOfFile, PAGE_READWRITE,
        };
        use windows_core::PCWSTR;

        let name = match scope {
            ShmScope::Global => shm_global_name(endpoint_id),
            ShmScope::Local => shm_local_name(endpoint_id),
        };
        let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
        let size = std::mem::size_of::<ShmBlock>() as u32;

        unsafe {
            let handle = CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                None,
                PAGE_READWRITE,
                0,
                size,
                PCWSTR(wide.as_ptr()),
            )
            .map_err(|e| std::io::Error::from_raw_os_error(e.code().0))?;
            let already_existed = GetLastError() == ERROR_ALREADY_EXISTS;

            let view = MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, size as usize);
            if view.Value.is_null() {
                let err = std::io::Error::last_os_error();
                let _ = CloseHandle(handle);
                return Err(err);
            }

            let block_ptr = view.Value as *mut ShmBlock;
            if !already_existed || !(*block_ptr).is_valid_header() {
                std::ptr::write(block_ptr, ShmBlock::new());
            }

            Ok(Self {
                backing: ShmBacking::Win32 { handle, view },
                name,
            })
        }
    }

    #[cfg(windows)]
    pub fn open_existing(endpoint_id: &str, scope: ShmScope) -> std::io::Result<Self> {
        use windows::Win32::Foundation::CloseHandle;
        use windows::Win32::System::Memory::{
            FILE_MAP_ALL_ACCESS, MapViewOfFile, OpenFileMappingW,
        };
        use windows_core::PCWSTR;

        let name = match scope {
            ShmScope::Global => shm_global_name(endpoint_id),
            ShmScope::Local => shm_local_name(endpoint_id),
        };
        let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
        let size = std::mem::size_of::<ShmBlock>() as u32;

        unsafe {
            let handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, PCWSTR(wide.as_ptr()))
                .map_err(|e| std::io::Error::from_raw_os_error(e.code().0))?;
            let view = MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, size as usize);
            if view.Value.is_null() {
                let err = std::io::Error::last_os_error();
                let _ = CloseHandle(handle);
                return Err(err);
            }
            Ok(Self {
                backing: ShmBacking::Win32 { handle, view },
                name,
            })
        }
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn block(&self) -> &ShmBlock {
        match &self.backing {
            ShmBacking::InMemory(arc) => arc,
            #[cfg(windows)]
            ShmBacking::Win32 { view, .. } => unsafe { &*(view.Value as *const ShmBlock) },
        }
    }

    pub fn publish(&self, cfg: &DspConfig) -> u64 {
        self.block().publish(cfg)
    }

    pub fn try_read_if_newer(&self, last_seen_gen: &mut u64) -> Option<DspConfig> {
        self.block().try_read_if_newer(last_seen_gen)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sanitize_and_name_generation_strip_separators() {
        let ep = "{0.0.0.00000000}.{9709E4AB-1234-5678-ABCD-0123456789AB}";
        assert_eq!(
            shm_global_name(ep),
            "Global\\ExoApoCfg_9709e4ab-1234-5678-abcd-0123456789ab"
        );
        assert_eq!(
            shm_local_name("..\\..\\evil/name"),
            "Local\\ExoApoCfg_evilname"
        );
    }

    #[test]
    fn double_buffered_publish_and_read_survives_1000_swaps() {
        let (writer, reader) = ShmChannel::in_memory("{test-endpoint}");
        let mut last_gen = 0u64;

        for i in 0..1_000 {
            let gain = -12.0 + (i % 25) as f64;
            let cfg = DspConfig::from_bands(&[ParametricBand::peak(1_000.0 + i as f64, gain, 1.2)]);
            let g = writer.publish(&cfg);
            let read = reader
                .try_read_if_newer(&mut last_gen)
                .expect("expected update");
            assert_eq!(last_gen, g);
            assert_eq!(read.band_count, 1);
            assert!((read.bands[0].gain_db - gain).abs() < 1.0e-9);
            assert!(reader.try_read_if_newer(&mut last_gen).is_none());
        }
    }

    #[test]
    fn corrupted_crc_payload_is_rejected_cleanly() {
        let (writer, reader) = ShmChannel::in_memory("{crc-test}");
        let cfg = DspConfig::from_bands(&[ParametricBand::peak(2_000.0, 4.0, 1.0)]);
        writer.publish(&cfg);
        let idx = (writer.block().active_slot.load(Ordering::Acquire) & 1) as usize;
        unsafe {
            (*writer.block().slots[idx].payload.get()).crc32 ^= 0xDEAD_BEEF;
        }
        let mut last_gen = 0u64;
        assert!(reader.try_read_if_newer(&mut last_gen).is_none());
    }

    #[cfg(windows)]
    #[test]
    fn win32_local_file_mapping_round_trips_between_writer_and_reader() {
        let id = format!("unit-local-{}", std::process::id());
        let writer = ShmChannel::create_or_open(&id, ShmScope::Local).expect("create local shm");
        let reader = ShmChannel::open_existing(&id, ShmScope::Local).expect("open local shm");

        let cfg = DspConfig {
            preamp_db: -3.5,
            spatial_mode: SpatialMode::HrtfCompetitive,
            spatial_strength: 0.65,
            ..DspConfig::from_bands(&[
                ParametricBand::low_shelf(90.0, -4.0, 0.71),
                ParametricBand::peak(2_600.0, 3.0, 1.2),
            ])
        };
        let generation = writer.publish(&cfg);
        let mut seen = 0u64;
        let got = reader
            .try_read_if_newer(&mut seen)
            .expect("read from win32 shm");
        assert_eq!(seen, generation);
        assert_eq!(got.band_count, 2);
        assert_eq!(got.spatial_mode, SpatialMode::HrtfCompetitive);
        assert!((got.bands[1].gain_db - 3.0).abs() < 1.0e-9);

        reader
            .block()
            .record_rt_telemetry(480, generation, -1.25, 0.4, 48_000, 2);
        let tel = writer.block().read_telemetry();
        assert_eq!(tel.frames_processed, 480);
        assert_eq!(tel.active_generation, generation);
        assert!((tel.peak_dbfs - (-1.25)).abs() < 0.02);
        assert!((tel.limiter_gr_db - 0.4).abs() < 0.02);
    }
}
