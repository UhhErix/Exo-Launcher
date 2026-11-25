use std::collections::HashMap;
use std::path::Path;

use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};

use crate::downloader::{
    DownloadThrottle, InstallOptions, InstallReport, StoreManifest, VerifyReport,
    install_or_update, uninstall_manifest, verify_and_repair,
};
use crate::epic::auth::{EpicExchangeLaunchArgs, build_official_launch_args};
use crate::security::StoreGamePolicy;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpicLibraryItem {
    pub app_name: String,
    pub title: String,
    pub catalog_namespace: String,
    pub catalog_item_id: String,
    pub build_version: String,
    pub optional_tags: Vec<String>,
    pub anti_cheat: AntiCheatClass,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CloudSaveFileMeta {
    pub rel_path: String,
    pub sha256: String,
    pub modified_unix: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CloudSyncAction {
    Upload(String),
    Download(String),
    Conflict {
        rel_path: String,
        local_unix: u64,
        remote_unix: u64,
    },
}

pub fn plan_cloud_save_sync(
    local: &[CloudSaveFileMeta],
    remote: &[CloudSaveFileMeta],
) -> Vec<CloudSyncAction> {
    let local_map: HashMap<&str, &CloudSaveFileMeta> =
        local.iter().map(|f| (f.rel_path.as_str(), f)).collect();
    let remote_map: HashMap<&str, &CloudSaveFileMeta> =
        remote.iter().map(|f| (f.rel_path.as_str(), f)).collect();

    let mut actions = Vec::new();
    for l in local {
        match remote_map.get(l.rel_path.as_str()) {
            None => actions.push(CloudSyncAction::Upload(l.rel_path.clone())),
            Some(r) if l.sha256 == r.sha256 => {}
            Some(r) if l.modified_unix > r.modified_unix => {
                actions.push(CloudSyncAction::Upload(l.rel_path.clone()))
            }
            Some(r) if r.modified_unix > l.modified_unix => {
                actions.push(CloudSyncAction::Download(l.rel_path.clone()))
            }
            Some(r) => actions.push(CloudSyncAction::Conflict {
                rel_path: l.rel_path.clone(),
                local_unix: l.modified_unix,
                remote_unix: r.modified_unix,
            }),
        }
    }
    for r in remote {
        if !local_map.contains_key(r.rel_path.as_str()) {
            actions.push(CloudSyncAction::Download(r.rel_path.clone()));
        }
    }
    actions
}

#[derive(Debug, Default)]
pub struct EpicClient;

impl EpicClient {
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
        item: &EpicLibraryItem,
        executable: &str,
        account_id: &str,
        exchange_code: &str,
    ) -> Result<(EpicExchangeLaunchArgs, StoreGamePolicy), String> {
        let policy = StoreGamePolicy::for_class(item.anti_cheat);
        let args = build_official_launch_args(
            executable,
            &item.app_name,
            &item.catalog_namespace,
            &item.catalog_item_id,
            account_id,
            exchange_code,
        )?;
        Ok((args, policy))
    }
}
