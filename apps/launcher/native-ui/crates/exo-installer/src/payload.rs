use crate::InstallError;
use std::fs::{self, File};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;

pub const EMBED_FOOTER_MAGIC: [u8; 8] = *b"EXOPACK1";
pub const EMBED_FOOTER_LEN: usize = 8 + 8 + 8 + 64 + 32;

#[derive(Debug, Clone)]
pub struct EmbeddedPayload {
    pub pack: Vec<u8>,
    pub signature: [u8; 64],
    pub public_key: [u8; 32],
}

pub fn write_self_contained_installer(
    stub_exe: &Path,
    pack_bytes: &[u8],
    signature: &[u8; 64],
    public_key: &[u8; 32],
    out_exe: &Path,
) -> Result<u64, InstallError> {
    let stub = fs::read(stub_exe)?;
    let payload_offset = stub.len() as u64;
    let payload_len = pack_bytes.len() as u64;

    if let Some(parent) = out_exe.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut f = File::create(out_exe)?;
    f.write_all(&stub)?;
    f.write_all(pack_bytes)?;
    f.write_all(&payload_offset.to_le_bytes())?;
    f.write_all(&payload_len.to_le_bytes())?;
    f.write_all(signature)?;
    f.write_all(public_key)?;
    f.write_all(&EMBED_FOOTER_MAGIC)?;
    f.flush()?;
    Ok(payload_offset + payload_len + EMBED_FOOTER_LEN as u64)
}

pub fn read_embedded_payload(exe_path: &Path) -> Result<Option<EmbeddedPayload>, InstallError> {
    let mut f = File::open(exe_path)?;
    let total = f.metadata()?.len();
    if total < EMBED_FOOTER_LEN as u64 {
        return Ok(None);
    }
    f.seek(SeekFrom::End(-(EMBED_FOOTER_LEN as i64)))?;
    let mut footer = [0u8; EMBED_FOOTER_LEN];
    f.read_exact(&mut footer)?;
    if footer[EMBED_FOOTER_LEN - 8..] != EMBED_FOOTER_MAGIC {
        return Ok(None);
    }
    let payload_offset = u64::from_le_bytes(footer[0..8].try_into().unwrap());
    let payload_len = u64::from_le_bytes(footer[8..16].try_into().unwrap());
    let payload_end = payload_offset
        .checked_add(payload_len)
        .ok_or_else(|| InstallError::InvalidArg("embedded payload offset overflow".into()))?;
    if payload_end + EMBED_FOOTER_LEN as u64 != total {
        return Err(InstallError::InvalidArg(
            "embedded payload bounds mismatch".into(),
        ));
    }
    let mut signature = [0u8; 64];
    signature.copy_from_slice(&footer[16..80]);
    let mut public_key = [0u8; 32];
    public_key.copy_from_slice(&footer[80..112]);

    f.seek(SeekFrom::Start(payload_offset))?;
    let mut pack = vec![0u8; payload_len as usize];
    f.read_exact(&mut pack)?;

    Ok(Some(EmbeddedPayload {
        pack,
        signature,
        public_key,
    }))
}
