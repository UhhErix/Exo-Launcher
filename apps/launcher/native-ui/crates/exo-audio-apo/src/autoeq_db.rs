use crate::dsp::{DspConfig, FilterKind, MAX_BANDS, ParametricBand, SpatialMode};
use exo_rpc::anticheat::AntiCheatClass;
use flate2::Compression;
use flate2::read::DeflateDecoder;
use flate2::write::DeflateEncoder;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::io::{Read, Write};

pub const BINARY_INDEX_MAGIC: [u8; 4] = *b"EXOE";
pub const BINARY_INDEX_VERSION: u16 = 1;
pub const TARGET_MAX_INDEX_BYTES: usize = 400 * 1024;

#[repr(u8)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum HeadphoneForm {
    #[default]
    OverEar = 0,
    InEar = 1,
    Earbud = 2,
}

impl HeadphoneForm {
    pub const fn from_u8(v: u8) -> Self {
        match v {
            1 => Self::InEar,
            2 => Self::Earbud,
            _ => Self::OverEar,
        }
    }

    pub fn from_rig_str(rig: &str) -> Self {
        let lower = rig.to_ascii_lowercase();
        if lower.contains("in-ear") {
            Self::InEar
        } else if lower.contains("earbud") {
            Self::Earbud
        } else {
            Self::OverEar
        }
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::OverEar => "over-ear",
            Self::InEar => "in-ear",
            Self::Earbud => "earbud",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoEqEntry {
    pub id: String,
    pub name: String,
    pub measurer: String,
    pub form: HeadphoneForm,
    pub usb_vid: Option<u16>,
    pub usb_pid: Option<u16>,
    pub bands: Vec<ParametricBand>,
}

impl AutoEqEntry {
    pub fn attribution_line(&self) -> String {
        format!(
            "{}: correction from AutoEq (MIT), measured by {}",
            self.name, self.measurer
        )
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KnownUsbDevice {
    pub vid: u16,
    pub pid: u16,
    pub brand: &'static str,
    pub model: &'static str,
    pub autoeq_id: &'static str,
    pub measurer: &'static str,
    pub form: HeadphoneForm,
}

pub const KNOWN_USB_HEADSETS: &[KnownUsbDevice] = &[
    KnownUsbDevice {
        vid: 0x0951,
        pid: 0x16A4,
        brand: "HyperX",
        model: "HyperX Cloud II",
        autoeq_id: "oratory1990/over-ear/HyperX Cloud II",
        measurer: "oratory1990",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x03F0,
        pid: 0x098D,
        brand: "HyperX",
        model: "HyperX Cloud Alpha Wireless",
        autoeq_id: "Rtings/over-ear/HyperX Cloud Alpha Wireless",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x03F0,
        pid: 0x0596,
        brand: "HyperX",
        model: "HyperX Cloud III",
        autoeq_id: "Rtings/over-ear/HyperX Cloud III",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x1038,
        pid: 0x12E0,
        brand: "SteelSeries",
        model: "SteelSeries Arctis Nova Pro Wireless",
        autoeq_id: "Rtings/over-ear/SteelSeries Arctis Nova Pro Wireless",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x1038,
        pid: 0x2202,
        brand: "SteelSeries",
        model: "SteelSeries Arctis Nova 7",
        autoeq_id: "Rtings/over-ear/SteelSeries Arctis Nova 7",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x1038,
        pid: 0x12AD,
        brand: "SteelSeries",
        model: "SteelSeries Arctis 7",
        autoeq_id: "oratory1990/over-ear/SteelSeries Arctis 7",
        measurer: "oratory1990",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x046D,
        pid: 0x0AAA,
        brand: "Logitech",
        model: "Logitech G Pro X",
        autoeq_id: "oratory1990/over-ear/Logitech G Pro X",
        measurer: "oratory1990",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x046D,
        pid: 0x0AF7,
        brand: "Logitech",
        model: "Logitech G Pro X 2 Lightspeed",
        autoeq_id: "Rtings/over-ear/Logitech G PRO X 2 LIGHTSPEED",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x046D,
        pid: 0x0AB5,
        brand: "Logitech",
        model: "Logitech G733",
        autoeq_id: "Rtings/over-ear/Logitech G733 LIGHTSPEED Wireless",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x1395,
        pid: 0x0300,
        brand: "Sennheiser",
        model: "Sennheiser GSP 670",
        autoeq_id: "Rtings/over-ear/Sennheiser GSP 670",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x05AC,
        pid: 0x110A,
        brand: "Apple",
        model: "Apple USB-C to 3.5mm Adapter",
        autoeq_id: "reference/over-ear/Apple USB-C DAC",
        measurer: "Exo Reference",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x2FC6,
        pid: 0xF001,
        brand: "Moondrop",
        model: "Moondrop Chu II DSP",
        autoeq_id: "crinacle/in-ear/Moondrop Chu II",
        measurer: "crinacle",
        form: HeadphoneForm::InEar,
    },
    KnownUsbDevice {
        vid: 0x1532,
        pid: 0x0528,
        brand: "Razer",
        model: "Razer BlackShark V2 Pro",
        autoeq_id: "Rtings/over-ear/Razer BlackShark V2 Pro",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x1B1C,
        pid: 0x0A6B,
        brand: "Corsair",
        model: "Corsair HS80 RGB Wireless",
        autoeq_id: "Rtings/over-ear/Corsair HS80 RGB WIRELESS",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
    KnownUsbDevice {
        vid: 0x054C,
        pid: 0x0DF2,
        brand: "Sony",
        model: "Sony INZONE H9",
        autoeq_id: "Rtings/over-ear/Sony INZONE H9",
        measurer: "Rtings",
        form: HeadphoneForm::OverEar,
    },
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MatchConfidence {
    ExactUsbVidPid,
    EndpointName,
}

#[derive(Debug, Clone, PartialEq)]
pub struct HeadphoneMatch {
    pub entry: AutoEqEntry,
    pub confidence: MatchConfidence,
}

pub fn lookup_usb_headset(vid: u16, pid: u16) -> Option<KnownUsbDevice> {
    KNOWN_USB_HEADSETS
        .iter()
        .copied()
        .find(|d| d.vid == vid && d.pid == pid)
}

pub fn parse_usb_vid_pid(pnp_or_instance_id: &str) -> Option<(u16, u16)> {
    let upper = pnp_or_instance_id.to_ascii_uppercase();
    let vid_pos = upper.find("VID_")?;
    let pid_pos = upper.find("PID_")?;
    let vid_hex = upper.get(vid_pos + 4..vid_pos + 8)?;
    let pid_hex = upper.get(pid_pos + 4..pid_pos + 8)?;
    let vid = u16::from_str_radix(vid_hex, 16).ok()?;
    let pid = u16::from_str_radix(pid_hex, 16).ok()?;
    Some((vid, pid))
}

pub fn is_safe_autoeq_id(id: &str) -> bool {
    let parts: Vec<&str> = id.split('/').collect();
    if parts.len() != 3 {
        return false;
    }
    parts
        .iter()
        .all(|p| !p.is_empty() && *p != "." && *p != ".." && !p.contains('\\') && !p.contains(':'))
}

pub fn parse_autoeq_readme(markdown: &str) -> Vec<AutoEqEntry> {
    let mut entries = Vec::new();
    let mut seen = HashSet::new();

    for raw in markdown.lines() {
        let line = raw.trim();
        if !line.starts_with("- [") || !line.ends_with(')') {
            continue;
        }
        let Some(split) = line.find("](./") else {
            continue;
        };
        if split < 4 {
            continue;
        }
        let name = line[3..split].trim();
        let encoded_id = &line[split + 4..line.len() - 1];
        let id = percent_decode(encoded_id);
        if name.is_empty() || !is_safe_autoeq_id(&id) {
            continue;
        }
        let key = id.to_ascii_lowercase();
        if !seen.insert(key) {
            continue;
        }
        let mut parts = id.split('/');
        let measurer = parts.next().unwrap_or("AutoEq").to_string();
        let rig = parts.next().unwrap_or("over-ear");
        let form = HeadphoneForm::from_rig_str(rig);
        entries.push(AutoEqEntry {
            id,
            name: name.to_string(),
            measurer,
            form,
            usb_vid: None,
            usb_pid: None,
            bands: Vec::new(),
        });
    }
    entries
}

pub fn parse_parametric_eq_txt(text: &str) -> Vec<ParametricBand> {
    let mut out = Vec::new();
    for raw in text.lines() {
        let line = raw.trim();
        let Some(colon) = line.find(':') else {
            continue;
        };
        let prefix = line[..colon].trim();
        if prefix.len() < 6 || !prefix[..6].eq_ignore_ascii_case("filter") {
            continue;
        }
        let words: Vec<&str> = line[colon + 1..].split_whitespace().collect();
        if words.len() < 4 || !words[0].eq_ignore_ascii_case("ON") {
            continue;
        }
        let kind = match words[1].to_ascii_uppercase().as_str() {
            "PK" | "PEQ" => FilterKind::Peak,
            "LS" | "LSC" => FilterKind::LowShelf,
            "HS" | "HSC" => FilterKind::HighShelf,
            "LP" | "LPQ" => FilterKind::LowPass,
            "HP" | "HPQ" => FilterKind::HighPass,
            _ => continue,
        };
        let Some(freq_hz) = parse_word_after(&words, "Fc") else {
            continue;
        };
        let gain_db = match kind {
            FilterKind::LowPass | FilterKind::HighPass => {
                parse_word_after(&words, "Gain").unwrap_or(0.0)
            }
            _ => match parse_word_after(&words, "Gain") {
                Some(g) => g,
                None => continue,
            },
        };
        let q = parse_word_after(&words, "Q").unwrap_or(0.707);
        out.push(
            ParametricBand {
                kind,
                freq_hz,
                gain_db,
                q,
            }
            .clamped(48_000.0),
        );
        if out.len() >= MAX_BANDS {
            break;
        }
    }
    out
}

fn parse_word_after(words: &[&str], label: &str) -> Option<f64> {
    let idx = words.iter().position(|w| w.eq_ignore_ascii_case(label))?;
    words.get(idx + 1)?.parse::<f64>().ok()
}

fn percent_decode(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0usize;
    while i < bytes.len() {
        if bytes[i] == b'%'
            && i + 2 < bytes.len()
            && let (Some(hi), Some(lo)) = (hex_val(bytes[i + 1]), hex_val(bytes[i + 2]))
        {
            out.push((hi << 4) | lo);
            i += 3;
            continue;
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

const fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

#[derive(Debug, Clone, Default)]
pub struct AutoEqCatalog {
    entries: Vec<AutoEqEntry>,
}

impl AutoEqCatalog {
    pub fn new(entries: Vec<AutoEqEntry>) -> Self {
        Self { entries }
    }

    pub fn builtin_usb_catalog() -> Self {
        let entries = KNOWN_USB_HEADSETS
            .iter()
            .map(|d| AutoEqEntry {
                id: d.autoeq_id.to_string(),
                name: d.model.to_string(),
                measurer: d.measurer.to_string(),
                form: d.form,
                usb_vid: Some(d.vid),
                usb_pid: Some(d.pid),
                bands: Vec::new(),
            })
            .collect();
        Self { entries }
    }

    pub fn entries(&self) -> &[AutoEqEntry] {
        &self.entries
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn search(&self, query: &str, limit: usize) -> Vec<&AutoEqEntry> {
        let q = query.trim();
        if q.is_empty() {
            return self.entries.iter().take(limit).collect();
        }
        let q_lower = q.to_ascii_lowercase();
        let terms: Vec<&str> = q_lower.split_whitespace().collect();
        let mut hits: Vec<(usize, &AutoEqEntry)> = self
            .entries
            .iter()
            .filter_map(|e| {
                let matched = terms.iter().all(|t| {
                    contains_ascii_ci(&e.name, t)
                        || contains_ascii_ci(&e.measurer, t)
                        || contains_ascii_ci(e.form.as_str(), t)
                });
                if !matched {
                    return None;
                }
                let starts = usize::from(starts_with_ascii_ci(&e.name, &q_lower));
                Some((
                    starts * 100 + usize::from(e.measurer == "oratory1990") * 10,
                    e,
                ))
            })
            .collect();
        hits.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.name.cmp(&b.1.name)));
        hits.into_iter().take(limit).map(|(_, e)| e).collect()
    }

    pub fn recognize_device(
        &self,
        endpoint_name: &str,
        pnp_or_usb_id: Option<&str>,
    ) -> Option<HeadphoneMatch> {
        if let Some((vid, pid)) = pnp_or_usb_id.and_then(parse_usb_vid_pid) {
            if let Some(entry) = self
                .entries
                .iter()
                .find(|e| e.usb_vid == Some(vid) && e.usb_pid == Some(pid))
            {
                return Some(HeadphoneMatch {
                    entry: entry.clone(),
                    confidence: MatchConfidence::ExactUsbVidPid,
                });
            }
            if let Some(known) = lookup_usb_headset(vid, pid) {
                if let Some(entry) = self
                    .entries
                    .iter()
                    .find(|e| e.id.eq_ignore_ascii_case(known.autoeq_id))
                {
                    return Some(HeadphoneMatch {
                        entry: entry.clone(),
                        confidence: MatchConfidence::ExactUsbVidPid,
                    });
                }
                return Some(HeadphoneMatch {
                    entry: AutoEqEntry {
                        id: known.autoeq_id.to_string(),
                        name: known.model.to_string(),
                        measurer: known.measurer.to_string(),
                        form: known.form,
                        usb_vid: Some(known.vid),
                        usb_pid: Some(known.pid),
                        bands: Vec::new(),
                    },
                    confidence: MatchConfidence::ExactUsbVidPid,
                });
            }
        }

        self.match_endpoint_name(endpoint_name)
            .map(|entry| HeadphoneMatch {
                entry: entry.clone(),
                confidence: MatchConfidence::EndpointName,
            })
    }

    pub fn match_endpoint_name(&self, endpoint_name: &str) -> Option<&AutoEqEntry> {
        if endpoint_name.trim().is_empty() {
            return None;
        }
        let heard: HashSet<String> = tokenize(endpoint_name).into_iter().collect();
        if heard.is_empty() {
            return None;
        }
        let mut best: Option<&AutoEqEntry> = None;
        let mut best_score = 0usize;
        let mut tie = false;

        for entry in &self.entries {
            let words = tokenize(&entry.name);
            if words.is_empty() {
                continue;
            }
            let whole = words.iter().all(|w| heard.contains(w));
            if !whole && (words.len() < 2 || !words[1..].iter().all(|w| heard.contains(w))) {
                continue;
            }
            let model = if whole { &words[..] } else { &words[1..] };
            let telling = model
                .iter()
                .filter(|w| !GENERIC_AUDIO_WORDS.contains(&w.as_str()))
                .count();
            if telling == 0 {
                continue;
            }
            let score = telling * 10 + model.len() * 2 + usize::from(whole);
            if score > best_score {
                best = Some(entry);
                best_score = score;
                tie = false;
            } else if score == best_score {
                tie = true;
            }
        }
        if tie { None } else { best }
    }

    pub fn compile_binary_index(&self) -> std::io::Result<Vec<u8>> {
        let mut measurer_table: Vec<&str> = Vec::new();
        let mut measurer_map: HashMap<&str, u16> = HashMap::new();
        for e in &self.entries {
            if !measurer_map.contains_key(e.measurer.as_str()) {
                let idx = measurer_table.len() as u16;
                measurer_table.push(e.measurer.as_str());
                measurer_map.insert(e.measurer.as_str(), idx);
            }
        }

        let mut raw = Vec::with_capacity(self.entries.len() * 64);
        raw.extend_from_slice(&(measurer_table.len() as u16).to_le_bytes());
        for m in &measurer_table {
            write_str_u8(&mut raw, m);
        }
        raw.extend_from_slice(&(self.entries.len() as u32).to_le_bytes());

        for e in &self.entries {
            let m_idx = measurer_map[e.measurer.as_str()];
            raw.extend_from_slice(&m_idx.to_le_bytes());
            let mut flags = (e.form as u8) & 0x03;
            if e.usb_vid.is_some() && e.usb_pid.is_some() {
                flags |= 0x80;
            }
            raw.push(flags);
            write_str_u8(&mut raw, &e.name);
            let compact_id = strip_standard_id_prefix(&e.id, &e.measurer, e.form);
            write_str_u8(&mut raw, compact_id);
            if let (Some(vid), Some(pid)) = (e.usb_vid, e.usb_pid) {
                raw.extend_from_slice(&vid.to_le_bytes());
                raw.extend_from_slice(&pid.to_le_bytes());
            }
            let band_count = e.bands.len().min(MAX_BANDS) as u8;
            raw.push(band_count);
            for b in e.bands.iter().take(band_count as usize) {
                raw.push(b.kind as u8);
                let fc = b.freq_hz.round().clamp(20.0, 20_000.0) as u16;
                let gain_centi = (b.gain_db * 100.0).round().clamp(-2400.0, 1200.0) as i16;
                let q_milli = (b.q * 1000.0).round().clamp(100.0, 10_000.0) as u16;
                raw.extend_from_slice(&fc.to_le_bytes());
                raw.extend_from_slice(&gain_centi.to_le_bytes());
                raw.extend_from_slice(&q_milli.to_le_bytes());
            }
        }

        let mut encoder = DeflateEncoder::new(Vec::new(), Compression::best());
        encoder.write_all(&raw)?;
        let compressed = encoder.finish()?;

        let mut blob = Vec::with_capacity(10 + compressed.len());
        blob.extend_from_slice(&BINARY_INDEX_MAGIC);
        blob.extend_from_slice(&BINARY_INDEX_VERSION.to_le_bytes());
        blob.extend_from_slice(&(raw.len() as u32).to_le_bytes());
        blob.extend_from_slice(&compressed);
        Ok(blob)
    }

    pub fn decode_binary_index(blob: &[u8]) -> std::io::Result<Self> {
        if blob.len() < 10 || blob[0..4] != BINARY_INDEX_MAGIC {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "invalid AutoEq binary index header",
            ));
        }
        let ver = u16::from_le_bytes([blob[4], blob[5]]);
        if ver != BINARY_INDEX_VERSION {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "unsupported AutoEq binary index version",
            ));
        }
        let expected_raw_len = u32::from_le_bytes([blob[6], blob[7], blob[8], blob[9]]) as usize;
        if expected_raw_len > 16 * 1024 * 1024 {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "AutoEq binary index payload exceeds safety cap",
            ));
        }

        let mut decoder = DeflateDecoder::new(&blob[10..]);
        let mut raw = Vec::with_capacity(expected_raw_len);
        decoder.read_to_end(&mut raw)?;

        let mut cursor = 0usize;
        let m_count = read_u16(&raw, &mut cursor)? as usize;
        let mut measurers = Vec::with_capacity(m_count);
        for _ in 0..m_count {
            measurers.push(read_str_u8(&raw, &mut cursor)?);
        }

        let entry_count = read_u32(&raw, &mut cursor)? as usize;
        let mut entries = Vec::with_capacity(entry_count);
        for _ in 0..entry_count {
            let m_idx = read_u16(&raw, &mut cursor)? as usize;
            let measurer = measurers
                .get(m_idx)
                .ok_or_else(|| {
                    std::io::Error::new(std::io::ErrorKind::InvalidData, "bad measurer index")
                })?
                .clone();
            let flags = read_u8(&raw, &mut cursor)?;
            let form = HeadphoneForm::from_u8(flags & 0x03);
            let has_usb = (flags & 0x80) != 0;
            let name = read_str_u8(&raw, &mut cursor)?;
            let compact_id = read_str_u8(&raw, &mut cursor)?;
            let id = if compact_id.is_empty() {
                format!("{measurer}/{}/{name}", form.as_str())
            } else if compact_id.contains('/') {
                compact_id
            } else {
                format!("{measurer}/{}/{compact_id}", form.as_str())
            };
            let (usb_vid, usb_pid) = if has_usb {
                (
                    Some(read_u16(&raw, &mut cursor)?),
                    Some(read_u16(&raw, &mut cursor)?),
                )
            } else {
                (None, None)
            };
            let band_count = (read_u8(&raw, &mut cursor)? as usize).min(MAX_BANDS);
            let mut bands = Vec::with_capacity(band_count);
            for _ in 0..band_count {
                let kind = FilterKind::from_u8(read_u8(&raw, &mut cursor)?);
                let fc = read_u16(&raw, &mut cursor)? as f64;
                let gain_centi = read_i16(&raw, &mut cursor)? as f64 / 100.0;
                let q = read_u16(&raw, &mut cursor)? as f64 / 1000.0;
                bands.push(ParametricBand {
                    kind,
                    freq_hz: fc,
                    gain_db: gain_centi,
                    q,
                });
            }
            entries.push(AutoEqEntry {
                id,
                name,
                measurer,
                form,
                usb_vid,
                usb_pid,
                bands,
            });
        }
        Ok(Self { entries })
    }
}

fn strip_standard_id_prefix<'a>(id: &'a str, measurer: &str, form: HeadphoneForm) -> &'a str {
    let prefix = format!("{measurer}/{}/", form.as_str());
    id.strip_prefix(&prefix).unwrap_or(id)
}

fn write_str_u8(out: &mut Vec<u8>, s: &str) {
    let bytes = s.as_bytes();
    let len = bytes.len().min(255);
    out.push(len as u8);
    out.extend_from_slice(&bytes[..len]);
}

fn read_str_u8(buf: &[u8], cur: &mut usize) -> std::io::Result<String> {
    let len = read_u8(buf, cur)? as usize;
    let slice = buf
        .get(*cur..*cur + len)
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof str"))?;
    *cur += len;
    Ok(String::from_utf8_lossy(slice).into_owned())
}

fn read_u8(buf: &[u8], cur: &mut usize) -> std::io::Result<u8> {
    let b = *buf
        .get(*cur)
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof u8"))?;
    *cur += 1;
    Ok(b)
}

fn read_u16(buf: &[u8], cur: &mut usize) -> std::io::Result<u16> {
    let s = buf
        .get(*cur..*cur + 2)
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof u16"))?;
    *cur += 2;
    Ok(u16::from_le_bytes([s[0], s[1]]))
}

fn read_i16(buf: &[u8], cur: &mut usize) -> std::io::Result<i16> {
    let s = buf
        .get(*cur..*cur + 2)
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof i16"))?;
    *cur += 2;
    Ok(i16::from_le_bytes([s[0], s[1]]))
}

fn read_u32(buf: &[u8], cur: &mut usize) -> std::io::Result<u32> {
    let s = buf
        .get(*cur..*cur + 4)
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof u32"))?;
    *cur += 4;
    Ok(u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}

const GENERIC_AUDIO_WORDS: &[&str] = &[
    "headset",
    "headphones",
    "headphone",
    "earphone",
    "earphones",
    "speakers",
    "speaker",
    "wireless",
    "gaming",
    "game",
    "chat",
    "stereo",
    "usb",
    "audio",
    "pro",
    "x",
    "the",
    "and",
    "with",
    "hands",
    "free",
    "bluetooth",
    "le",
    "high",
    "definition",
    "device",
    "digital",
    "output",
    "realtek",
    "r",
    "anc",
    "on",
    "off",
    "mode",
    "dongle",
    "dac",
    "amp",
    "receiver",
    "lightspeed",
    "hyperspeed",
    "slipstream",
    "sonar",
    "dts",
    "spatial",
    "sound",
    "24g",
    "24ghz",
    "usbaudio",
];

fn starts_with_ascii_ci(haystack: &str, needle_lower: &str) -> bool {
    let h = haystack.as_bytes();
    let n = needle_lower.as_bytes();
    h.len() >= n.len() && h[..n.len()].eq_ignore_ascii_case(n)
}

fn contains_ascii_ci(haystack: &str, needle_lower: &str) -> bool {
    let h = haystack.as_bytes();
    let n = needle_lower.as_bytes();
    if n.is_empty() {
        return true;
    }
    if n.len() > h.len() {
        return false;
    }
    h.windows(n.len()).any(|w| w.eq_ignore_ascii_case(n))
}

fn tokenize(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut cur = String::new();
    for ch in text.chars() {
        if ch.is_ascii_alphanumeric() {
            cur.push(ch.to_ascii_lowercase());
        } else if !cur.is_empty() {
            out.push(std::mem::take(&mut cur));
        }
    }
    if !cur.is_empty() {
        out.push(cur);
    }
    out
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum EqSoundProfile {
    #[default]
    Reference,
    CompetitiveClarity,
    Vocal,
    Immersion,
    Bass,
}

impl EqSoundProfile {
    pub const fn label(self) -> &'static str {
        match self {
            Self::Reference => "Reference",
            Self::CompetitiveClarity => "Competitive Clarity",
            Self::Vocal => "Vocal",
            Self::Immersion => "Immersion",
            Self::Bass => "Bass",
        }
    }

    pub const fn preset_bands(self) -> &'static [ParametricBand] {
        match self {
            Self::Reference => &[],
            Self::CompetitiveClarity => PRESET_COMPETITIVE_CLARITY,
            Self::Vocal => PRESET_VOCAL,
            Self::Immersion => PRESET_IMMERSION,
            Self::Bass => PRESET_BASS,
        }
    }
}

const PRESET_COMPETITIVE_CLARITY: &[ParametricBand] = &[
    ParametricBand::low_shelf(105.0, -5.0, 0.71),
    ParametricBand::peak(260.0, -2.0, 1.1),
    ParametricBand::peak(1_150.0, 1.5, 1.2),
    ParametricBand::peak(2_600.0, 2.8, 1.15),
    ParametricBand::peak(4_200.0, 2.0, 1.4),
    ParametricBand::peak(7_500.0, -2.0, 1.8),
    ParametricBand::high_shelf(11_000.0, -1.5, 0.71),
];

const PRESET_VOCAL: &[ParametricBand] = &[
    ParametricBand::low_shelf(85.0, -1.5, 0.71),
    ParametricBand::peak(210.0, -1.8, 0.9),
    ParametricBand::peak(1_500.0, 1.5, 1.0),
    ParametricBand::peak(3_100.0, 1.8, 1.3),
    ParametricBand::peak(6_800.0, -1.2, 2.0),
];

const PRESET_IMMERSION: &[ParametricBand] = &[
    ParametricBand::low_shelf(75.0, 3.0, 0.71),
    ParametricBand::peak(240.0, -1.2, 1.0),
    ParametricBand::peak(2_200.0, 0.8, 1.0),
    ParametricBand::high_shelf(9_000.0, 1.5, 0.71),
];

const PRESET_BASS: &[ParametricBand] = &[
    ParametricBand::low_shelf(90.0, 4.0, 0.71),
    ParametricBand::peak(250.0, -1.0, 1.1),
];

pub const IEM_NATURAL_REFINEMENT: &[ParametricBand] = &[
    ParametricBand::peak(190.0, 1.5, 0.85),
    ParametricBand::peak(3_400.0, -2.0, 1.35),
    ParametricBand::peak(5_200.0, -1.0, 2.0),
];

pub const NIGHT_SHIFT_BANDS: &[ParametricBand] = &[
    ParametricBand::high_shelf(5_000.0, -3.0, 0.71),
    ParametricBand::low_shelf(150.0, 1.0, 0.71),
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActiveGameAudioSession {
    pub game_id: String,
    pub title: String,
    pub profile: EqSoundProfile,
    pub anti_cheat_class: AntiCheatClass,
    pub effective_anti_cheat_class: AntiCheatClass,
    pub uses_windows_apo_only: bool,
    pub forbids_game_process_touch: bool,
}

pub struct GameAudioRouter;

impl GameAudioRouter {
    pub fn classify_title(title: &str) -> EqSoundProfile {
        let key: String = title
            .chars()
            .filter(|c| c.is_ascii_alphanumeric())
            .map(|c| c.to_ascii_lowercase())
            .collect();
        if key.is_empty() {
            return EqSoundProfile::Reference;
        }
        if COMPETITIVE_FPS_EXACT.contains(&key.as_str())
            || COMPETITIVE_FPS_PREFIXES.iter().any(|p| key.starts_with(p))
        {
            return EqSoundProfile::CompetitiveClarity;
        }
        if VOCAL_EXACT.contains(&key.as_str()) || VOCAL_PREFIXES.iter().any(|p| key.starts_with(p))
        {
            return EqSoundProfile::Vocal;
        }
        if IMMERSION_EXACT.contains(&key.as_str())
            || IMMERSION_PREFIXES.iter().any(|p| key.starts_with(p))
        {
            return EqSoundProfile::Immersion;
        }
        EqSoundProfile::Reference
    }

    pub fn session_for_game(
        game_id: impl Into<String>,
        title: impl Into<String>,
        anti_cheat_class: AntiCheatClass,
        override_profile: Option<EqSoundProfile>,
    ) -> ActiveGameAudioSession {
        let title_str = title.into();
        let effective = anti_cheat_class.effective();
        let profile = override_profile.unwrap_or_else(|| Self::classify_title(&title_str));
        ActiveGameAudioSession {
            game_id: game_id.into(),
            title: title_str,
            profile,
            anti_cheat_class,
            effective_anti_cheat_class: effective,
            uses_windows_apo_only: anti_cheat_class.allows_windows_audio_apo()
                && anti_cheat_class.forbids_audio_process_hooking(),
            forbids_game_process_touch: effective.forbids_game_tree_set_rights(),
        }
    }

    pub fn build_dsp_config(
        headphone: Option<&AutoEqEntry>,
        profile: EqSoundProfile,
        night_shift: bool,
        spatial_mode: SpatialMode,
        spatial_strength: f64,
        ab_compare_bypass: bool,
    ) -> DspConfig {
        let mut combined = Vec::with_capacity(MAX_BANDS);
        if let Some(hp) = headphone {
            for &b in &hp.bands {
                if combined.len() >= MAX_BANDS {
                    break;
                }
                combined.push(b);
            }
            if !hp.bands.is_empty()
                && matches!(hp.form, HeadphoneForm::InEar | HeadphoneForm::Earbud)
            {
                for &b in IEM_NATURAL_REFINEMENT {
                    if combined.len() >= MAX_BANDS {
                        break;
                    }
                    combined.push(b);
                }
            }
        }
        for &b in profile.preset_bands() {
            if combined.len() >= MAX_BANDS {
                break;
            }
            combined.push(b);
        }
        if night_shift {
            for &b in NIGHT_SHIFT_BANDS {
                if combined.len() >= MAX_BANDS {
                    break;
                }
                combined.push(b);
            }
        }
        let mut cfg = DspConfig::from_bands(&combined);
        cfg.bypass = ab_compare_bypass;
        cfg.auto_preamp = true;
        cfg.limiter_enabled = true;
        cfg.spatial_mode = spatial_mode;
        cfg.spatial_strength = spatial_strength;
        cfg
    }
}

const COMPETITIVE_FPS_EXACT: &[&str] = &[
    "counterstrike2",
    "cs2",
    "valorant",
    "apexlegends",
    "fortnite",
    "pubg",
    "pubgbattlegrounds",
    "overwatch2",
    "marvelrivals",
    "thefinals",
    "deadlock",
    "escapefromtarkov",
    "arenabreakoutinfinite",
    "huntshowdown1896",
    "deltaforce",
    "haloinfinite",
    "teamfortress2",
    "rust",
    "dayz",
    "readyornot",
    "warzone",
    "destiny2",
];

const COMPETITIVE_FPS_PREFIXES: &[&str] = &[
    "callofduty",
    "battlefield",
    "rainbowsix",
    "tomclancysrainbowsix",
    "counterstrike",
];

const VOCAL_EXACT: &[&str] = &[
    "cyberpunk2077",
    "discoelysium",
    "metaphorrefantazio",
    "clairobscurexpedition33",
    "skyrim",
    "starfield",
    "outerwilds",
    "control",
    "deathstranding",
    "ghostoftsushima",
];

const VOCAL_PREFIXES: &[&str] = &[
    "baldursgate",
    "thewitcher",
    "reddeadredemption",
    "thelastofus",
    "godofwar",
    "masseffect",
    "alanwake",
    "finalfantasy",
];

const IMMERSION_EXACT: &[&str] = &[
    "eldenring",
    "sekiro",
    "liesofp",
    "blackmythwukong",
    "helldivers2",
    "rocketleague",
    "hollowknight",
    "hades",
    "hades2",
    "palworld",
    "valheim",
    "subnautica",
    "doomthedarkages",
];

const IMMERSION_PREFIXES: &[&str] = &[
    "darksouls",
    "monsterhunter",
    "doom",
    "forza",
    "assettocorsa",
    "grandtheftauto",
    "gta",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_autoeq_readme_and_rejects_traversal() {
        let md = "\
- [Sennheiser HD 600](./oratory1990/over-ear/Sennheiser%20HD%20600)
- [1MORE Aero (ANC Off)](./Rtings/in-ear/1MORE%20Aero%20(ANC%20Off))
- [Bad Traversal](./../etc/passwd)
- [Bad Colon](./oratory1990/over-ear/C:evil)
";
        let list = parse_autoeq_readme(md);
        assert_eq!(list.len(), 2);
        assert_eq!(list[0].name, "Sennheiser HD 600");
        assert_eq!(list[0].measurer, "oratory1990");
        assert_eq!(list[0].form, HeadphoneForm::OverEar);
        assert!(
            list[0]
                .attribution_line()
                .contains("measured by oratory1990")
        );
        assert_eq!(list[1].name, "1MORE Aero (ANC Off)");
        assert_eq!(list[1].form, HeadphoneForm::InEar);
    }

    #[test]
    fn parses_parametric_eq_txt_filters() {
        let txt = "\
Preamp: -6.2 dB
Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70
Filter 2: ON PK Fc 235 Hz Gain -1.8 dB Q 1.15
Filter 3: ON HSC Fc 10000 Hz Gain 2.1 dB Q 0.71
Filter 4: OFF PK Fc 500 Hz Gain 9.0 dB Q 1.0
";
        let bands = parse_parametric_eq_txt(txt);
        assert_eq!(bands.len(), 3);
        assert_eq!(bands[0].kind, FilterKind::LowShelf);
        assert!((bands[0].freq_hz - 105.0).abs() < 1.0e-6);
        assert_eq!(bands[1].kind, FilterKind::Peak);
        assert!((bands[1].gain_db - (-1.8)).abs() < 1.0e-6);
        assert_eq!(bands[2].kind, FilterKind::HighShelf);
    }

    #[test]
    fn four_thousand_entry_binary_index_stays_under_400_kb_and_round_trips() {
        let measurers = ["oratory1990", "crinacle", "Rtings", "Innerfidelity"];
        let forms = [
            HeadphoneForm::OverEar,
            HeadphoneForm::InEar,
            HeadphoneForm::Earbud,
        ];
        let mut entries = Vec::with_capacity(4_000);
        for i in 0..4_000usize {
            let m = measurers[i % measurers.len()];
            let f = forms[i % forms.len()];
            let name = format!("Model Series {:04} Reference", i);
            let id = format!("{m}/{}/{name}", f.as_str());
            let bands = vec![
                ParametricBand::low_shelf(105.0, 3.2 - (i % 7) as f64 * 0.5, 0.71),
                ParametricBand::peak(250.0, -1.5, 1.1),
                ParametricBand::peak(1_200.0, 1.2, 1.4),
                ParametricBand::peak(3_100.0, -2.4, 2.0),
                ParametricBand::peak(5_800.0, 1.8, 3.2),
                ParametricBand::high_shelf(10_000.0, -1.0, 0.71),
                ParametricBand::peak(80.0, 1.1, 0.9),
                ParametricBand::peak(640.0, -0.8, 1.3),
                ParametricBand::peak(4_500.0, 0.7, 2.1),
                ParametricBand::peak(8_200.0, -1.3, 2.8),
            ];
            entries.push(AutoEqEntry {
                id,
                name,
                measurer: m.to_string(),
                form: f,
                usb_vid: if i < 20 { Some(0x1038) } else { None },
                usb_pid: if i < 20 {
                    Some(0x2200 + i as u16)
                } else {
                    None
                },
                bands,
            });
        }
        let catalog = AutoEqCatalog::new(entries);
        let blob = catalog.compile_binary_index().expect("compile index");
        assert!(
            blob.len() < TARGET_MAX_INDEX_BYTES,
            "binary index {} bytes exceeded {} byte cap",
            blob.len(),
            TARGET_MAX_INDEX_BYTES
        );
        let decoded = AutoEqCatalog::decode_binary_index(&blob).expect("decode index");
        assert_eq!(decoded.len(), 4_000);
        assert_eq!(decoded.entries()[0].measurer, "oratory1990");
        assert_eq!(decoded.entries()[0].bands.len(), 10);
        assert!((decoded.entries()[0].bands[0].gain_db - 3.2).abs() < 0.02);
        assert_eq!(decoded.entries()[0].usb_vid, Some(0x1038));
    }

    #[test]
    fn usb_vid_pid_and_endpoint_name_recognition_work_offline() {
        let catalog = AutoEqCatalog::builtin_usb_catalog();
        let m = catalog
            .recognize_device(
                "Headphones (USB Audio Device)",
                Some("USB\\VID_0951&PID_16A4&MI_00\\7&1A2B3C"),
            )
            .expect("HyperX Cloud II via USB VID/PID");
        assert_eq!(m.confidence, MatchConfidence::ExactUsbVidPid);
        assert_eq!(m.entry.name, "HyperX Cloud II");
        assert_eq!(m.entry.measurer, "oratory1990");

        let by_name = catalog
            .recognize_device("Speakers (Arctis Nova 7)", None)
            .expect("Arctis Nova 7 by endpoint name");
        assert_eq!(by_name.confidence, MatchConfidence::EndpointName);
        assert_eq!(by_name.entry.name, "SteelSeries Arctis Nova 7");

        assert!(
            catalog
                .recognize_device("Headphones (Realtek High Definition Audio)", None)
                .is_none()
        );
    }

    #[test]
    fn game_router_uses_shared_anti_cheat_class_with_unknown_strict() {
        let unknown_fps = GameAudioRouter::session_for_game(
            "riot:valorant",
            "VALORANT",
            AntiCheatClass::Unknown,
            None,
        );
        assert_eq!(unknown_fps.profile, EqSoundProfile::CompetitiveClarity);
        assert_eq!(
            unknown_fps.effective_anti_cheat_class,
            AntiCheatClass::CompetitivePvpOnline
        );
        assert!(unknown_fps.uses_windows_apo_only);
        assert!(unknown_fps.forbids_game_process_touch);

        let offline_rpg = GameAudioRouter::session_for_game(
            "steam:1091500",
            "Cyberpunk 2077",
            AntiCheatClass::OfflineAntiCheat,
            None,
        );
        assert_eq!(offline_rpg.profile, EqSoundProfile::Vocal);
        assert!(offline_rpg.uses_windows_apo_only);
        assert!(!offline_rpg.forbids_game_process_touch);

        let music =
            GameAudioRouter::session_for_game("desktop", "Spotify", AntiCheatClass::Unknown, None);
        assert_eq!(music.profile, EqSoundProfile::Reference);
    }
}
