#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetSystemFirmwareTable(
        firmware_table_provider_signature: u32,
        firmware_table_id: u32,
        p_firmware_table_buffer: *mut u8,
        buffer_size: u32,
    ) -> u32;
}

const RSMB_SIG: u32 = 0x5253_4D42;
const FORM_FACTOR_DIMM: u8 = 0x09;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DimmInfo {
    pub size_mb: u32,
    pub speed_mts: u32,
    pub configured_mts: u32,
    pub form_factor: u8,
    pub memory_type: String,
    pub device_locator: String,
    pub bank_locator: String,
}

impl DimmInfo {
    pub fn is_desktop_dimm(&self) -> bool {
        self.form_factor == FORM_FACTOR_DIMM
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SmbiosReport {
    pub bios_vendor: Option<String>,
    pub bios_version: Option<String>,
    pub bios_date: Option<String>,
    pub board_maker: Option<String>,
    pub board_product: Option<String>,
    pub cpu_model: Option<String>,
    pub cpu_cores: Option<u32>,
    pub cpu_threads: Option<u32>,
    pub cpu_max_mhz: Option<u32>,
    pub dimms: Vec<DimmInfo>,
}

impl SmbiosReport {
    pub fn total_ram_gb(&self) -> u32 {
        let mb: u64 = self.dimms.iter().map(|d| d.size_mb as u64).sum();
        ((mb + 512) / 1024) as u32
    }

    pub fn is_single_channel_desktop(&self) -> bool {
        let desktop: Vec<&DimmInfo> = self
            .dimms
            .iter()
            .filter(|d| d.size_mb > 0 && d.is_desktop_dimm())
            .collect();
        if desktop.is_empty() {
            return false;
        }
        if desktop.len() == 1 {
            return true;
        }
        let mut channels = std::collections::HashSet::new();
        for d in &desktop {
            if let Some(ch) = infer_channel(&d.device_locator, &d.bank_locator) {
                channels.insert(ch);
            }
        }
        channels.len() == 1
    }

    pub fn xmp_expo_mismatch(&self) -> Option<(u32, u32)> {
        let mut rated = 0u32;
        let mut configured = u32::MAX;
        for d in self
            .dimms
            .iter()
            .filter(|d| d.size_mb > 0 && d.is_desktop_dimm())
        {
            if d.speed_mts > 0 && d.configured_mts > 0 {
                rated = rated.max(d.speed_mts);
                configured = configured.min(d.configured_mts);
            }
        }
        if rated > 0 && configured < u32::MAX && configured + 100 < rated {
            Some((configured, rated))
        } else {
            None
        }
    }
}

pub fn read_live_smbios() -> Option<SmbiosReport> {
    let size = unsafe { GetSystemFirmwareTable(RSMB_SIG, 0, std::ptr::null_mut(), 0) };
    if size == 0 || size > 4 * 1024 * 1024 {
        return None;
    }
    let mut buf = vec![0u8; size as usize];
    let got = unsafe { GetSystemFirmwareTable(RSMB_SIG, 0, buf.as_mut_ptr(), size) };
    if got != size {
        return None;
    }
    Some(parse_rsmb(&buf))
}

pub fn parse_rsmb(raw: &[u8]) -> SmbiosReport {
    let mut report = SmbiosReport::default();
    if raw.len() < 8 {
        return report;
    }
    let table_len = u32::from_le_bytes([raw[4], raw[5], raw[6], raw[7]]) as usize;
    let end = (8 + table_len).min(raw.len());
    let mut i = 8usize;

    while i + 4 <= end {
        let kind = raw[i];
        let len = raw[i + 1] as usize;
        if len < 4 || i + len > end {
            break;
        }
        let body = &raw[i..i + len];
        let mut s_idx = i + len;
        let mut strings: Vec<String> = Vec::new();
        while s_idx < end {
            if raw[s_idx] == 0 {
                s_idx += 1;
                if strings.is_empty() && s_idx < end && raw[s_idx] == 0 {
                    s_idx += 1;
                }
                break;
            }
            let start = s_idx;
            while s_idx < end && raw[s_idx] != 0 {
                s_idx += 1;
            }
            strings.push(
                String::from_utf8_lossy(&raw[start..s_idx])
                    .trim()
                    .to_string(),
            );
            if s_idx < end {
                s_idx += 1;
            }
            if s_idx < end && raw[s_idx] == 0 {
                s_idx += 1;
                break;
            }
        }
        if s_idx <= i {
            break;
        }
        i = s_idx;

        match kind {
            0 if body.len() >= 9 => {
                report.bios_vendor = smbios_str(&strings, body[4]);
                report.bios_version = smbios_str(&strings, body[5]);
                report.bios_date = smbios_str(&strings, body[8]);
            }
            2 if body.len() >= 6 => {
                report.board_maker = smbios_str(&strings, body[4]);
                report.board_product = smbios_str(&strings, body[5]);
            }
            4 if body.len() >= 22 => {
                if report.cpu_model.is_none() {
                    report.cpu_model = smbios_str(&strings, body[16]);
                }
                if body.len() >= 22 {
                    let max_mhz = u16::from_le_bytes([body[20], body[21]]) as u32;
                    if max_mhz > 0 {
                        report.cpu_max_mhz = Some(max_mhz);
                    }
                }
                if body.len() >= 38 {
                    let cores = body[35] as u32;
                    let threads = body[37] as u32;
                    if cores > 0 {
                        report.cpu_cores = Some(cores);
                    }
                    if threads > 0 {
                        report.cpu_threads = Some(threads);
                    }
                }
            }
            17 if body.len() >= 23 => {
                let raw_size = u16::from_le_bytes([body[12], body[13]]);
                let size_mb = if raw_size == 0 || raw_size == 0xFFFF {
                    0
                } else if raw_size == 0x7FFF && body.len() >= 32 {
                    u32::from_le_bytes([body[28], body[29], body[30], body[31]]) & 0x7FFF_FFFF
                } else if raw_size & 0x8000 != 0 {
                    ((raw_size & 0x7FFF) as u32) / 1024
                } else {
                    raw_size as u32
                };
                if size_mb == 0 {
                    continue;
                }
                let form_factor = body[14];
                let device_locator = smbios_str(&strings, body[16]).unwrap_or_default();
                let bank_locator = smbios_str(&strings, body[17]).unwrap_or_default();
                let memory_type = decode_memory_type(body[18]).to_string();
                let mut speed_mts = u16::from_le_bytes([body[21], body[22]]) as u32;
                let mut configured_mts = if body.len() >= 34 {
                    u16::from_le_bytes([body[32], body[33]]) as u32
                } else {
                    0
                };
                if speed_mts == 0xFFFF && body.len() >= 88 {
                    speed_mts = u32::from_le_bytes([body[84], body[85], body[86], body[87]]);
                }
                if configured_mts == 0xFFFF && body.len() >= 92 {
                    configured_mts = u32::from_le_bytes([body[88], body[89], body[90], body[91]]);
                }
                report.dimms.push(DimmInfo {
                    size_mb,
                    speed_mts,
                    configured_mts,
                    form_factor,
                    memory_type,
                    device_locator,
                    bank_locator,
                });
            }
            127 => break,
            _ => {}
        }
    }
    report
}

fn smbios_str(strings: &[String], idx: u8) -> Option<String> {
    if idx == 0 {
        return None;
    }
    strings
        .get((idx - 1) as usize)
        .filter(|s| !s.is_empty() && !s.eq_ignore_ascii_case("to be filled by o.e.m."))
        .cloned()
}

fn decode_memory_type(code: u8) -> &'static str {
    match code {
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => "RAM",
    }
}

pub fn infer_channel(device_locator: &str, bank_locator: &str) -> Option<char> {
    for src in [device_locator, bank_locator] {
        let up = src.to_ascii_uppercase();
        if let Some(pos) = up.find("CHANNEL") {
            let after = up[pos + 7..].trim_start_matches([' ', '_', '-', ':']);
            if let Some(ch) = after.chars().next()
                && matches!(ch, 'A'..='D')
            {
                return Some(ch);
            }
        }
        if let Some(pos) = up.find("DIMM") {
            let after = up[pos + 4..].trim_start_matches([' ', '_', '-', ':']);
            if let Some(ch) = after.chars().next()
                && matches!(ch, 'A'..='D')
            {
                return Some(ch);
            }
        }
        for tok in up.split(|c: char| !c.is_ascii_alphanumeric()) {
            let b = tok.as_bytes();
            if b.len() == 2 && matches!(b[0], b'A'..=b'D') && matches!(b[1], b'0'..=b'3') {
                return Some(b[0] as char);
            }
        }
    }
    None
}
