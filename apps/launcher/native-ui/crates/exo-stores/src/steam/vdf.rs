pub const APPINFO_MAGIC_V41: u32 = 0x0756_4429;
pub const APPINFO_MAGIC_SHARD: u32 = 0x0756_442A;
const SHARD_HEADER_BYTES: usize = 44;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VdfValue<'a> {
    Str(&'a str),
    Obj(Vec<(&'a str, VdfValue<'a>)>),
}

impl<'a> VdfValue<'a> {
    pub fn as_str(&self) -> Option<&'a str> {
        match self {
            Self::Str(s) => Some(*s),
            Self::Obj(_) => None,
        }
    }

    pub fn as_obj(&self) -> Option<&[(&'a str, VdfValue<'a>)]> {
        match self {
            Self::Obj(v) => Some(v.as_slice()),
            Self::Str(_) => None,
        }
    }

    pub fn get_ci(&self, key: &str) -> Option<&VdfValue<'a>> {
        self.as_obj()?
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(key))
            .map(|(_, v)| v)
    }
}

struct TextScanner<'a> {
    bytes: &'a [u8],
    src: &'a str,
    pos: usize,
}

impl<'a> TextScanner<'a> {
    fn new(src: &'a str) -> Self {
        Self {
            bytes: src.as_bytes(),
            src,
            pos: 0,
        }
    }

    fn skip_ws(&mut self) {
        while self.pos < self.bytes.len() {
            let b = self.bytes[self.pos];
            if b.is_ascii_whitespace() {
                self.pos += 1;
            } else if b == b'/'
                && self.pos + 1 < self.bytes.len()
                && self.bytes[self.pos + 1] == b'/'
            {
                self.pos += 2;
                while self.pos < self.bytes.len() && self.bytes[self.pos] != b'\n' {
                    self.pos += 1;
                }
            } else {
                break;
            }
        }
    }

    fn next_token(&mut self) -> Option<TextToken<'a>> {
        self.skip_ws();
        if self.pos >= self.bytes.len() {
            return None;
        }
        match self.bytes[self.pos] {
            b'{' => {
                self.pos += 1;
                Some(TextToken::LBrace)
            }
            b'}' => {
                self.pos += 1;
                Some(TextToken::RBrace)
            }
            b'"' => {
                self.pos += 1;
                let start = self.pos;
                while self.pos < self.bytes.len() {
                    if self.bytes[self.pos] == b'\\' && self.pos + 1 < self.bytes.len() {
                        self.pos += 2;
                        continue;
                    }
                    if self.bytes[self.pos] == b'"' {
                        let s = &self.src[start..self.pos];
                        self.pos += 1;
                        return Some(TextToken::Atom(s));
                    }
                    self.pos += 1;
                }
                Some(TextToken::Atom(&self.src[start..]))
            }
            _ => {
                let start = self.pos;
                while self.pos < self.bytes.len()
                    && !self.bytes[self.pos].is_ascii_whitespace()
                    && !matches!(self.bytes[self.pos], b'{' | b'}' | b'"')
                {
                    self.pos += 1;
                }
                Some(TextToken::Atom(&self.src[start..self.pos]))
            }
        }
    }
}

enum TextToken<'a> {
    LBrace,
    RBrace,
    Atom(&'a str),
}

pub fn parse_text_vdf<'a>(src: &'a str) -> Result<Vec<(&'a str, VdfValue<'a>)>, String> {
    let mut sc = TextScanner::new(src);
    parse_text_object(&mut sc, false)
}

fn parse_text_object<'a>(
    sc: &mut TextScanner<'a>,
    expect_rbrace: bool,
) -> Result<Vec<(&'a str, VdfValue<'a>)>, String> {
    let mut pairs = Vec::new();
    while let Some(tok) = sc.next_token() {
        match tok {
            TextToken::RBrace => {
                if expect_rbrace {
                    return Ok(pairs);
                }
                return Err("Unexpected '}' in text VDF".into());
            }
            TextToken::Atom(key) => {
                let val_tok = sc
                    .next_token()
                    .ok_or_else(|| format!("Missing value for VDF key '{key}'"))?;
                match val_tok {
                    TextToken::LBrace => {
                        let child = parse_text_object(sc, true)?;
                        pairs.push((key, VdfValue::Obj(child)));
                    }
                    TextToken::Atom(val) => {
                        pairs.push((key, VdfValue::Str(val)));
                    }
                    TextToken::RBrace => {
                        return Err(format!("Unexpected '}}' after key '{key}'"));
                    }
                }
            }
            TextToken::LBrace => {
                return Err("Unexpected '{' without key in text VDF".into());
            }
        }
    }
    if expect_rbrace {
        return Err("Unclosed '{' in text VDF".into());
    }
    Ok(pairs)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SteamLibraryFolder<'a> {
    pub path: &'a str,
    pub app_ids: Vec<u32>,
}

pub fn parse_library_folders<'a>(src: &'a str) -> Result<Vec<SteamLibraryFolder<'a>>, String> {
    let root = parse_text_vdf(src)?;
    let Some((_, lib_obj)) = root
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case("libraryfolders"))
    else {
        return Ok(Vec::new());
    };
    let Some(entries) = lib_obj.as_obj() else {
        return Ok(Vec::new());
    };
    let mut out = Vec::new();
    for (_, v) in entries {
        if let Some(path) = v.get_ci("path").and_then(VdfValue::as_str) {
            let mut app_ids = Vec::new();
            if let Some(apps) = v.get_ci("apps").and_then(VdfValue::as_obj) {
                for (aid, _) in apps {
                    if let Ok(id) = aid.parse::<u32>() {
                        app_ids.push(id);
                    }
                }
            }
            out.push(SteamLibraryFolder { path, app_ids });
        }
    }
    Ok(out)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SteamAppManifest<'a> {
    pub app_id: u32,
    pub name: &'a str,
    pub install_dir: &'a str,
    pub state_flags: u32,
    pub size_on_disk: u64,
    pub build_id: &'a str,
}

pub fn parse_app_manifest<'a>(src: &'a str) -> Result<Option<SteamAppManifest<'a>>, String> {
    let root = parse_text_vdf(src)?;
    let Some((_, state)) = root
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case("AppState"))
    else {
        return Ok(None);
    };
    let Some(app_id) = state
        .get_ci("appid")
        .and_then(VdfValue::as_str)
        .and_then(|s| s.parse::<u32>().ok())
    else {
        return Ok(None);
    };
    Ok(Some(SteamAppManifest {
        app_id,
        name: state
            .get_ci("name")
            .and_then(VdfValue::as_str)
            .unwrap_or(""),
        install_dir: state
            .get_ci("installdir")
            .and_then(VdfValue::as_str)
            .unwrap_or(""),
        state_flags: state
            .get_ci("StateFlags")
            .and_then(VdfValue::as_str)
            .and_then(|s| s.parse::<u32>().ok())
            .unwrap_or(0),
        size_on_disk: state
            .get_ci("SizeOnDisk")
            .and_then(VdfValue::as_str)
            .and_then(|s| s.parse::<u64>().ok())
            .unwrap_or(0),
        build_id: state
            .get_ci("buildid")
            .and_then(VdfValue::as_str)
            .unwrap_or(""),
    }))
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SteamAppInfoRecord<'a> {
    pub app_id: u32,
    pub name: &'a str,
    pub app_type: &'a str,
    pub parent_app_id: &'a str,
}

pub fn parse_binary_appinfo<'a>(buf: &'a [u8]) -> Result<Vec<SteamAppInfoRecord<'a>>, String> {
    if buf.len() < 8 {
        return Err("Binary appinfo.vdf truncated (<8 bytes)".into());
    }
    let magic = u32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]);
    match magic {
        APPINFO_MAGIC_V41 => parse_appinfo_v41(buf),
        APPINFO_MAGIC_SHARD => parse_appinfo_shard(buf),
        other => Err(format!("Unsupported appinfo.vdf magic 0x{other:08X}")),
    }
}

fn read_u32_le(buf: &[u8], pos: &mut usize) -> Option<u32> {
    let end = pos.checked_add(4)?;
    let slice = buf.get(*pos..end)?;
    *pos = end;
    Some(u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
}

fn read_u64_le(buf: &[u8], pos: &mut usize) -> Option<u64> {
    let end = pos.checked_add(8)?;
    let slice = buf.get(*pos..end)?;
    *pos = end;
    Some(u64::from_le_bytes([
        slice[0], slice[1], slice[2], slice[3], slice[4], slice[5], slice[6], slice[7],
    ]))
}

fn read_cstr<'a>(buf: &'a [u8], pos: &mut usize) -> Option<&'a str> {
    let rest = buf.get(*pos..)?;
    let nul = rest.iter().position(|&b| b == 0)?;
    let s = std::str::from_utf8(&rest[..nul]).ok()?;
    *pos += nul + 1;
    Some(s)
}

fn parse_string_table(buf: &[u8], mut pos: usize, count: usize) -> Result<Vec<&str>, String> {
    let mut keys = Vec::with_capacity(count.min(65_536));
    for _ in 0..count {
        let s =
            read_cstr(buf, &mut pos).ok_or_else(|| "Truncated appinfo string table".to_string())?;
        keys.push(s);
    }
    Ok(keys)
}

#[derive(Clone, Copy)]
struct CommonKeyIds {
    common: Option<u32>,
    name: Option<u32>,
    app_type: Option<u32>,
    parent: Option<u32>,
}

impl CommonKeyIds {
    fn from_table(keys: &[&str]) -> Self {
        let mut out = Self {
            common: None,
            name: None,
            app_type: None,
            parent: None,
        };
        for (i, &k) in keys.iter().enumerate() {
            let idx = i as u32;
            if out.common.is_none() && k.eq_ignore_ascii_case("common") {
                out.common = Some(idx);
            } else if out.name.is_none() && k.eq_ignore_ascii_case("name") {
                out.name = Some(idx);
            } else if out.app_type.is_none() && k.eq_ignore_ascii_case("type") {
                out.app_type = Some(idx);
            } else if out.parent.is_none() && k.eq_ignore_ascii_case("parent") {
                out.parent = Some(idx);
            }
        }
        out
    }
}

fn parse_appinfo_v41<'a>(buf: &'a [u8]) -> Result<Vec<SteamAppInfoRecord<'a>>, String> {
    if buf.len() < 16 {
        return Err("appinfo v41 header truncated".into());
    }
    let mut pos = 8usize;
    let table_offset =
        read_u64_le(buf, &mut pos).ok_or("Missing v41 string table offset")? as usize;
    if table_offset >= buf.len() {
        return Err("appinfo v41 string table offset out of bounds".into());
    }
    let mut tpos = table_offset;
    let key_count = read_u32_le(buf, &mut tpos).ok_or("Missing v41 string table count")? as usize;
    let keys = parse_string_table(buf, tpos, key_count)?;
    let key_ids = CommonKeyIds::from_table(&keys);

    let mut out = Vec::with_capacity((table_offset / 128).min(262_144));
    while pos + 4 <= table_offset {
        let app_id = read_u32_le(buf, &mut pos).ok_or("Truncated app_id")?;
        if app_id == 0 {
            break;
        }
        let rec_size = read_u32_le(buf, &mut pos).ok_or("Truncated record size")? as usize;
        let rec_end = pos
            .checked_add(rec_size)
            .ok_or("Overflow in v41 record size")?;
        if rec_end > table_offset {
            return Err("v41 record extends past string table".into());
        }
        if rec_size < 60 {
            pos = rec_end;
            continue;
        }
        let kv_start = pos + 60;
        let (name, app_type, parent) = extract_common_fields(&buf[kv_start..rec_end], key_ids);
        out.push(SteamAppInfoRecord {
            app_id,
            name,
            app_type,
            parent_app_id: parent,
        });
        pos = rec_end;
    }
    Ok(out)
}

fn parse_appinfo_shard<'a>(buf: &'a [u8]) -> Result<Vec<SteamAppInfoRecord<'a>>, String> {
    if buf.len() < SHARD_HEADER_BYTES {
        return Err("appinfo shard header truncated".into());
    }
    let mut pos = 16usize;
    let app_count = read_u32_le(buf, &mut pos).ok_or("Missing shard app_count")? as usize;
    let index_size = read_u32_le(buf, &mut pos).ok_or("Missing shard index_size")? as usize;
    let key_count = read_u32_le(buf, &mut pos).ok_or("Missing shard key_count")? as usize;
    let key_table_size = read_u32_le(buf, &mut pos).ok_or("Missing shard key_table_size")? as usize;
    let data_size = read_u32_le(buf, &mut pos).ok_or("Missing shard data_size")? as usize;

    let index_start = SHARD_HEADER_BYTES;
    let key_start = index_start
        .checked_add(index_size)
        .ok_or("Shard index_size overflow")?;
    let data_start = key_start
        .checked_add(key_table_size)
        .ok_or("Shard key_table_size overflow")?;
    let data_end = data_start
        .checked_add(data_size)
        .ok_or("Shard data_size overflow")?;
    if data_end > buf.len() {
        return Err("Shard sections exceed file length".into());
    }

    let keys = parse_string_table(buf, key_start, key_count)?;
    let key_ids = CommonKeyIds::from_table(&keys);
    let mut ipos = index_start;
    let mut out = Vec::with_capacity(app_count.min(65_536));
    for _ in 0..app_count {
        if ipos + 16 > key_start {
            break;
        }
        let app_id = read_u32_le(buf, &mut ipos).ok_or("Shard index app_id")?;
        let offset = read_u32_le(buf, &mut ipos).ok_or("Shard index offset")? as usize;
        let size = read_u32_le(buf, &mut ipos).ok_or("Shard index size")? as usize;
        let extra = read_u32_le(buf, &mut ipos).ok_or("Shard index extra")? as usize;
        ipos = ipos.saturating_add(extra);
        let rec_start = data_start.saturating_add(offset);
        let rec_end = rec_start.saturating_add(size);
        if size > 20 && rec_end <= data_end {
            let (name, app_type, parent) =
                extract_common_fields(&buf[rec_start + 20..rec_end], key_ids);
            out.push(SteamAppInfoRecord {
                app_id,
                name,
                app_type,
                parent_app_id: parent,
            });
        }
    }
    Ok(out)
}

fn extract_common_fields(data: &[u8], key_ids: CommonKeyIds) -> (&str, &str, &str) {
    let mut pos = 0usize;
    let mut name = "";
    let mut app_type = "";
    let mut parent = "";
    walk_bkv(
        data,
        &mut pos,
        key_ids,
        false,
        &mut name,
        &mut app_type,
        &mut parent,
    );
    (name, app_type, parent)
}

fn walk_bkv<'a>(
    data: &'a [u8],
    pos: &mut usize,
    key_ids: CommonKeyIds,
    in_common: bool,
    name: &mut &'a str,
    app_type: &mut &'a str,
    parent: &mut &'a str,
) {
    while *pos < data.len() {
        let ty = data[*pos];
        *pos += 1;
        if ty == 0x08 {
            return;
        }
        let Some(key_idx) = read_u32_le(data, pos) else {
            return;
        };
        match ty {
            0x00 => {
                let next_common = in_common || key_ids.common == Some(key_idx);
                walk_bkv(data, pos, key_ids, next_common, name, app_type, parent);
            }
            0x01 => {
                let rest = &data[*pos..];
                let Some(nul) = rest.iter().position(|&b| b == 0) else {
                    return;
                };
                let slice = &rest[..nul];
                *pos += nul + 1;
                if in_common {
                    if key_ids.name == Some(key_idx) && name.is_empty() {
                        if let Ok(s) = std::str::from_utf8(slice) {
                            *name = s;
                        }
                    } else if key_ids.app_type == Some(key_idx) && app_type.is_empty() {
                        if let Ok(s) = std::str::from_utf8(slice) {
                            *app_type = s;
                        }
                    } else if key_ids.parent == Some(key_idx)
                        && parent.is_empty()
                        && let Ok(s) = std::str::from_utf8(slice)
                    {
                        *parent = s;
                    }
                }
            }
            0x02..=0x06 => {
                if *pos + 4 > data.len() {
                    return;
                }
                *pos += 4;
            }
            0x07 | 0x0A => {
                if *pos + 8 > data.len() {
                    return;
                }
                *pos += 8;
            }
            _ => return,
        }
    }
}

pub fn build_synthetic_appinfo_v41(app_count: usize) -> Vec<u8> {
    let keys = ["appinfo", "common", "name", "type", "parent", "buildid"];
    let mut records = Vec::with_capacity(app_count * 120);
    for i in 0..app_count {
        let app_id = 1000 + i as u32;
        let mut kv = Vec::with_capacity(64);
        kv.push(0x00);
        kv.extend_from_slice(&0u32.to_le_bytes());
        kv.push(0x00);
        kv.extend_from_slice(&1u32.to_le_bytes());
        kv.push(0x01);
        kv.extend_from_slice(&2u32.to_le_bytes());
        kv.extend_from_slice(format!("Synthetic Game Title #{app_id:06} Edition\0").as_bytes());
        kv.push(0x01);
        kv.extend_from_slice(&3u32.to_le_bytes());
        kv.extend_from_slice(b"Game\0");
        kv.push(0x02);
        kv.extend_from_slice(&5u32.to_le_bytes());
        kv.extend_from_slice(&(9_000_000u32 + i as u32).to_le_bytes());
        kv.push(0x08);
        kv.push(0x08);
        kv.push(0x08);

        let rec_size = (60 + kv.len()) as u32;
        records.extend_from_slice(&app_id.to_le_bytes());
        records.extend_from_slice(&rec_size.to_le_bytes());
        records.extend_from_slice(&[0u8; 60]);
        records.extend_from_slice(&kv);
    }
    records.extend_from_slice(&0u32.to_le_bytes());

    let table_offset = (16 + records.len()) as u64;
    let mut out = Vec::with_capacity(table_offset as usize + 128);
    out.extend_from_slice(&APPINFO_MAGIC_V41.to_le_bytes());
    out.extend_from_slice(&1u32.to_le_bytes());
    out.extend_from_slice(&table_offset.to_le_bytes());
    out.extend_from_slice(&records);
    out.extend_from_slice(&(keys.len() as u32).to_le_bytes());
    for k in keys {
        out.extend_from_slice(k.as_bytes());
        out.push(0);
    }
    out
}

pub fn build_synthetic_appinfo_shard(app_count: usize) -> Vec<u8> {
    let keys = ["appinfo", "common", "name", "type"];
    let mut key_table = Vec::new();
    for k in keys {
        key_table.extend_from_slice(k.as_bytes());
        key_table.push(0);
    }

    let mut index = Vec::with_capacity(app_count * 16);
    let mut data = Vec::with_capacity(app_count * 64);
    for i in 0..app_count {
        let app_id = 5000 + i as u32;
        let offset = data.len() as u32;
        let start = data.len();
        data.extend_from_slice(&[0u8; 20]);
        data.push(0x00);
        data.extend_from_slice(&0u32.to_le_bytes());
        data.push(0x00);
        data.extend_from_slice(&1u32.to_le_bytes());
        data.push(0x01);
        data.extend_from_slice(&2u32.to_le_bytes());
        data.extend_from_slice(format!("Sharded Steam Game #{app_id}\0").as_bytes());
        data.push(0x01);
        data.extend_from_slice(&3u32.to_le_bytes());
        data.extend_from_slice(b"Game\0");
        data.push(0x08);
        data.push(0x08);
        data.push(0x08);
        let size = (data.len() - start) as u32;

        index.extend_from_slice(&app_id.to_le_bytes());
        index.extend_from_slice(&offset.to_le_bytes());
        index.extend_from_slice(&size.to_le_bytes());
        index.extend_from_slice(&0u32.to_le_bytes());
    }

    let mut out =
        Vec::with_capacity(SHARD_HEADER_BYTES + index.len() + key_table.len() + data.len());
    out.extend_from_slice(&APPINFO_MAGIC_SHARD.to_le_bytes());
    out.extend_from_slice(&1u32.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes());
    out.extend_from_slice(&256u32.to_le_bytes());
    out.extend_from_slice(&(app_count as u32).to_le_bytes());
    out.extend_from_slice(&(index.len() as u32).to_le_bytes());
    out.extend_from_slice(&(keys.len() as u32).to_le_bytes());
    out.extend_from_slice(&(key_table.len() as u32).to_le_bytes());
    out.extend_from_slice(&(data.len() as u32).to_le_bytes());
    out.extend_from_slice(&[0u8; 8]);
    out.extend_from_slice(&index);
    out.extend_from_slice(&key_table);
    out.extend_from_slice(&data);
    out
}
