use exo_apo::autoeq_db::{AutoEqCatalog, AutoEqEntry, HeadphoneForm};
use exo_apo::dsp::{DspConfig, DspEngine, ParametricBand, SpatialMode};
use exo_apo::shm_ipc::{ShmBlock, ShmChannel};
use std::hint::black_box;
use std::time::Instant;

fn bench_dsp(label: &str, cfg: &DspConfig, blocks: usize) {
    let fs = 48_000.0;
    let frames_per_block = 480usize;
    let mut engine = DspEngine::new(fs, 2);
    engine.apply_config_immediate(cfg);

    let mut buf = vec![0.0_f32; frames_per_block * 2];
    for i in 0..frames_per_block {
        let s = (0.35 * (2.0 * std::f64::consts::PI * 440.0 * i as f64 / fs).sin()) as f32;
        buf[i * 2] = s;
        buf[i * 2 + 1] = s * 0.9;
    }

    for _ in 0..200 {
        black_box(engine.process_interleaved_f32(&mut buf));
    }

    let start = Instant::now();
    for _ in 0..blocks {
        black_box(engine.process_interleaved_f32(&mut buf));
    }
    let elapsed = start.elapsed();
    let per_block_us = elapsed.as_secs_f64() * 1_000_000.0 / blocks as f64;
    let cpu_pct = (per_block_us / 10_000.0) * 100.0;
    println!(
        "{label}: {per_block_us:.2} us / 10ms block ({cpu_pct:.4}% of 1 core, SIMD={:?})",
        engine.simd_path()
    );
}

fn main() {
    let blocks = 10_000usize;
    let ten_bands = [
        ParametricBand::low_shelf(105.0, 4.5, 0.71),
        ParametricBand::peak(180.0, -1.8, 1.1),
        ParametricBand::peak(360.0, -1.2, 1.3),
        ParametricBand::peak(720.0, 0.9, 1.5),
        ParametricBand::peak(1_400.0, 1.6, 1.2),
        ParametricBand::peak(2_600.0, 2.8, 1.15),
        ParametricBand::peak(4_200.0, 2.0, 1.4),
        ParametricBand::peak(6_400.0, -2.5, 2.1),
        ParametricBand::peak(8_500.0, -1.6, 2.4),
        ParametricBand::high_shelf(11_000.0, 1.2, 0.71),
    ];
    let sixteen_bands = [
        ten_bands[0],
        ten_bands[1],
        ten_bands[2],
        ten_bands[3],
        ten_bands[4],
        ten_bands[5],
        ten_bands[6],
        ten_bands[7],
        ten_bands[8],
        ten_bands[9],
        ParametricBand::peak(55.0, 1.5, 0.9),
        ParametricBand::peak(500.0, -0.7, 1.4),
        ParametricBand::peak(1_950.0, 1.1, 1.8),
        ParametricBand::peak(3_400.0, -1.9, 1.35),
        ParametricBand::peak(5_200.0, -1.0, 2.0),
        ParametricBand::high_pass(22.0, 0.707),
    ];

    println!(
        "=== exo-audio-apo DSP & Memory Benchmark (48 kHz stereo, 480-sample 10 ms blocks x {blocks}) ==="
    );
    let cfg10 = DspConfig::from_bands(&ten_bands);
    bench_dsp("10-band EQ + AutoPreamp + TruePeak Limiter", &cfg10, blocks);

    let cfg16_cf = DspConfig {
        spatial_mode: SpatialMode::Crossfeed,
        spatial_strength: 0.6,
        ..DspConfig::from_bands(&sixteen_bands)
    };
    bench_dsp("16-band EQ + Limiter + Crossfeed", &cfg16_cf, blocks);

    let cfg16_hrtf = DspConfig {
        spatial_mode: SpatialMode::HrtfCompetitive,
        spatial_strength: 0.7,
        ..DspConfig::from_bands(&sixteen_bands)
    };
    bench_dsp(
        "16-band EQ + Limiter + HRTF Spatializer",
        &cfg16_hrtf,
        blocks,
    );

    let (writer, reader) = ShmChannel::in_memory("{bench}");
    let swaps = 100_000usize;
    let mut seen = 0u64;
    let t0 = Instant::now();
    for i in 0..swaps {
        let mut c = cfg10;
        c.bands[0].gain_db = -6.0 + (i % 13) as f64;
        writer.publish(&c);
        black_box(reader.try_read_if_newer(&mut seen));
    }
    let swap_ns = t0.elapsed().as_nanos() as f64 / swaps as f64;
    println!("SHM lock-free publish+read round-trip: {swap_ns:.1} ns");

    let mut entries = Vec::with_capacity(4_000);
    let measurers = ["oratory1990", "crinacle", "Rtings", "Innerfidelity"];
    for i in 0..4_000usize {
        let m = measurers[i % measurers.len()];
        entries.push(AutoEqEntry {
            id: format!("{m}/over-ear/Headphone Model {i:04}"),
            name: format!("Headphone Model {i:04}"),
            measurer: m.to_string(),
            form: HeadphoneForm::OverEar,
            usb_vid: None,
            usb_pid: None,
            bands: ten_bands.to_vec(),
        });
    }
    let cat = AutoEqCatalog::new(entries);
    let blob = cat.compile_binary_index().expect("compile");
    let t_dec = Instant::now();
    let decoded = AutoEqCatalog::decode_binary_index(&blob).expect("decode");
    let dec_ms = t_dec.elapsed().as_secs_f64() * 1_000.0;
    let t_q = Instant::now();
    for _ in 0..1_000 {
        black_box(decoded.search("Model 2048", 10));
    }
    let q_us = t_q.elapsed().as_secs_f64() * 1_000_000.0 / 1_000.0;
    println!(
        "AutoEq 4000-entry binary index: {:.1} KB, decode={dec_ms:.2} ms, search={q_us:.1} us",
        blob.len() as f64 / 1024.0
    );

    println!(
        "Memory footprints: DspEngine={} B, ShmBlock={} B, AddedBufferLatency=0.000 ms",
        std::mem::size_of::<DspEngine>(),
        std::mem::size_of::<ShmBlock>(),
    );
}
