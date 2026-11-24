use serde::{Deserialize, Serialize};
use std::fs::File;
use std::io::Read;
use std::path::Path;

const FOSSILIZE_MAGIC: &[u8; 10] = b"\x81FOSSILIZE";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FossilizeArchiveInfo {
    pub path: String,
    pub format_version: u8,
    pub file_bytes: u64,
    pub valid: bool,
}

pub fn inspect_fossilize_archive(path: &Path) -> Option<FossilizeArchiveInfo> {
    let mut file = File::open(path).ok()?;
    let file_bytes = file.metadata().ok()?.len();
    if file_bytes < 16 {
        return None;
    }
    let mut header = [0u8; 16];
    file.read_exact(&mut header).ok()?;
    let valid = &header[0..10] == FOSSILIZE_MAGIC;
    Some(FossilizeArchiveInfo {
        path: path.display().to_string(),
        format_version: header[15],
        file_bytes,
        valid,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_fossilize_magic() {
        let p = std::env::temp_dir().join(format!("exo-test-{}.foz", std::process::id()));
        let mut buf = [0u8; 32];
        buf[0..10].copy_from_slice(FOSSILIZE_MAGIC);
        buf[15] = 6;
        std::fs::write(&p, buf).unwrap();
        let info = inspect_fossilize_archive(&p).unwrap();
        assert!(info.valid);
        assert_eq!(info.format_version, 6);
        let _ = std::fs::remove_file(&p);
    }
}
