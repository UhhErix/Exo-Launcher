use serde::{Deserialize, Serialize};
use std::fmt;

pub const WIRE_MAGIC: u32 = 0x3157_5845;
pub const WIRE_VERSION: u8 = 1;
pub const HEADER_LEN: usize = 20;
pub const MAX_FRAME_BYTES: usize = 32 * 1024 * 1024;

pub const METHOD_LIBRARY_GET: u16 = 1;
pub const METHOD_MEDIA_FRAME: u16 = 2;

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameKind {
    Request = 1,
    Response = 2,
    Event = 3,
    BinaryMedia = 4,
}

impl FrameKind {
    pub fn from_u8(v: u8) -> Result<Self, WireError> {
        match v {
            1 => Ok(Self::Request),
            2 => Ok(Self::Response),
            3 => Ok(Self::Event),
            4 => Ok(Self::BinaryMedia),
            _ => Err(WireError::InvalidKind(v)),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameHeader {
    pub version: u8,
    pub kind: FrameKind,
    pub method_id: u16,
    pub req_id: u64,
    pub payload_len: u32,
}

impl FrameHeader {
    pub fn encode(&self) -> [u8; HEADER_LEN] {
        let mut out = [0u8; HEADER_LEN];
        out[0..4].copy_from_slice(&WIRE_MAGIC.to_le_bytes());
        out[4] = self.version;
        out[5] = self.kind as u8;
        out[6..8].copy_from_slice(&self.method_id.to_le_bytes());
        out[8..16].copy_from_slice(&self.req_id.to_le_bytes());
        out[16..20].copy_from_slice(&self.payload_len.to_le_bytes());
        out
    }

    pub fn decode(buf: &[u8; HEADER_LEN]) -> Result<Self, WireError> {
        let magic = u32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]);
        if magic != WIRE_MAGIC {
            return Err(WireError::BadMagic(magic));
        }
        let version = buf[4];
        if version != WIRE_VERSION {
            return Err(WireError::UnsupportedVersion(version));
        }
        let kind = FrameKind::from_u8(buf[5])?;
        let method_id = u16::from_le_bytes([buf[6], buf[7]]);
        let req_id = u64::from_le_bytes(buf[8..16].try_into().unwrap());
        let payload_len = u32::from_le_bytes([buf[16], buf[17], buf[18], buf[19]]);
        if payload_len as usize > MAX_FRAME_BYTES {
            return Err(WireError::FrameTooLarge(payload_len));
        }
        Ok(Self {
            version,
            kind,
            method_id,
            req_id,
            payload_len,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WireError {
    BadMagic(u32),
    UnsupportedVersion(u8),
    InvalidKind(u8),
    FrameTooLarge(u32),
    UnexpectedEof,
    InvalidUtf8,
    TrailingBytes(usize),
}

impl fmt::Display for WireError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for WireError {}

pub struct WireWriter {
    buf: Vec<u8>,
}

impl WireWriter {
    pub fn with_capacity(cap: usize) -> Self {
        Self {
            buf: Vec::with_capacity(cap),
        }
    }

    pub fn clear(&mut self) {
        self.buf.clear();
    }

    pub fn as_slice(&self) -> &[u8] {
        &self.buf
    }

    pub fn into_vec(self) -> Vec<u8> {
        self.buf
    }

    pub fn put_u8(&mut self, v: u8) {
        self.buf.push(v);
    }

    pub fn put_u16(&mut self, v: u16) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_u32(&mut self, v: u32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_i32(&mut self, v: i32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_u64(&mut self, v: u64) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_i64(&mut self, v: i64) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_f64(&mut self, v: f64) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    pub fn put_str(&mut self, s: &str) {
        self.put_u32(s.len() as u32);
        self.buf.extend_from_slice(s.as_bytes());
    }

    pub fn put_opt_str(&mut self, s: Option<&str>) {
        match s {
            Some(v) => {
                self.put_u8(1);
                self.put_str(v);
            }
            None => self.put_u8(0),
        }
    }

    pub fn put_bytes(&mut self, data: &[u8]) {
        self.put_u32(data.len() as u32);
        self.buf.extend_from_slice(data);
    }
}

pub struct WireReader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> WireReader<'a> {
    pub fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    pub fn remaining(&self) -> usize {
        self.buf.len().saturating_sub(self.pos)
    }

    pub fn finish(&self) -> Result<(), WireError> {
        let rem = self.remaining();
        if rem == 0 {
            Ok(())
        } else {
            Err(WireError::TrailingBytes(rem))
        }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], WireError> {
        let end = self.pos.checked_add(n).ok_or(WireError::UnexpectedEof)?;
        if end > self.buf.len() {
            return Err(WireError::UnexpectedEof);
        }
        let s = &self.buf[self.pos..end];
        self.pos = end;
        Ok(s)
    }

    pub fn get_u8(&mut self) -> Result<u8, WireError> {
        Ok(self.take(1)?[0])
    }

    pub fn get_u16(&mut self) -> Result<u16, WireError> {
        let b = self.take(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }

    pub fn get_u32(&mut self) -> Result<u32, WireError> {
        let b = self.take(4)?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    pub fn get_i32(&mut self) -> Result<i32, WireError> {
        let b = self.take(4)?;
        Ok(i32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    pub fn get_u64(&mut self) -> Result<u64, WireError> {
        let b = self.take(8)?;
        Ok(u64::from_le_bytes(b.try_into().unwrap()))
    }

    pub fn get_i64(&mut self) -> Result<i64, WireError> {
        let b = self.take(8)?;
        Ok(i64::from_le_bytes(b.try_into().unwrap()))
    }

    pub fn get_f64(&mut self) -> Result<f64, WireError> {
        let b = self.take(8)?;
        Ok(f64::from_le_bytes(b.try_into().unwrap()))
    }

    pub fn get_str(&mut self) -> Result<&'a str, WireError> {
        let len = self.get_u32()? as usize;
        let bytes = self.take(len)?;
        std::str::from_utf8(bytes).map_err(|_| WireError::InvalidUtf8)
    }

    pub fn get_opt_str(&mut self) -> Result<Option<&'a str>, WireError> {
        match self.get_u8()? {
            0 => Ok(None),
            1 => Ok(Some(self.get_str()?)),
            other => Err(WireError::InvalidKind(other)),
        }
    }

    pub fn get_bytes(&mut self) -> Result<&'a [u8], WireError> {
        let len = self.get_u32()? as usize;
        self.take(len)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameDto {
    pub id: String,
    pub store: String,
    pub title: String,
    pub installed: bool,
    pub cover_url: Option<String>,
    pub playtime_hours: f64,
    pub size_bytes: i64,
    pub last_played_unix: i64,
    pub anti_cheat: bool,
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryPayload {
    pub schema_version: i32,
    pub generated_at_unix: i64,
    pub games: Vec<GameDto>,
}

#[derive(Debug, Clone, Copy)]
pub struct GameView<'a> {
    pub id: &'a str,
    pub store: &'a str,
    pub title: &'a str,
    pub installed: bool,
    pub cover_url: Option<&'a str>,
    pub playtime_hours: f64,
    pub size_bytes: i64,
    pub last_played_unix: i64,
    pub anti_cheat: bool,
    pub tag_count: u32,
}

pub fn encode_library(payload: &LibraryPayload, out: &mut WireWriter) {
    out.clear();
    out.put_i32(payload.schema_version);
    out.put_i64(payload.generated_at_unix);
    out.put_u32(payload.games.len() as u32);
    for g in &payload.games {
        out.put_str(&g.id);
        out.put_str(&g.store);
        out.put_str(&g.title);
        out.put_u8(u8::from(g.installed));
        out.put_opt_str(g.cover_url.as_deref());
        out.put_f64(g.playtime_hours);
        out.put_i64(g.size_bytes);
        out.put_i64(g.last_played_unix);
        out.put_u8(u8::from(g.anti_cheat));
        out.put_u32(g.tags.len() as u32);
        for t in &g.tags {
            out.put_str(t);
        }
    }
}

pub fn decode_library_owned(buf: &[u8]) -> Result<LibraryPayload, WireError> {
    let mut r = WireReader::new(buf);
    let schema_version = r.get_i32()?;
    let generated_at_unix = r.get_i64()?;
    let count = r.get_u32()? as usize;
    let mut games = Vec::with_capacity(count.min(4096));
    for _ in 0..count {
        let id = r.get_str()?.to_owned();
        let store = r.get_str()?.to_owned();
        let title = r.get_str()?.to_owned();
        let installed = r.get_u8()? != 0;
        let cover_url = r.get_opt_str()?.map(str::to_owned);
        let playtime_hours = r.get_f64()?;
        let size_bytes = r.get_i64()?;
        let last_played_unix = r.get_i64()?;
        let anti_cheat = r.get_u8()? != 0;
        let tag_count = r.get_u32()? as usize;
        let mut tags = Vec::with_capacity(tag_count.min(64));
        for _ in 0..tag_count {
            tags.push(r.get_str()?.to_owned());
        }
        games.push(GameDto {
            id,
            store,
            title,
            installed,
            cover_url,
            playtime_hours,
            size_bytes,
            last_played_unix,
            anti_cheat,
            tags,
        });
    }
    r.finish()?;
    Ok(LibraryPayload {
        schema_version,
        generated_at_unix,
        games,
    })
}

pub fn visit_library_zerocopy<'a, F>(buf: &'a [u8], mut on_game: F) -> Result<(i32, i64, u32), WireError>
where
    F: FnMut(GameView<'a>),
{
    let mut r = WireReader::new(buf);
    let schema_version = r.get_i32()?;
    let generated_at_unix = r.get_i64()?;
    let count = r.get_u32()?;
    for _ in 0..count {
        let id = r.get_str()?;
        let store = r.get_str()?;
        let title = r.get_str()?;
        let installed = r.get_u8()? != 0;
        let cover_url = r.get_opt_str()?;
        let playtime_hours = r.get_f64()?;
        let size_bytes = r.get_i64()?;
        let last_played_unix = r.get_i64()?;
        let anti_cheat = r.get_u8()? != 0;
        let tag_count = r.get_u32()?;
        for _ in 0..tag_count {
            let _ = r.get_str()?;
        }
        on_game(GameView {
            id,
            store,
            title,
            installed,
            cover_url,
            playtime_hours,
            size_bytes,
            last_played_unix,
            anti_cheat,
            tag_count,
        });
    }
    r.finish()?;
    Ok((schema_version, generated_at_unix, count))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_library(n: usize) -> LibraryPayload {
        let mut games = Vec::with_capacity(n);
        for i in 0..n {
            games.push(GameDto {
                id: format!("steam:{}", 1000 + i),
                store: "steam".into(),
                title: format!("Title {i}"),
                installed: i % 2 == 0,
                cover_url: if i % 3 == 0 {
                    None
                } else {
                    Some(format!("https://shared.fastly.steamstatic.com/apps/{i}.jpg"))
                },
                playtime_hours: i as f64 * 1.25,
                size_bytes: 1_000_000_000 + i as i64,
                last_played_unix: 1_760_000_000 + i as i64,
                anti_cheat: i % 5 == 0,
                tags: vec!["fps".into(), "hdr".into()],
            });
        }
        LibraryPayload {
            schema_version: 1,
            generated_at_unix: 1_764_000_000,
            games,
        }
    }

    #[test]
    fn header_roundtrip_and_guards() {
        let h = FrameHeader {
            version: WIRE_VERSION,
            kind: FrameKind::Response,
            method_id: METHOD_LIBRARY_GET,
            req_id: 42,
            payload_len: 1024,
        };
        let raw = h.encode();
        assert_eq!(FrameHeader::decode(&raw).unwrap(), h);

        let mut bad = raw;
        bad[0] ^= 0xff;
        assert!(matches!(FrameHeader::decode(&bad), Err(WireError::BadMagic(_))));

        let mut huge = raw;
        huge[16..20].copy_from_slice(&((MAX_FRAME_BYTES as u32) + 1).to_le_bytes());
        assert!(matches!(FrameHeader::decode(&huge), Err(WireError::FrameTooLarge(_))));
    }

    #[test]
    fn library_owned_and_zerocopy_roundtrip() {
        let lib = sample_library(25);
        let mut w = WireWriter::with_capacity(4096);
        encode_library(&lib, &mut w);
        let decoded = decode_library_owned(w.as_slice()).unwrap();
        assert_eq!(decoded, lib);

        let mut seen = 0usize;
        let mut installed = 0usize;
        let (ver, _, count) = visit_library_zerocopy(w.as_slice(), |g| {
            assert_eq!(g.id, lib.games[seen].id);
            assert_eq!(g.title, lib.games[seen].title);
            if g.installed {
                installed += 1;
            }
            seen += 1;
        })
        .unwrap();
        assert_eq!(ver, 1);
        assert_eq!(count as usize, lib.games.len());
        assert_eq!(seen, lib.games.len());
        assert_eq!(installed, 13);
    }

    #[test]
    fn rejects_truncated_and_invalid_utf8() {
        let lib = sample_library(2);
        let mut w = WireWriter::with_capacity(256);
        encode_library(&lib, &mut w);
        let bytes = w.into_vec();
        assert_eq!(
            decode_library_owned(&bytes[..bytes.len() - 3]),
            Err(WireError::UnexpectedEof)
        );

        let mut bad = vec![0u8; 4 + 8 + 4 + 4 + 2];
        bad[0..4].copy_from_slice(&1i32.to_le_bytes());
        bad[4..12].copy_from_slice(&0i64.to_le_bytes());
        bad[12..16].copy_from_slice(&1u32.to_le_bytes());
        bad[16..20].copy_from_slice(&2u32.to_le_bytes());
        bad[20] = 0xff;
        bad[21] = 0xfe;
        assert_eq!(decode_library_owned(&bad), Err(WireError::InvalidUtf8));
    }
}
