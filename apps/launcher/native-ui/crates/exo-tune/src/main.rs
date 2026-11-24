use exo_tune::{
    ConfigFormat, PcFacts, compile_pack, load_pack, parse_and_validate, patch_config,
    resolve_changes, rollback_config,
};
use std::fs;
use std::path::Path;
use std::time::Instant;

fn main() {
    let catalog_path = Path::new("recipes/catalog.exotune");
    let src = fs::read_to_string(catalog_path).expect("read recipes/catalog.exotune");

    let iters = 2000u32;
    let t = Instant::now();
    let mut recipes = Vec::new();
    for _ in 0..iters {
        recipes = parse_and_validate(&src).expect("parse_and_validate");
    }
    let parse_us = t.elapsed().as_secs_f64() * 1e6 / iters as f64;

    let t = Instant::now();
    let mut pack = Vec::new();
    for _ in 0..iters {
        pack = compile_pack(&recipes);
    }
    let compile_us = t.elapsed().as_secs_f64() * 1e6 / iters as f64;

    let t = Instant::now();
    for _ in 0..iters {
        let loaded = load_pack(&pack).expect("load_pack");
        std::hint::black_box(loaded.len());
    }
    let load_us = t.elapsed().as_secs_f64() * 1e6 / iters as f64;

    let pc = PcFacts {
        vendor: "nvidia".into(),
        gpu_strong: true,
        gpu_rtx: true,
        vram_gb: 8,
        hdr_nits: 600,
        fullscreen: false,
    };

    let eval_iters = 10_000u32;
    let t = Instant::now();
    let mut total_changes = 0usize;
    for _ in 0..eval_iters {
        for r in &recipes {
            total_changes += resolve_changes(r, &pc).len();
        }
    }
    let eval_us = t.elapsed().as_secs_f64() * 1e6 / eval_iters as f64;

    let sample_xml = "<settings>\r\n  <textureQuality>kSettingLevel_Medium</textureQuality>\r\n  <anisotropicFiltering value=\"2\" />\r\n  <shadowQuality>kSettingLevel_Low</shadowQuality>\r\n  <POMQuality>kSettingLevel_Low</POMQuality>\r\n  <lightingQuality>kSettingLevel_Low</lightingQuality>\r\n  <volumetricsRaymarchQuality>kSettingLevel_Low</volumetricsRaymarchQuality>\r\n  <reflectionQuality>kSettingLevel_High</reflectionQuality>\r\n  <windowed value=\"1\" />\r\n</settings>";
    let rdr2_changes = resolve_changes(&recipes[0], &pc);
    let patch_iters = 20_000u32;
    let t = Instant::now();
    for _ in 0..patch_iters {
        let (out, log) = patch_config(ConfigFormat::Xml, sample_xml, &rdr2_changes);
        std::hint::black_box((out.len(), log.len()));
    }
    let patch_us = t.elapsed().as_secs_f64() * 1e6 / patch_iters as f64;

    let (patched, log) = patch_config(ConfigFormat::Xml, sample_xml, &rdr2_changes);
    assert_eq!(rollback_config(ConfigFormat::Xml, &patched, &log), sample_xml);

    println!(
        "EXO-TUNE DSL: games={} | src={} B | compiled_pack={} B ({:.1}% of src)",
        recipes.len(),
        src.len(),
        pack.len(),
        100.0 * pack.len() as f64 / src.len() as f64
    );
    println!(
        "parse+safety_check={:.1} us | compile_pack={:.1} us | load_pack={:.1} us | eval_all_12_games={:.2} us (changes={}) | xml_patch={:.2} us",
        parse_us,
        compile_us,
        load_us,
        eval_us,
        total_changes / eval_iters as usize,
        patch_us
    );
}
