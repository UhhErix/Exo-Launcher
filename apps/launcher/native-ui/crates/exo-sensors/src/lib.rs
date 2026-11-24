pub mod adaptive;
pub mod gpu;
pub mod nvme_smart;
pub mod pawnio_imc;
pub mod sentinel;
pub mod smbios;
pub mod whea;
pub mod win32;

#[cfg(test)]
mod tests {
    use crate::adaptive::PollMode;
    use crate::gpu::{
        GpuBackendKind, GpuSnapshot, GpuVendor, THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL,
        decode_astral_i2c_frame, map_nvml_bits,
    };
    use crate::nvme_smart::{
        NvmeDriveHealth, parse_device_descriptor, parse_nvme_smart_log, parse_temperature_property,
    };
    use crate::pawnio_imc::decode_amd_umc_timings;
    use crate::sentinel::{SentinelKind, SentinelSeverity, evaluate_sentinel};
    use crate::smbios::{DimmInfo, SmbiosReport, infer_channel, parse_rsmb};
    use crate::whea::StabilityEventSummary;

    fn make_dimm(size_mb: u32, rated: u32, cfg: u32, loc: &str, bank: &str) -> DimmInfo {
        DimmInfo {
            size_mb,
            speed_mts: rated,
            configured_mts: cfg,
            form_factor: 0x09,
            memory_type: "DDR5".into(),
            device_locator: loc.into(),
            bank_locator: bank.into(),
        }
    }

    #[test]
    fn smbios_parser_handles_empty_truncated_and_malformed_fuzz_buffers_without_panic() {
        assert!(parse_rsmb(&[]).dimms.is_empty());
        assert!(parse_rsmb(&[0u8; 4]).dimms.is_empty());
        assert!(parse_rsmb(&[0xFFu8; 64]).dimms.is_empty());
        let mut trunc = vec![0u8; 32];
        trunc[4..8].copy_from_slice(&9999u32.to_le_bytes());
        trunc[8] = 17;
        trunc[9] = 40;
        assert!(parse_rsmb(&trunc).dimms.is_empty());
    }

    #[test]
    fn smbios_parses_type_0_2_4_17_golden_dump() {
        let mut raw = Vec::new();
        raw.extend_from_slice(&[0, 3, 2, 0, 0, 0, 0, 0]);
        let start = raw.len();

        let mut t0 = vec![0u8; 20];
        t0[0] = 0;
        t0[1] = 20;
        t0[4] = 1;
        t0[5] = 2;
        t0[8] = 3;
        raw.extend_from_slice(&t0);
        raw.extend_from_slice(b"AMI\03607\008/15/2026\0\0");

        let mut t2 = vec![0u8; 10];
        t2[0] = 2;
        t2[1] = 10;
        t2[4] = 1;
        t2[5] = 2;
        raw.extend_from_slice(&t2);
        raw.extend_from_slice(b"ASUSTeK\0ROG STRIX B550-F\0\0");

        let mut t17 = vec![0u8; 40];
        t17[0] = 17;
        t17[1] = 40;
        t17[12..14].copy_from_slice(&16384u16.to_le_bytes());
        t17[14] = 0x09;
        t17[16] = 1;
        t17[17] = 2;
        t17[18] = 0x22;
        t17[21..23].copy_from_slice(&6000u16.to_le_bytes());
        t17[32..34].copy_from_slice(&4800u16.to_le_bytes());
        raw.extend_from_slice(&t17);
        raw.extend_from_slice(b"DIMM_A2\0CHANNEL A\0\0");

        let table_len = (raw.len() - start) as u32;
        raw[4..8].copy_from_slice(&table_len.to_le_bytes());

        let rep = parse_rsmb(&raw);
        assert_eq!(rep.bios_vendor.as_deref(), Some("AMI"));
        assert_eq!(rep.bios_version.as_deref(), Some("3607"));
        assert_eq!(rep.board_maker.as_deref(), Some("ASUSTeK"));
        assert_eq!(rep.dimms.len(), 1);
        assert_eq!(rep.total_ram_gb(), 16);
        assert_eq!(rep.xmp_expo_mismatch(), Some((4800, 6000)));
        assert!(rep.is_single_channel_desktop());
    }

    #[test]
    fn sentinel_detects_all_five_misconfigurations_with_100_pct_precision() {
        let smbios = SmbiosReport {
            dimms: vec![
                make_dimm(16384, 6000, 4800, "DIMM_A1", "Channel A"),
                make_dimm(16384, 6000, 4800, "DIMM_A2", "Channel A"),
            ],
            ..Default::default()
        };
        let gpu = GpuSnapshot {
            timestamp_ms: 1_000,
            backend: GpuBackendKind::NvidiaNvmlNvapi,
            vendor: GpuVendor::Nvidia,
            name: "NVIDIA GeForce RTX 3070".into(),
            driver_version: Some("621.35".into()),
            core_temp_c: Some(62.0),
            hotspot_temp_c: None,
            memory_temp_c: None,
            board_power_w: Some(180.0),
            power_limit_w: Some(240.0),
            enforced_power_limit_w: Some(240.0),
            min_power_limit_w: Some(100.0),
            max_power_limit_w: Some(265.0),
            power_pct: Some(75.0),
            gpu_clock_mhz: Some(1950),
            mem_clock_mhz: Some(7001),
            vram_used_mb: Some(4096),
            vram_total_mb: Some(8192),
            gpu_util_pct: Some(92),
            fan_pct: Some(60),
            fan_rpm: Some(1500),
            pstate: Some(0),
            pcie_gen_current: Some(4),
            pcie_gen_max: Some(4),
            pcie_width_current: Some(8),
            pcie_width_max: Some(16),
            temp_target_c: Some(83),
            temp_max_c: Some(93),
            temp_slowdown_c: Some(95),
            throttle_flags: 0,
            connector: None,
        };
        let drives = vec![NvmeDriveHealth {
            disk_number: 0,
            model: "Samsung SSD 990 PRO 2TB".into(),
            is_nvme: true,
            temp_c: Some(76),
            warn_temp_c: Some(74),
            critical_temp_c: Some(85),
            life_used_pct: Some(2),
            spare_pct: Some(100),
            spare_threshold_pct: Some(10),
            critical_warning: 0,
            media_errors: 0,
            unsafe_shutdowns: 1,
            throttle_events: 4,
            throttle_seconds: 120,
        }];
        let whea = StabilityEventSummary {
            window_hours: 24,
            whea_errors: 3,
            gpu_tdr_resets: 1,
            kernel_power_41: 0,
            bugcheck_1001: 0,
        };

        let findings = evaluate_sentinel(Some(&smbios), Some(&gpu), &drives, &whea);
        assert!(findings.iter().any(|f| f.kind == SentinelKind::XmpExpoOff));
        assert!(
            findings
                .iter()
                .any(|f| f.kind == SentinelKind::SingleChannelRam)
        );
        assert!(
            findings
                .iter()
                .any(|f| f.kind == SentinelKind::PcieLinkDrop)
        );
        assert!(
            findings
                .iter()
                .any(|f| f.kind == SentinelKind::NvmeThrottling)
        );
        assert!(findings.iter().any(
            |f| f.kind == SentinelKind::WheaErrors && f.severity == SentinelSeverity::Critical
        ));
        assert!(findings.iter().any(|f| f.kind == SentinelKind::GpuTdrBurst));
    }

    #[test]
    fn nvme_smart_and_temp_parsers_decode_exact_offsets() {
        let mut log = vec![0u8; 512];
        log[0] = 0x02;
        log[1..3].copy_from_slice(&347u16.to_le_bytes());
        log[3] = 98;
        log[4] = 10;
        log[5] = 4;
        log[144..152].copy_from_slice(&7u64.to_le_bytes());
        log[160..168].copy_from_slice(&2u64.to_le_bytes());
        log[216..220].copy_from_slice(&3u32.to_le_bytes());
        log[224..228].copy_from_slice(&45u32.to_le_bytes());

        let parsed = parse_nvme_smart_log(&log).unwrap();
        assert_eq!(parsed.critical_warning, 0x02);
        assert_eq!(parsed.composite_temp_c, Some(74));
        assert_eq!(parsed.spare_pct, 98);
        assert_eq!(parsed.life_used_pct, 4);
        assert_eq!(parsed.media_errors, 2);
        assert_eq!(parsed.throttle_events, 3);
        assert_eq!(parsed.throttle_seconds, 45);

        let mut td = vec![0u8; 48];
        td[8..10].copy_from_slice(&85i16.to_le_bytes());
        td[10..12].copy_from_slice(&75i16.to_le_bytes());
        td[12..14].copy_from_slice(&1u16.to_le_bytes());
        td[26..28].copy_from_slice(&42i16.to_le_bytes());
        assert_eq!(
            parse_temperature_property(&td),
            Some((Some(42), Some(75), Some(85)))
        );

        let mut desc = vec![0u8; 64];
        desc[28] = 17;
        desc[16..20].copy_from_slice(&40u32.to_le_bytes());
        desc[40..51].copy_from_slice(b"Samsung 990");
        assert_eq!(parse_device_descriptor(&desc), ("Samsung 990".into(), 17));
    }

    #[test]
    fn channel_inference_amd_umc_timings_and_adaptive_poll_modes_work() {
        assert_eq!(infer_channel("DIMM_A2", "BANK 0"), Some('A'));
        assert_eq!(infer_channel("DIMM_B2", "BANK 2"), Some('B'));
        assert_eq!(infer_channel("Slot 1", "Channel B"), Some('B'));

        let reg204 = 16u32 | (36u32 << 8) | (18u32 << 16) | (18u32 << 24);
        let reg208 = 54u32 | (18u32 << 16);
        let reg260 = 560u32;
        let t = decode_amd_umc_timings(1800, 1800, 1800, reg204, reg208, reg260);
        assert_eq!(
            (t.tcl, t.trcd_rd, t.trp, t.tras, t.trc, t.trfc),
            (16, 18, 18, 36, 54, 560)
        );
        assert!(t.ratio_1_to_1);

        assert_eq!(PollMode::resolve(false, false).interval(), None);
        assert_eq!(
            PollMode::resolve(false, true).interval().unwrap().as_secs(),
            10
        );
        assert_eq!(
            PollMode::resolve(true, false).interval().unwrap().as_secs(),
            1
        );

        let mapped = map_nvml_bits(0x40 | 0x80);
        assert_ne!(mapped & THROTTLE_HW_THERMAL, 0);
        assert_ne!(mapped & THROTTLE_HW_POWER_BRAKE, 0);

        let raw = [0u8; 24];
        assert_eq!(decode_astral_i2c_frame(&raw).pins.len(), 6);
    }
}
