use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

struct PanicOnRtAlloc;

static RT_SECTION_ACTIVE: AtomicBool = AtomicBool::new(false);
static RT_ALLOC_COUNT: AtomicUsize = AtomicUsize::new(0);

unsafe impl GlobalAlloc for PanicOnRtAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        if RT_SECTION_ACTIVE.load(Ordering::Acquire) {
            RT_ALLOC_COUNT.fetch_add(1, Ordering::AcqRel);
            panic!(
                "heap allocation forbidden on RT audio thread (size={} align={})",
                layout.size(),
                layout.align()
            );
        }
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        if RT_SECTION_ACTIVE.load(Ordering::Acquire) {
            RT_ALLOC_COUNT.fetch_add(1, Ordering::AcqRel);
            panic!(
                "heap deallocation forbidden on RT audio thread (size={} align={})",
                layout.size(),
                layout.align()
            );
        }
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: PanicOnRtAlloc = PanicOnRtAlloc;

#[cfg(windows)]
#[test]
fn sixty_seconds_of_com_apo_process_performs_zero_heap_allocations_and_zero_clicks() {
    use core::f64::consts::PI;
    use core::mem::ManuallyDrop;
    use exo_apo::apo_com::{CLSID_EXO_AUDIO_APO_EFX, ExoAudioApo, TestAudioMediaType};
    use exo_apo::dsp::{DspConfig, HARD_LIMITER_CEILING_LINEAR, ParametricBand, SpatialMode};
    use exo_apo::shm_ipc::ShmChannel;
    use windows::Win32::Media::Audio::Apo::{
        APO_CONNECTION_BUFFER_TYPE_EXTERNAL, APO_CONNECTION_DESCRIPTOR, APO_CONNECTION_PROPERTY,
        BUFFER_VALID, IAudioProcessingObject, IAudioProcessingObjectConfiguration,
        IAudioProcessingObjectRT,
    };
    use windows_core::Interface;

    let (writer, reader) = ShmChannel::in_memory("{zero-alloc-60s-test}");
    let apo: IAudioProcessingObject =
        ExoAudioApo::new_with_shm(CLSID_EXO_AUDIO_APO_EFX, reader).into();
    let cfg_iface: IAudioProcessingObjectConfiguration = apo.cast().expect("config iface");
    let rt_iface: IAudioProcessingObjectRT = apo.cast().expect("rt iface");

    let fs = 48_000.0_f64;
    let frames_per_block = 480usize;
    let blocks_60s = 6_000usize;
    let mut audio = vec![0.0_f32; frames_per_block * 2];

    let fmt = TestAudioMediaType::ieee_float(fs as f32, 2);
    let desc = APO_CONNECTION_DESCRIPTOR {
        Type: APO_CONNECTION_BUFFER_TYPE_EXTERNAL,
        pBuffer: audio.as_mut_ptr() as usize,
        u32MaxFrameCount: frames_per_block as u32,
        pFormat: ManuallyDrop::new(Some(fmt)),
        u32Signature: 0,
    };
    let descs = [&desc as *const APO_CONNECTION_DESCRIPTOR];
    unsafe {
        cfg_iface
            .LockForProcess(&descs, &descs)
            .expect("LockForProcess");
    }

    let mut precomputed_cfgs = Vec::with_capacity(100);
    for i in 0..100usize {
        let gain = if i % 2 == 0 { -12.0 } else { 12.0 };
        precomputed_cfgs.push(DspConfig {
            spatial_mode: if i % 3 == 0 {
                SpatialMode::HrtfCompetitive
            } else {
                SpatialMode::Off
            },
            spatial_strength: 0.5,
            ..DspConfig::from_bands(&[
                ParametricBand::low_shelf(105.0, -4.0, 0.71),
                ParametricBand::peak(1_000.0, gain, 1.0),
                ParametricBand::peak(2_600.0, 2.5, 1.2),
                ParametricBand::high_shelf(10_000.0, -1.5, 0.71),
            ])
        });
    }

    let mut phase = 0.0_f64;
    let phase_inc = 2.0 * PI * 440.0 / fs;
    let mut prev_l = 0.0_f32;
    let mut max_delta = 0.0_f32;
    let mut peak = 0.0_f32;

    let in_prop = APO_CONNECTION_PROPERTY {
        pBuffer: audio.as_mut_ptr() as usize,
        u32ValidFrameCount: frames_per_block as u32,
        u32BufferFlags: BUFFER_VALID,
        u32Signature: 0,
    };
    let mut out_prop = in_prop;
    let in_ptrs = [&in_prop as *const APO_CONNECTION_PROPERTY];
    let mut out_ptrs = [&mut out_prop as *mut APO_CONNECTION_PROPERTY];

    RT_ALLOC_COUNT.store(0, Ordering::Release);
    RT_SECTION_ACTIVE.store(true, Ordering::Release);

    for block in 0..blocks_60s {
        if block % 60 == 0 {
            let cfg_idx = (block / 60) % precomputed_cfgs.len();
            writer.publish(&precomputed_cfgs[cfg_idx]);
        }
        for f in 0..frames_per_block {
            let s = (0.25 * phase.sin()) as f32;
            phase += phase_inc;
            audio[f * 2] = s;
            audio[f * 2 + 1] = s;
        }
        unsafe {
            rt_iface.APOProcess(1, in_ptrs.as_ptr(), 1, out_ptrs.as_mut_ptr());
        }
        for f in 0..frames_per_block {
            let cur = audio[f * 2];
            peak = peak.max(cur.abs());
            if block > 0 || f > 0 {
                let d = (cur - prev_l).abs();
                if d > max_delta {
                    max_delta = d;
                }
            }
            prev_l = cur;
        }
    }

    RT_SECTION_ACTIVE.store(false, Ordering::Release);
    unsafe {
        cfg_iface.UnlockForProcess().expect("UnlockForProcess");
    }

    assert_eq!(RT_ALLOC_COUNT.load(Ordering::Acquire), 0);
    assert!(
        (peak as f64) <= HARD_LIMITER_CEILING_LINEAR + 1.0e-5,
        "peak {peak} exceeded ceiling"
    );
    assert!(
        max_delta < 0.09,
        "click detected during 60s RT run with 100 live SHM swaps: max_delta={max_delta}"
    );
    let tel = writer.block().read_telemetry();
    assert_eq!(tel.frames_processed, (blocks_60s * frames_per_block) as u64);
}
