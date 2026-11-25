use crate::verify::{DllVerification, Vendor, VerifyError, verify_vendor_dll};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

pub const MANIFEST_SCHEMA_VERSION: u32 = 1;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum UpscalerKind {
    DlssSr,
    DlssRr,
    DlssFg,
    Fsr3,
    Xess,
    XessFg,
}

impl UpscalerKind {
    pub const fn vendor(self) -> Vendor {
        match self {
            Self::DlssSr | Self::DlssRr | Self::DlssFg => Vendor::Nvidia,
            Self::Fsr3 => Vendor::Amd,
            Self::Xess | Self::XessFg => Vendor::Intel,
        }
    }

    pub const fn upstream_repo(self) -> &'static str {
        match self {
            Self::DlssSr | Self::DlssRr | Self::DlssFg => "NVIDIA/DLSS",
            Self::Fsr3 => "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK",
            Self::Xess | Self::XessFg => "intel/xess",
        }
    }

    pub fn from_file_name(name: &str) -> Option<Self> {
        let lower = name.to_ascii_lowercase();
        match lower.as_str() {
            "nvngx_dlss.dll" => Some(Self::DlssSr),
            "nvngx_dlssd.dll" => Some(Self::DlssRr),
            "nvngx_dlssg.dll" => Some(Self::DlssFg),
            "amd_fidelityfx_dx12.dll"
            | "amd_fidelityfx_vk.dll"
            | "amd_fidelityfx_upscaler_dx12.dll"
            | "amd_fidelityfx_framegeneration_dx12.dll"
            | "ffx_fsr2_api_x64.dll"
            | "ffx_fsr2_api_dx12_x64.dll" => Some(Self::Fsr3),
            "libxess.dll" | "libxess_dx11.dll" => Some(Self::Xess),
            "libxess_fg.dll" => Some(Self::XessFg),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpstreamReleaseSource {
    pub vendor: Vendor,
    pub repo: String,
    pub api_url: String,
}

pub fn official_upstream_sources() -> Vec<UpstreamReleaseSource> {
    vec![
        UpstreamReleaseSource {
            vendor: Vendor::Nvidia,
            repo: "NVIDIA/DLSS".into(),
            api_url: "https://api.github.com/repos/NVIDIA/DLSS/releases".into(),
        },
        UpstreamReleaseSource {
            vendor: Vendor::Amd,
            repo: "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK".into(),
            api_url:
                "https://api.github.com/repos/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases"
                    .into(),
        },
        UpstreamReleaseSource {
            vendor: Vendor::Intel,
            repo: "intel/xess".into(),
            api_url: "https://api.github.com/repos/intel/xess/releases".into(),
        },
    ]
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogEntry {
    pub kind: UpscalerKind,
    pub vendor: Vendor,
    pub file_name: String,
    pub version: String,
    pub size_bytes: u64,
    pub blake3: String,
    pub sha256: String,
    pub publisher: String,
    pub source_repo: String,
    pub release_tag: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VaultManifest {
    pub schema_version: u32,
    pub generated_unix_secs: u64,
    pub entries: Vec<CatalogEntry>,
    pub manifest_blake3: String,
}

impl VaultManifest {
    pub fn build(mut entries: Vec<CatalogEntry>) -> Self {
        entries.sort_by(|a, b| {
            a.kind
                .cmp(&b.kind)
                .then_with(|| a.file_name.cmp(&b.file_name))
                .then_with(|| a.version.cmp(&b.version))
                .then_with(|| a.blake3.cmp(&b.blake3))
        });
        let generated_unix_secs = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let manifest_blake3 = compute_entries_digest(&entries);
        Self {
            schema_version: MANIFEST_SCHEMA_VERSION,
            generated_unix_secs,
            entries,
            manifest_blake3,
        }
    }

    pub fn verify_integrity(&self) -> bool {
        if self.schema_version != MANIFEST_SCHEMA_VERSION {
            return false;
        }
        let expected = compute_entries_digest(&self.entries);
        self.manifest_blake3.eq_ignore_ascii_case(&expected)
    }
}

fn compute_entries_digest(entries: &[CatalogEntry]) -> String {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"exo-upscale-manifest-v1\n");
    for e in entries {
        let line = format!(
            "{:?}|{:?}|{}|{}|{}|{}|{}|{}|{}|{}\n",
            e.kind,
            e.vendor,
            e.file_name,
            e.version,
            e.size_bytes,
            e.blake3,
            e.sha256,
            e.publisher,
            e.source_repo,
            e.release_tag
        );
        hasher.update(line.as_bytes());
    }
    hasher.finalize().to_hex().to_string()
}

pub struct VaultStore {
    root: PathBuf,
}

impl VaultStore {
    pub fn new(root: impl Into<PathBuf>) -> Result<Self, String> {
        let root = root.into();
        fs::create_dir_all(root.join("blobs")).map_err(|e| format!("create blobs dir: {e}"))?;
        fs::create_dir_all(root.join("backups")).map_err(|e| format!("create backups dir: {e}"))?;
        Ok(Self { root })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn manifest_path(&self) -> PathBuf {
        self.root.join("manifest.json")
    }

    pub fn blob_path(&self, blake3: &str, file_name: &str) -> PathBuf {
        self.root.join("blobs").join(blake3).join(file_name)
    }

    pub fn load_manifest(&self) -> Result<VaultManifest, String> {
        let p = self.manifest_path();
        if !p.exists() {
            return Ok(VaultManifest::build(Vec::new()));
        }
        let raw = fs::read_to_string(&p).map_err(|e| format!("read manifest: {e}"))?;
        let manifest: VaultManifest =
            serde_json::from_str(&raw).map_err(|e| format!("parse manifest: {e}"))?;
        if !manifest.verify_integrity() {
            return Err("vault manifest failed BLAKE3 integrity verification".into());
        }
        Ok(manifest)
    }

    pub fn write_manifest(&self, manifest: &VaultManifest) -> Result<(), String> {
        if !manifest.verify_integrity() {
            return Err("refusing to write manifest with invalid digest".into());
        }
        let json = serde_json::to_string_pretty(manifest)
            .map_err(|e| format!("serialize manifest: {e}"))?;
        let tmp = self.root.join("manifest.json.tmp");
        fs::write(&tmp, json.as_bytes()).map_err(|e| format!("write temp manifest: {e}"))?;
        atomic_replace_file(&tmp, &self.manifest_path())
    }

    pub fn ingest_verified_dll(
        &self,
        source_path: &Path,
        kind: UpscalerKind,
        release_tag: &str,
    ) -> Result<CatalogEntry, VerifyError> {
        let vendor = kind.vendor();
        let info: DllVerification = verify_vendor_dll(source_path, vendor, None)?;
        let file_name = source_path
            .file_name()
            .and_then(|n| n.to_str())
            .ok_or_else(|| VerifyError::Io("invalid dll file name".into()))?
            .to_string();
        let dst = self.blob_path(&info.blake3, &file_name);
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent).map_err(|e| VerifyError::Io(e.to_string()))?;
        }
        if !dst.exists() {
            let tmp = dst.with_extension("dll.tmp");
            fs::copy(source_path, &tmp).map_err(|e| VerifyError::Io(e.to_string()))?;
            atomic_replace_file(&tmp, &dst).map_err(VerifyError::Io)?;
        }
        let entry = CatalogEntry {
            kind,
            vendor,
            file_name,
            version: info.file_version.unwrap_or_else(|| "0.0.0.0".into()),
            size_bytes: info.size_bytes,
            blake3: info.blake3,
            sha256: info.sha256,
            publisher: info
                .publisher
                .unwrap_or_else(|| vendor.expected_publisher().to_string()),
            source_repo: kind.upstream_repo().to_string(),
            release_tag: release_tag.to_string(),
        };
        let mut manifest = self.load_manifest().map_err(VerifyError::Io)?;
        manifest.entries.retain(|e| e.blake3 != entry.blake3);
        manifest.entries.push(entry.clone());
        let rebuilt = VaultManifest::build(manifest.entries);
        self.write_manifest(&rebuilt).map_err(VerifyError::Io)?;
        Ok(entry)
    }
}

pub fn generate_local_manifest(
    scan_dir: &Path,
    out_manifest: &Path,
    release_tag: &str,
) -> Result<VaultManifest, String> {
    let mut entries = Vec::new();
    collect_verified_dlls(scan_dir, release_tag, &mut entries)?;
    let manifest = VaultManifest::build(entries);
    if let Some(parent) = out_manifest.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("create manifest parent: {e}"))?;
    }
    let json =
        serde_json::to_string_pretty(&manifest).map_err(|e| format!("serialize manifest: {e}"))?;
    let tmp = out_manifest.with_extension("json.tmp");
    fs::write(&tmp, json.as_bytes()).map_err(|e| format!("write temp manifest: {e}"))?;
    atomic_replace_file(&tmp, out_manifest)?;
    Ok(manifest)
}

fn collect_verified_dlls(
    dir: &Path,
    release_tag: &str,
    out: &mut Vec<CatalogEntry>,
) -> Result<(), String> {
    if !dir.exists() {
        return Ok(());
    }
    let rd = fs::read_dir(dir).map_err(|e| format!("read_dir {}: {e}", dir.display()))?;
    for item in rd.flatten() {
        let path = item.path();
        if path.is_dir() {
            collect_verified_dlls(&path, release_tag, out)?;
        } else if let Some(name) = path.file_name().and_then(|n| n.to_str())
            && let Some(kind) = UpscalerKind::from_file_name(name)
            && let Ok(info) = verify_vendor_dll(&path, kind.vendor(), None)
        {
            out.push(CatalogEntry {
                kind,
                vendor: kind.vendor(),
                file_name: name.to_string(),
                version: info.file_version.unwrap_or_else(|| "0.0.0.0".into()),
                size_bytes: info.size_bytes,
                blake3: info.blake3,
                sha256: info.sha256,
                publisher: info
                    .publisher
                    .unwrap_or_else(|| kind.vendor().expected_publisher().to_string()),
                source_repo: kind.upstream_repo().to_string(),
                release_tag: release_tag.to_string(),
            });
        }
    }
    Ok(())
}

pub(crate) fn atomic_replace_file(src: &Path, dst: &Path) -> Result<(), String> {
    #[cfg(windows)]
    {
        use windows::Win32::Storage::FileSystem::{
            MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
        };
        use windows::core::{HSTRING, PCWSTR};

        let s = HSTRING::from(src.as_os_str());
        let d = HSTRING::from(dst.as_os_str());
        unsafe {
            MoveFileExW(
                PCWSTR::from_raw(s.as_ptr()),
                PCWSTR::from_raw(d.as_ptr()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
            .map_err(|e| format!("MoveFileExW {} -> {}: {e}", src.display(), dst.display()))?;
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        fs::rename(src, dst)
            .map_err(|e| format!("rename {} -> {}: {e}", src.display(), dst.display()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn upstream_sources_are_official_vendor_repos_only_never_third_party_archives() {
        let sources = official_upstream_sources();
        assert_eq!(sources.len(), 3);
        for s in &sources {
            assert!(s.api_url.starts_with("https://api.github.com/repos/"));
            assert!(!s.api_url.contains("beeradmoore"));
            assert!(!s.api_url.contains("dlss-swapper"));
        }
    }

    #[test]
    fn manifest_integrity_detects_tampering() {
        let entry = CatalogEntry {
            kind: UpscalerKind::DlssSr,
            vendor: Vendor::Nvidia,
            file_name: "nvngx_dlss.dll".into(),
            version: "310.2.1.0".into(),
            size_bytes: 48_000_000,
            blake3: "a".repeat(64),
            sha256: "b".repeat(64),
            publisher: "NVIDIA Corporation".into(),
            source_repo: "NVIDIA/DLSS".into(),
            release_tag: "v310.2.1".into(),
        };
        let mut manifest = VaultManifest::build(vec![entry]);
        assert!(manifest.verify_integrity());

        manifest.entries[0].version = "310.9.9.9".into();
        assert!(!manifest.verify_integrity());
    }

    #[test]
    fn local_manifest_generator_writes_and_validates_locally() {
        let root = std::env::temp_dir().join(format!("exo-upscale-cat-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let scan_dir = root.join("sdk-local");
        fs::create_dir_all(&scan_dir).unwrap();
        let out_manifest = root.join("local-manifest.json");

        let manifest = generate_local_manifest(&scan_dir, &out_manifest, "local-test").unwrap();
        assert!(manifest.verify_integrity());
        assert!(out_manifest.exists());

        let store = VaultStore::new(root.join("vault")).unwrap();
        store.write_manifest(&manifest).unwrap();
        let reloaded = store.load_manifest().unwrap();
        assert_eq!(reloaded.manifest_blake3, manifest.manifest_blake3);

        let _ = fs::remove_dir_all(&root);
    }
}
