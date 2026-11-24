use std::path::Path;

use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};

use crate::downloader::{
    DownloadThrottle, InstallOptions, InstallReport, StoreManifest, VerifyReport,
    install_or_update, uninstall_manifest, verify_and_repair,
};
use crate::security::StoreGamePolicy;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GogGameItem {
    pub product_id: String,
    pub title: String,
    pub build_id: String,
    pub executable: String,
    pub anti_cheat: AntiCheatClass,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GogAchievement {
    pub id: String,
    pub name: String,
    pub unlocked: bool,
    pub unlocked_at_unix: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GogLaunchPlan {
    pub executable: String,
    pub args: Vec<String>,
    pub policy: StoreGamePolicy,
}

#[derive(Debug, Default)]
pub struct GogClient;

impl GogClient {
    pub fn install(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
        opts: &InstallOptions,
        throttle: Option<&DownloadThrottle>,
    ) -> Result<InstallReport, String> {
        install_or_update(manifest, install_root, opts, throttle)
    }

    pub fn verify_and_repair(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
        opts: &InstallOptions,
    ) -> Result<VerifyReport, String> {
        verify_and_repair(manifest, install_root, opts)
    }

    pub fn uninstall(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
    ) -> Result<usize, String> {
        uninstall_manifest(manifest, install_root)
    }

    pub fn plan_launch(
        &self,
        item: &GogGameItem,
        extra_args: &[String],
    ) -> Result<GogLaunchPlan, String> {
        if item.executable.trim().is_empty() {
            return Err("GOG launch executable is empty".into());
        }
        let policy = StoreGamePolicy::for_class(item.anti_cheat);
        Ok(GogLaunchPlan {
            executable: item.executable.clone(),
            args: extra_args.to_vec(),
            policy,
        })
    }
}
