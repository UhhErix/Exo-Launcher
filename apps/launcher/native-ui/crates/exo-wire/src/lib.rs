pub mod generated;

use std::fmt;

pub use generated::{
    EVENT_ID_AUTOPILOT_LIVE, EVENT_ID_INSTALL_PROGRESS, EVENT_ID_LIBRARY_UPDATED, EVENT_ID_UNKNOWN,
    EVENTS, HOST_CONTRACT_VERSION, METHOD_ID_GAME_EXTRAS, METHOD_ID_GAME_GET,
    METHOD_ID_LIBRARY_GET, METHOD_ID_LIBRARY_REFRESH, METHOD_ID_RPC_CANCEL,
    METHOD_ID_SHELL_BOOTSTRAP, METHOD_ID_UNKNOWN, METHODS, SCHEMA_HASH, event_id, event_name,
    method_id, method_name,
};

pub const WIRE_MAGIC: u32 = 0x3157_5845;
pub const WIRE_MAGIC_BYTES: [u8; 4] = *b"EXW1";
pub const WIRE_VERSION: u8 = 1;
pub const HEADER_LEN: usize = 20;
pub const HELLO_PAYLOAD_LEN: usize = 16;
pub const MAX_FRAME_BYTES: usize = 96 * 1024 * 1024;

pub const FLAG_USE_SCHEMA_IDS: u16 = 1 << 0;
pub const FLAG_BINARY_HOT_CODECS: u16 = 1 << 1;

pub const HELLO_STATUS_OK: u8 = 0;
pub const HELLO_STATUS_SCHEMA_MISMATCH: u8 = 1;

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameKind {
    Request = 1,
    ResponseOk = 2,
    ResponseErr = 3,
    Event = 4,
    BinaryMedia = 5,
    Hello = 6,
    HelloAck = 7,
}

impl FrameKind {
    pub fn from_u8(v: u8) -> Result<Self, WireError> {
        match v {
            1 => Ok(Self::Request),
            2 => Ok(Self::ResponseOk),
            3 => Ok(Self::ResponseErr),
            4 => Ok(Self::Event),
            5 => Ok(Self::BinaryMedia),
            6 => Ok(Self::Hello),
            7 => Ok(Self::HelloAck),
            _ => Err(WireError::InvalidKind(v)),
        }
    }
}

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PayloadEncoding {
    Empty = 0,
    JsonUtf8 = 1,
    BinaryTyped = 2,
}

impl PayloadEncoding {
    pub fn from_u8(v: u8) -> Result<Self, WireError> {
        match v {
            0 => Ok(Self::Empty),
            1 => Ok(Self::JsonUtf8),
            2 => Ok(Self::BinaryTyped),
            _ => Err(WireError::InvalidEncoding(v)),
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
        out[0..4].copy_from_slice(&WIRE_MAGIC_BYTES);
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
        let req_id = u64::from_le_bytes([
            buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15],
        ]);
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HelloPayload {
    pub min_version: u8,
    pub max_version: u8,
    pub flags: u16,
    pub max_frame_bytes: u32,
    pub schema_hash: u64,
}

impl HelloPayload {
    pub fn current() -> Self {
        Self {
            min_version: WIRE_VERSION,
            max_version: WIRE_VERSION,
            flags: FLAG_USE_SCHEMA_IDS | FLAG_BINARY_HOT_CODECS,
            max_frame_bytes: MAX_FRAME_BYTES as u32,
            schema_hash: SCHEMA_HASH,
        }
    }

    pub fn encode(&self) -> [u8; HELLO_PAYLOAD_LEN] {
        let mut out = [0u8; HELLO_PAYLOAD_LEN];
        out[0] = self.min_version;
        out[1] = self.max_version;
        out[2..4].copy_from_slice(&self.flags.to_le_bytes());
        out[4..8].copy_from_slice(&self.max_frame_bytes.to_le_bytes());
        out[8..16].copy_from_slice(&self.schema_hash.to_le_bytes());
        out
    }

    pub fn decode(buf: &[u8]) -> Result<Self, WireError> {
        if buf.len() != HELLO_PAYLOAD_LEN {
            return Err(WireError::UnexpectedEof);
        }
        Ok(Self {
            min_version: buf[0],
            max_version: buf[1],
            flags: u16::from_le_bytes([buf[2], buf[3]]),
            max_frame_bytes: u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]),
            schema_hash: u64::from_le_bytes([
                buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15],
            ]),
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HelloAckPayload {
    pub negotiated_version: u8,
    pub status: u8,
    pub flags: u16,
    pub max_frame_bytes: u32,
    pub host_schema_hash: u64,
}

impl HelloAckPayload {
    pub fn encode(&self) -> [u8; HELLO_PAYLOAD_LEN] {
        let mut out = [0u8; HELLO_PAYLOAD_LEN];
        out[0] = self.negotiated_version;
        out[1] = self.status;
        out[2..4].copy_from_slice(&self.flags.to_le_bytes());
        out[4..8].copy_from_slice(&self.max_frame_bytes.to_le_bytes());
        out[8..16].copy_from_slice(&self.host_schema_hash.to_le_bytes());
        out
    }

    pub fn decode(buf: &[u8]) -> Result<Self, WireError> {
        if buf.len() != HELLO_PAYLOAD_LEN {
            return Err(WireError::UnexpectedEof);
        }
        Ok(Self {
            negotiated_version: buf[0],
            status: buf[1],
            flags: u16::from_le_bytes([buf[2], buf[3]]),
            max_frame_bytes: u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]),
            host_schema_hash: u64::from_le_bytes([
                buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15],
            ]),
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WireError {
    BadMagic(u32),
    UnsupportedVersion(u8),
    InvalidKind(u8),
    InvalidEncoding(u8),
    FrameTooLarge(u32),
    UnexpectedEof,
    InvalidUtf8,
    InvalidBool(u8),
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

    pub fn reserve_header(&mut self) {
        self.buf.clear();
        self.buf.resize(HEADER_LEN, 0);
    }

    pub fn finalize_frame(&mut self, kind: FrameKind, method_id: u16, req_id: u64) -> &[u8] {
        let payload_len = (self.buf.len() - HEADER_LEN) as u32;
        let hdr = FrameHeader {
            version: WIRE_VERSION,
            kind,
            method_id,
            req_id,
            payload_len,
        };
        self.buf[0..HEADER_LEN].copy_from_slice(&hdr.encode());
        &self.buf
    }

    pub fn put_u8(&mut self, v: u8) {
        self.buf.push(v);
    }

    pub fn put_bool(&mut self, v: bool) {
        self.buf.push(u8::from(v));
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

    pub fn put_opt_u32(&mut self, v: Option<u32>) {
        match v {
            Some(n) => {
                self.put_u8(1);
                self.put_u32(n);
            }
            None => self.put_u8(0),
        }
    }

    pub fn put_opt_u64(&mut self, v: Option<u64>) {
        match v {
            Some(n) => {
                self.put_u8(1);
                self.put_u64(n);
            }
            None => self.put_u8(0),
        }
    }

    pub fn put_opt_f64(&mut self, v: Option<f64>) {
        match v {
            Some(n) => {
                self.put_u8(1);
                self.put_f64(n);
            }
            None => self.put_u8(0),
        }
    }

    pub fn put_str(&mut self, s: &str) {
        self.put_u32(s.len() as u32);
        self.buf.extend_from_slice(s.as_bytes());
    }

    pub fn put_short_str(&mut self, s: &str) {
        self.put_u16(s.len() as u16);
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

    pub fn put_raw(&mut self, data: &[u8]) {
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

    pub fn remaining_slice(&self) -> &'a [u8] {
        &self.buf[self.pos..]
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

    pub fn get_bool(&mut self) -> Result<bool, WireError> {
        match self.get_u8()? {
            0 => Ok(false),
            1 => Ok(true),
            other => Err(WireError::InvalidBool(other)),
        }
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
        Ok(u64::from_le_bytes([
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
        ]))
    }

    pub fn get_i64(&mut self) -> Result<i64, WireError> {
        let b = self.take(8)?;
        Ok(i64::from_le_bytes([
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
        ]))
    }

    pub fn get_f64(&mut self) -> Result<f64, WireError> {
        let b = self.take(8)?;
        Ok(f64::from_le_bytes([
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
        ]))
    }

    pub fn get_opt_u32(&mut self) -> Result<Option<u32>, WireError> {
        if self.get_bool()? {
            Ok(Some(self.get_u32()?))
        } else {
            Ok(None)
        }
    }

    pub fn get_opt_u64(&mut self) -> Result<Option<u64>, WireError> {
        if self.get_bool()? {
            Ok(Some(self.get_u64()?))
        } else {
            Ok(None)
        }
    }

    pub fn get_opt_f64(&mut self) -> Result<Option<f64>, WireError> {
        if self.get_bool()? {
            Ok(Some(self.get_f64()?))
        } else {
            Ok(None)
        }
    }

    pub fn get_str(&mut self) -> Result<&'a str, WireError> {
        let len = self.get_u32()? as usize;
        let bytes = self.take(len)?;
        std::str::from_utf8(bytes).map_err(|_| WireError::InvalidUtf8)
    }

    pub fn get_short_str(&mut self) -> Result<&'a str, WireError> {
        let len = self.get_u16()? as usize;
        let bytes = self.take(len)?;
        std::str::from_utf8(bytes).map_err(|_| WireError::InvalidUtf8)
    }

    pub fn get_string(&mut self) -> Result<String, WireError> {
        Ok(self.get_str()?.to_owned())
    }

    pub fn get_opt_str(&mut self) -> Result<Option<&'a str>, WireError> {
        if self.get_bool()? {
            Ok(Some(self.get_str()?))
        } else {
            Ok(None)
        }
    }

    pub fn get_opt_string(&mut self) -> Result<Option<String>, WireError> {
        Ok(self.get_opt_str()?.map(str::to_owned))
    }

    pub fn get_string_vec(&mut self) -> Result<Vec<String>, WireError> {
        let len = self.get_u32()? as usize;
        if len > self.remaining() / 4 {
            return Err(WireError::UnexpectedEof);
        }
        let mut out = Vec::with_capacity(len.min(4096));
        for _ in 0..len {
            out.push(self.get_string()?);
        }
        Ok(out)
    }

    pub fn get_bytes(&mut self) -> Result<&'a [u8], WireError> {
        let len = self.get_u32()? as usize;
        self.take(len)
    }
}

pub fn encode_hello_frame(hello: HelloPayload) -> [u8; HEADER_LEN + HELLO_PAYLOAD_LEN] {
    let hdr = FrameHeader {
        version: WIRE_VERSION,
        kind: FrameKind::Hello,
        method_id: 0,
        req_id: 0,
        payload_len: HELLO_PAYLOAD_LEN as u32,
    };
    let mut out = [0u8; HEADER_LEN + HELLO_PAYLOAD_LEN];
    out[0..HEADER_LEN].copy_from_slice(&hdr.encode());
    out[HEADER_LEN..].copy_from_slice(&hello.encode());
    out
}

pub fn encode_hello_ack_frame(ack: HelloAckPayload) -> [u8; HEADER_LEN + HELLO_PAYLOAD_LEN] {
    let hdr = FrameHeader {
        version: WIRE_VERSION,
        kind: FrameKind::HelloAck,
        method_id: 0,
        req_id: 0,
        payload_len: HELLO_PAYLOAD_LEN as u32,
    };
    let mut out = [0u8; HEADER_LEN + HELLO_PAYLOAD_LEN];
    out[0..HEADER_LEN].copy_from_slice(&hdr.encode());
    out[HEADER_LEN..].copy_from_slice(&ack.encode());
    out
}

pub fn encode_request_frame(
    req_id: u64,
    method: &str,
    use_schema_ids: bool,
    encoding: PayloadEncoding,
    payload: &[u8],
) -> Vec<u8> {
    let id = if use_schema_ids {
        method_id(method)
    } else {
        METHOD_ID_UNKNOWN
    };
    let extra = if id == METHOD_ID_UNKNOWN {
        2 + method.len()
    } else {
        0
    };
    let mut writer = WireWriter::with_capacity(HEADER_LEN + extra + 1 + payload.len());
    writer.reserve_header();
    if id == METHOD_ID_UNKNOWN {
        writer.put_short_str(method);
    }
    writer.put_u8(encoding as u8);
    writer.put_raw(payload);
    writer.finalize_frame(FrameKind::Request, id, req_id);
    writer.into_vec()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_header_roundtrips_and_rejects_invalid_inputs() {
        let hdr = FrameHeader {
            version: WIRE_VERSION,
            kind: FrameKind::Request,
            method_id: METHOD_ID_LIBRARY_GET,
            req_id: 42,
            payload_len: 1024,
        };
        let encoded = hdr.encode();
        assert_eq!(&encoded[0..4], b"EXW1");
        assert_eq!(FrameHeader::decode(&encoded).unwrap(), hdr);

        let mut bad_magic = encoded;
        bad_magic[0] = b'{';
        assert!(matches!(
            FrameHeader::decode(&bad_magic),
            Err(WireError::BadMagic(_))
        ));

        let mut bad_version = encoded;
        bad_version[4] = 99;
        assert_eq!(
            FrameHeader::decode(&bad_version),
            Err(WireError::UnsupportedVersion(99))
        );

        let mut too_large = encoded;
        too_large[16..20].copy_from_slice(&((MAX_FRAME_BYTES as u32) + 1).to_le_bytes());
        assert!(matches!(
            FrameHeader::decode(&too_large),
            Err(WireError::FrameTooLarge(_))
        ));
    }

    #[test]
    fn hello_and_hello_ack_roundtrip() {
        let hello = HelloPayload::current();
        let frame = encode_hello_frame(hello);
        let hdr = FrameHeader::decode(frame[0..HEADER_LEN].try_into().unwrap()).unwrap();
        assert_eq!(hdr.kind, FrameKind::Hello);
        assert_eq!(hdr.payload_len as usize, HELLO_PAYLOAD_LEN);
        assert_eq!(HelloPayload::decode(&frame[HEADER_LEN..]).unwrap(), hello);

        let ack = HelloAckPayload {
            negotiated_version: WIRE_VERSION,
            status: HELLO_STATUS_OK,
            flags: FLAG_USE_SCHEMA_IDS | FLAG_BINARY_HOT_CODECS,
            max_frame_bytes: MAX_FRAME_BYTES as u32,
            host_schema_hash: SCHEMA_HASH,
        };
        let ack_frame = encode_hello_ack_frame(ack);
        let ack_hdr = FrameHeader::decode(ack_frame[0..HEADER_LEN].try_into().unwrap()).unwrap();
        assert_eq!(ack_hdr.kind, FrameKind::HelloAck);
        assert_eq!(
            HelloAckPayload::decode(&ack_frame[HEADER_LEN..]).unwrap(),
            ack
        );
    }

    #[test]
    fn generated_schema_tables_are_sorted_and_invertible() {
        assert!(!METHODS.is_empty());
        assert!(!EVENTS.is_empty());
        assert_ne!(METHOD_ID_LIBRARY_GET, METHOD_ID_UNKNOWN);
        assert_ne!(METHOD_ID_GAME_GET, METHOD_ID_UNKNOWN);
        assert_ne!(METHOD_ID_GAME_EXTRAS, METHOD_ID_UNKNOWN);
        assert_ne!(METHOD_ID_SHELL_BOOTSTRAP, METHOD_ID_UNKNOWN);
        assert_ne!(EVENT_ID_AUTOPILOT_LIVE, EVENT_ID_UNKNOWN);
        assert_ne!(EVENT_ID_INSTALL_PROGRESS, EVENT_ID_UNKNOWN);
        assert_ne!(EVENT_ID_LIBRARY_UPDATED, EVENT_ID_UNKNOWN);

        for (i, &m) in METHODS.iter().enumerate() {
            let id = method_id(m);
            assert_eq!(id as usize, i + 1);
            assert_eq!(method_name(id), Some(m));
        }
        for (i, &e) in EVENTS.iter().enumerate() {
            let id = event_id(e);
            assert_eq!(id as usize, i + 1);
            assert_eq!(event_name(id), Some(e));
        }
        assert_eq!(method_id("unknown.method.xyz"), METHOD_ID_UNKNOWN);
        assert_eq!(method_name(0), None);
        assert_eq!(event_id("unknown.event.xyz"), EVENT_ID_UNKNOWN);
        assert_eq!(event_name(0), None);
    }

    #[test]
    fn writer_and_reader_roundtrip_primitives() {
        let mut w = WireWriter::with_capacity(64);
        w.put_bool(true);
        w.put_u16(0x1234);
        w.put_u32(0xdead_beef);
        w.put_i32(-42);
        w.put_u64(0x0123_4567_89ab_cdef);
        w.put_f64(144.5);
        w.put_opt_f64(Some(59.9));
        w.put_opt_f64(None);
        w.put_str("exo-wire");
        w.put_opt_str(Some("hello"));
        w.put_opt_str(None);
        w.put_bytes(&[1, 2, 3, 4]);

        let mut r = WireReader::new(w.as_slice());
        assert!(r.get_bool().unwrap());
        assert_eq!(r.get_u16().unwrap(), 0x1234);
        assert_eq!(r.get_u32().unwrap(), 0xdead_beef);
        assert_eq!(r.get_i32().unwrap(), -42);
        assert_eq!(r.get_u64().unwrap(), 0x0123_4567_89ab_cdef);
        assert_eq!(r.get_f64().unwrap(), 144.5);
        assert_eq!(r.get_opt_f64().unwrap(), Some(59.9));
        assert_eq!(r.get_opt_f64().unwrap(), None);
        assert_eq!(r.get_str().unwrap(), "exo-wire");
        assert_eq!(r.get_opt_str().unwrap(), Some("hello"));
        assert_eq!(r.get_opt_str().unwrap(), None);
        assert_eq!(r.get_bytes().unwrap(), &[1, 2, 3, 4]);
        r.finish().unwrap();
    }

    #[test]
    fn fuzz_and_corrupt_frames_return_error_never_panic() {
        let mut hdr_bytes = FrameHeader {
            version: WIRE_VERSION,
            kind: FrameKind::Request,
            method_id: METHOD_ID_LIBRARY_GET,
            req_id: 1,
            payload_len: 16,
        }
        .encode();

        for bad_ver in 0u8..=255 {
            if bad_ver == WIRE_VERSION {
                continue;
            }
            hdr_bytes[4] = bad_ver;
            assert_eq!(
                FrameHeader::decode(&hdr_bytes),
                Err(WireError::UnsupportedVersion(bad_ver))
            );
        }
        hdr_bytes[4] = WIRE_VERSION;

        for bad_kind in [0u8, 8, 9, 127, 255] {
            hdr_bytes[5] = bad_kind;
            assert_eq!(
                FrameHeader::decode(&hdr_bytes),
                Err(WireError::InvalidKind(bad_kind))
            );
        }
        hdr_bytes[5] = FrameKind::Request as u8;

        for huge in [(MAX_FRAME_BYTES as u32) + 1, u32::MAX / 2, u32::MAX] {
            hdr_bytes[16..20].copy_from_slice(&huge.to_le_bytes());
            assert_eq!(
                FrameHeader::decode(&hdr_bytes),
                Err(WireError::FrameTooLarge(huge))
            );
        }

        for bad_enc in 3u8..=255 {
            assert_eq!(
                PayloadEncoding::from_u8(bad_enc),
                Err(WireError::InvalidEncoding(bad_enc))
            );
        }

        for len in 0..HELLO_PAYLOAD_LEN {
            let short = vec![0u8; len];
            assert_eq!(HelloPayload::decode(&short), Err(WireError::UnexpectedEof));
            assert_eq!(
                HelloAckPayload::decode(&short),
                Err(WireError::UnexpectedEof)
            );
        }
        let long_hello = vec![0u8; HELLO_PAYLOAD_LEN + 1];
        assert_eq!(
            HelloPayload::decode(&long_hello),
            Err(WireError::UnexpectedEof)
        );
        assert_eq!(
            HelloAckPayload::decode(&long_hello),
            Err(WireError::UnexpectedEof)
        );

        let invalid_utf8 = [4u8, 0, 0, 0, 0xFF, 0xFE, 0xFD, 0xFC];
        let mut r = WireReader::new(&invalid_utf8);
        assert_eq!(r.get_str(), Err(WireError::InvalidUtf8));

        let huge_vec_bomb = [0xFFu8, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];
        let mut r = WireReader::new(&huge_vec_bomb);
        assert_eq!(r.get_string_vec(), Err(WireError::UnexpectedEof));

        let mut r = WireReader::new(&huge_vec_bomb);
        assert_eq!(r.get_bytes(), Err(WireError::UnexpectedEof));

        let mut seed: u64 = 0x9e37_79b9_7f4a_7c15;
        let mut next_u64 = || {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            seed
        };

        for case in 0..4096usize {
            let len = (next_u64() as usize) % 96;
            let mut buf = vec![0u8; len];
            for b in &mut buf {
                *b = (next_u64() & 0xFF) as u8;
            }
            if case % 4 == 0 && len >= HEADER_LEN {
                buf[0..4].copy_from_slice(&WIRE_MAGIC_BYTES);
            }
            if len >= HEADER_LEN {
                let raw: [u8; HEADER_LEN] = buf[..HEADER_LEN].try_into().unwrap();
                let _ = FrameHeader::decode(&raw);
            }
            let _ = HelloPayload::decode(&buf);
            let _ = HelloAckPayload::decode(&buf);

            let mut r1 = WireReader::new(&buf);
            let _ = r1.get_string_vec();
            let mut r2 = WireReader::new(&buf);
            let _ = r2.get_opt_string();
            let mut r3 = WireReader::new(&buf);
            let _ = r3.get_bytes();
            let mut r4 = WireReader::new(&buf);
            let _ = r4.get_bool();
            let mut r5 = WireReader::new(&buf);
            let _ = r5.get_short_str();
        }
    }
}
