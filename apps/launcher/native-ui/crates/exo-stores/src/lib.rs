pub mod amazon;
pub mod downloader;
pub mod dpapi;
pub mod epic;
pub mod gog;
pub mod hash;
pub mod http;
pub mod security;
pub mod steam;
pub mod webview_fallback;

use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum StoreKind {
    Epic,
    Gog,
    Amazon,
    Steam,
}

impl StoreKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Epic => "epic",
            Self::Gog => "gog",
            Self::Amazon => "amazon",
            Self::Steam => "steam",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoreCatalogEntry {
    pub store: StoreKind,
    pub app_id: String,
    pub title: String,
    pub installed: bool,
    pub build_version: Option<String>,
    pub anti_cheat: AntiCheatClass,
}

#[derive(Debug, Clone, PartialEq)]
pub enum InstallJobEvent {
    RacingCdns {
        store: StoreKind,
        app_id: String,
        candidates: usize,
    },
    SelectedCdn {
        base_url: String,
        throughput_mb_s: f64,
    },
    Progress {
        downloaded_chunks: usize,
        total_chunks: usize,
        bytes_written: u64,
        throughput_mb_s: f64,
    },
    PausedForGame {
        anti_cheat: AntiCheatClass,
    },
    Completed {
        app_id: String,
        bytes_written: u64,
        elapsed_ms: f64,
    },
}

pub trait StoreBackend {
    fn kind(&self) -> StoreKind;
    fn list_catalog(&self) -> Vec<StoreCatalogEntry>;
}

#[cfg(windows)]
pub fn current_process_memory_mb() -> (f64, f64) {
    use std::mem::size_of;

    #[repr(C)]
    struct ProcessMemoryCountersEx {
        cb: u32,
        page_fault_count: u32,
        peak_working_set_size: usize,
        working_set_size: usize,
        quota_peak_paged_pool_usage: usize,
        quota_paged_pool_usage: usize,
        quota_peak_non_paged_pool_usage: usize,
        quota_non_paged_pool_usage: usize,
        pagefile_usage: usize,
        peak_pagefile_usage: usize,
        private_usage: usize,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcess() -> *mut std::ffi::c_void;
    }
    #[link(name = "psapi")]
    unsafe extern "system" {
        fn K32GetProcessMemoryInfo(
            process: *mut std::ffi::c_void,
            ppsmem_counters: *mut ProcessMemoryCountersEx,
            cb: u32,
        ) -> i32;
    }

    let mut counters: ProcessMemoryCountersEx = unsafe { std::mem::zeroed() };
    counters.cb = size_of::<ProcessMemoryCountersEx>() as u32;
    let ok = unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.cb) };
    if ok == 0 {
        return (0.0, 0.0);
    }
    (
        counters.working_set_size as f64 / (1024.0 * 1024.0),
        counters.private_usage as f64 / (1024.0 * 1024.0),
    )
}

#[cfg(not(windows))]
pub fn current_process_memory_mb() -> (f64, f64) {
    (0.0, 0.0)
}

#[cfg(windows)]
pub fn current_process_cpu_ms() -> f64 {
    #[repr(C)]
    #[derive(Default)]
    struct FileTime {
        low: u32,
        high: u32,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcess() -> *mut std::ffi::c_void;
        fn GetProcessTimes(
            h_process: *mut std::ffi::c_void,
            lp_creation_time: *mut FileTime,
            lp_exit_time: *mut FileTime,
            lp_kernel_time: *mut FileTime,
            lp_user_time: *mut FileTime,
        ) -> i32;
    }

    let mut c = FileTime::default();
    let mut e = FileTime::default();
    let mut k = FileTime::default();
    let mut u = FileTime::default();
    if unsafe { GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u) } == 0 {
        return 0.0;
    }
    let to_u64 = |ft: &FileTime| ((ft.high as u64) << 32) | (ft.low as u64);
    (to_u64(&k).saturating_add(to_u64(&u)) as f64) / 10_000.0
}

#[cfg(not(windows))]
pub fn current_process_cpu_ms() -> f64 {
    0.0
}

#[cfg(test)]
mod tests {
    use std::collections::{HashMap, HashSet};
    use std::fs::OpenOptions;
    use std::io::{BufRead, BufReader, Seek, SeekFrom, Write};
    use std::net::TcpListener;
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::thread;

    use exo_rpc::anticheat::AntiCheatClass;
    use flate2::Compression;
    use flate2::write::ZlibEncoder;

    use crate::amazon::auth::AmazonDeviceSession;
    use crate::amazon::client::{
        AmazonClient, AmazonGameItem, decode_fuel_proto_file_entries, parse_fuel_json_manifest,
    };
    use crate::downloader::{
        DownloadThrottle, InstallOptions, install_or_update, plan_delta, uninstall_manifest,
        verify_and_repair,
    };
    use crate::dpapi::MockMemoryVault;
    use crate::epic::auth::{EpicOAuthSession, build_official_launch_args};
    use crate::epic::client::{
        CloudSaveFileMeta, CloudSyncAction, EpicClient, EpicLibraryItem, plan_cloud_save_sync,
    };
    use crate::epic::manifest::{EPIC_BINARY_MANIFEST_MAGIC, parse_epic_manifest};
    use crate::gog::auth::GogOAuthSession;
    use crate::gog::client::{GogClient, GogGameItem};
    use crate::gog::manifest::parse_gog_depot_manifest;
    use crate::hash::{md5, sha1, sha256, to_hex};
    use crate::security::{
        StoreGamePolicy, is_allowed_store_host, validate_manifest_rel_path, validate_store_url,
    };
    use crate::steam::ipc::{
        MockSteamClientIpc, SteamIpcResult, SteamIpcTransport, steam_launch_uri,
    };
    use crate::steam::vdf::{
        build_synthetic_appinfo_shard, build_synthetic_appinfo_v41, parse_app_manifest,
        parse_binary_appinfo, parse_library_folders,
    };
    use crate::webview_fallback::{
        SharedWebProfile, SharedWebSurfaceRequest, StoreExecutionRoute, StoreOperation,
        route_store_operation,
    };

    struct MockHttpServer {
        base_url: String,
        stop: Arc<AtomicBool>,
        handle: Option<thread::JoinHandle<()>>,
    }

    impl MockHttpServer {
        fn start(routes: HashMap<String, Vec<u8>>, fail_all: bool) -> Self {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let addr = listener.local_addr().unwrap();
            let _ = listener.set_nonblocking(true);
            let stop = Arc::new(AtomicBool::new(false));
            let stop_c = Arc::clone(&stop);
            let routes = Arc::new(routes);

            let handle = thread::spawn(move || {
                while !stop_c.load(Ordering::Relaxed) {
                    match listener.accept() {
                        Ok((stream, _)) => {
                            let routes = Arc::clone(&routes);
                            let stop_conn = Arc::clone(&stop_c);
                            thread::spawn(move || {
                                let _ = stream.set_nonblocking(false);
                                let _ = stream.set_nodelay(true);
                                let _ = stream
                                    .set_read_timeout(Some(std::time::Duration::from_millis(500)));
                                let mut reader = BufReader::new(stream);
                                while !stop_conn.load(Ordering::Relaxed) {
                                    let mut req_line = String::new();
                                    let Ok(n) = reader.read_line(&mut req_line) else {
                                        break;
                                    };
                                    if n == 0 {
                                        break;
                                    }
                                    let path = req_line
                                        .split_whitespace()
                                        .nth(1)
                                        .unwrap_or("/")
                                        .to_string();
                                    let mut range: Option<(usize, usize)> = None;
                                    loop {
                                        let mut h = String::new();
                                        let Ok(hn) = reader.read_line(&mut h) else {
                                            return;
                                        };
                                        if hn == 0 || h == "\r\n" || h == "\n" {
                                            break;
                                        }
                                        if let Some(v) = h.strip_prefix("Range: bytes=")
                                            && let Some((a, b)) = v.trim().split_once('-')
                                            && let (Ok(s), Ok(e)) =
                                                (a.parse::<usize>(), b.parse::<usize>())
                                        {
                                            range = Some((s, e));
                                        }
                                    }
                                    let (status, body): (&str, Vec<u8>) = if fail_all {
                                        ("503 Service Unavailable", Vec::new())
                                    } else if let Some(data) = routes.get(&path) {
                                        if let Some((s, e)) = range {
                                            let end = e.min(data.len().saturating_sub(1));
                                            if s <= end && !data.is_empty() {
                                                ("206 Partial Content", data[s..=end].to_vec())
                                            } else {
                                                ("200 OK", data.clone())
                                            }
                                        } else {
                                            ("200 OK", data.clone())
                                        }
                                    } else {
                                        ("404 Not Found", Vec::new())
                                    };
                                    let hdr = format!(
                                        "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: keep-alive\r\n\r\n",
                                        body.len()
                                    );
                                    let sock = reader.get_mut();
                                    if sock.write_all(hdr.as_bytes()).is_err()
                                        || sock.write_all(&body).is_err()
                                    {
                                        break;
                                    }
                                }
                            });
                        }
                        Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                            thread::sleep(std::time::Duration::from_millis(2));
                        }
                        Err(_) => break,
                    }
                }
            });

            Self {
                base_url: format!("http://127.0.0.1:{}", addr.port()),
                stop,
                handle: Some(handle),
            }
        }
    }

    impl Drop for MockHttpServer {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::SeqCst);
            if let Some(h) = self.handle.take() {
                let _ = h.join();
            }
        }
    }

    fn zlib_compress(data: &[u8]) -> Vec<u8> {
        let mut enc = ZlibEncoder::new(Vec::new(), Compression::fast());
        enc.write_all(data).unwrap();
        enc.finish().unwrap()
    }

    fn unique_temp_dir(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!("exo-stores-test-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn hash_vectors_match_nist_and_rfc_standards() {
        assert_eq!(
            to_hex(&sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            to_hex(&sha1(b"abc")),
            "a9993e364706816aba3e25717850c26c9cd0d89d"
        );
        assert_eq!(to_hex(&md5(b"abc")), "900150983cd24fb0d6963f7d28e17f72");
    }

    #[test]
    fn path_traversal_and_security_gate_rejects_escapes_ads_and_disallowed_hosts() {
        let root = unique_temp_dir("sec");
        assert!(validate_manifest_rel_path(&root, "bin/game.exe").is_ok());
        assert!(validate_manifest_rel_path(&root, "../escape.dll").is_err());
        assert!(validate_manifest_rel_path(&root, "bin/../../escape.dll").is_err());
        assert!(validate_manifest_rel_path(&root, "/etc/passwd").is_err());
        assert!(validate_manifest_rel_path(&root, r"\Windows\System32\x.dll").is_err());
        assert!(validate_manifest_rel_path(&root, "C:\\Windows\\win.ini").is_err());
        assert!(validate_manifest_rel_path(&root, "game.exe:ads").is_err());
        assert!(validate_manifest_rel_path(&root, "bad\0name").is_err());
        assert!(validate_manifest_rel_path(&root, "trailingdot.").is_err());

        assert!(is_allowed_store_host(
            "launcher-public-service-prod06.ol.epicgames.com",
            false
        ));
        assert!(is_allowed_store_host("edge1.epicgamescdn.com", false));
        assert!(is_allowed_store_host("api.gog.com", false));
        assert!(is_allowed_store_host("d12345.cloudfront.net", false));
        assert!(!is_allowed_store_host("evil.example.com", false));
        assert!(validate_store_url("https://evil.example.com/chunk", false).is_err());
        assert!(validate_store_url("http://api.gog.com/v1", true).is_err());
        assert!(validate_store_url("http://127.0.0.1:8080/chunk", true).is_ok());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn anticheat_class_policy_treats_unknown_as_strict_and_pauses_downloads() {
        let unknown = StoreGamePolicy::for_class(AntiCheatClass::Unknown);
        assert_eq!(
            unknown.effective_class,
            AntiCheatClass::CompetitivePvpOnline
        );
        assert!(!unknown.allow_launch_wrappers);
        assert!(!unknown.allow_game_dir_mod_writes);
        assert!(unknown.require_official_client_route);
        assert!(unknown.pause_downloads_while_running);

        let offline = StoreGamePolicy::for_class(AntiCheatClass::OfflineAntiCheat);
        assert!(offline.allow_launch_wrappers);
        assert!(offline.allow_game_dir_mod_writes);
        assert!(!offline.pause_downloads_while_running);
    }

    #[test]
    fn shared_lazy_webview2_fallback_only_used_for_sign_in_and_drm_pages() {
        assert_eq!(
            route_store_operation(StoreOperation::LibraryScan, "Epic", None).unwrap(),
            StoreExecutionRoute::NativeRust
        );
        assert_eq!(
            route_store_operation(StoreOperation::CdnRaceAndDownload, "GOG", None).unwrap(),
            StoreExecutionRoute::NativeRust
        );
        assert_eq!(
            route_store_operation(StoreOperation::InteractiveSignIn, "GOG", None).unwrap(),
            StoreExecutionRoute::SharedLazyWebView2(SharedWebSurfaceRequest::GogSignIn)
        );
        let drm = route_store_operation(
            StoreOperation::DrmCheckoutPage,
            "Epic Store",
            Some("https://www.epicgames.com/store/en-US/"),
        )
        .unwrap();
        match drm {
            StoreExecutionRoute::SharedLazyWebView2(req) => {
                assert_eq!(req.profile(), SharedWebProfile::MediaDrm);
                assert!(req.is_ephemeral_on_room_leave());
            }
            StoreExecutionRoute::NativeRust => panic!("expected shared lazy WebView2 route"),
        }
        assert!(
            route_store_operation(
                StoreOperation::DrmCheckoutPage,
                "Evil",
                Some("https://untrusted.example.com/pay")
            )
            .is_err()
        );
    }

    #[test]
    fn vault_round_trips_epic_gog_amazon_sessions_and_speculative_refresh() {
        let mut vault = MockMemoryVault::default();

        let epic = EpicOAuthSession {
            account_id: "acc-1".into(),
            display_name: "Player".into(),
            access_token: "tok-a".into(),
            refresh_token: "ref-a".into(),
            expires_at_unix: 10_200,
        };
        assert!(epic.needs_speculative_refresh(10_000, 300));
        assert!(!epic.needs_speculative_refresh(9_500, 300));
        let sealed = epic.seal_to_vault(&mut vault).unwrap();
        assert_eq!(
            EpicOAuthSession::unseal_from_vault(&vault, "acc-1", &sealed).unwrap(),
            epic
        );

        let gog = GogOAuthSession {
            user_id: "gog-7".into(),
            access_token: "gtok".into(),
            refresh_token: "gref".into(),
            expires_at_unix: 50_000,
        };
        let gsealed = gog.seal_to_vault(&mut vault).unwrap();
        assert_eq!(
            GogOAuthSession::unseal_from_vault(&vault, "gog-7", &gsealed).unwrap(),
            gog
        );

        let amz = AmazonDeviceSession {
            device_serial: "dev-9".into(),
            access_token: "atok".into(),
            refresh_token: "aref".into(),
            expires_at_unix: 80_000,
        };
        let asealed = amz.seal_to_vault(&mut vault).unwrap();
        assert_eq!(
            AmazonDeviceSession::unseal_from_vault(&vault, "dev-9", &asealed).unwrap(),
            amz
        );
        assert_eq!(vault.record_count(), 3);
    }

    #[test]
    fn mock_store_e2e_lifecycle_epic_gog_amazon_interrupt_resume_corrupt_repair_delta_uninstall() {
        let chunk_a = vec![0x41u8; 64 * 1024];
        let chunk_b = vec![0x42u8; 64 * 1024];
        let chunk_c_v2 = vec![0x43u8; 64 * 1024];
        let dlc_chunk = vec![0x44u8; 32 * 1024];

        let mut file1_v1 = Vec::new();
        file1_v1.extend_from_slice(&chunk_a);
        file1_v1.extend_from_slice(&chunk_b);

        let mut file1_v2 = Vec::new();
        file1_v2.extend_from_slice(&chunk_a);
        file1_v2.extend_from_slice(&chunk_c_v2);

        let z_a = zlib_compress(&chunk_a);
        let z_b = zlib_compress(&chunk_b);
        let z_c = zlib_compress(&chunk_c_v2);
        let z_dlc = zlib_compress(&dlc_chunk);

        let mut routes = HashMap::new();
        routes.insert("/chunks/a.bin".into(), z_a.clone());
        routes.insert("/chunks/b.bin".into(), z_b.clone());
        routes.insert("/chunks/c.bin".into(), z_c.clone());
        routes.insert("/chunks/dlc.bin".into(), z_dlc.clone());
        routes.insert("/raw/a.bin".into(), chunk_a.clone());

        let bad_cdn = MockHttpServer::start(HashMap::new(), true);
        let good_cdn = MockHttpServer::start(routes, false);

        let epic_json_v1 = serde_json::json!({
            "app_name": "TestEpicGame",
            "build_version": "1.0.0",
            "launch_exe": "Binaries/Game.exe",
            "cdn_bases": [bad_cdn.base_url, good_cdn.base_url],
            "chunks": [
                {"guid": "cA", "url_path": "chunks/a.bin", "sha256": to_hex(&sha256(&chunk_a)), "compressed_size": z_a.len(), "uncompressed_size": chunk_a.len(), "zlib": true},
                {"guid": "cB", "url_path": "chunks/b.bin", "sha256": to_hex(&sha256(&chunk_b)), "compressed_size": z_b.len(), "uncompressed_size": chunk_b.len(), "zlib": true},
                {"guid": "cDlc", "url_path": "chunks/dlc.bin", "sha256": to_hex(&sha256(&dlc_chunk)), "compressed_size": z_dlc.len(), "uncompressed_size": dlc_chunk.len(), "zlib": true}
            ],
            "files": [
                {
                    "filename": "Binaries/Game.exe",
                    "file_size": file1_v1.len(),
                    "sha256": to_hex(&sha256(&file1_v1)),
                    "install_tags": [],
                    "parts": [
                        {"guid": "cA", "chunk_offset": 0, "file_offset": 0, "size": chunk_a.len()},
                        {"guid": "cB", "chunk_offset": 0, "file_offset": chunk_a.len(), "size": chunk_b.len()}
                    ]
                },
                {
                    "filename": "Content/HiResPack.pak",
                    "file_size": dlc_chunk.len(),
                    "sha256": to_hex(&sha256(&dlc_chunk)),
                    "install_tags": ["hi_res_textures"],
                    "parts": [
                        {"guid": "cDlc", "chunk_offset": 0, "file_offset": 0, "size": dlc_chunk.len()}
                    ]
                }
            ]
        });
        let raw_epic_json = serde_json::to_vec(&epic_json_v1).unwrap();
        let compressed_epic_payload = zlib_compress(&raw_epic_json);
        let mut epic_bin = Vec::new();
        epic_bin.extend_from_slice(&EPIC_BINARY_MANIFEST_MAGIC.to_le_bytes());
        epic_bin.extend_from_slice(&41u32.to_le_bytes());
        epic_bin.extend_from_slice(&(raw_epic_json.len() as u32).to_le_bytes());
        epic_bin.extend_from_slice(&(compressed_epic_payload.len() as u32).to_le_bytes());
        epic_bin.extend_from_slice(&sha1(&raw_epic_json));
        epic_bin.push(0x01);
        epic_bin.extend_from_slice(&18u32.to_le_bytes());
        epic_bin.extend_from_slice(&compressed_epic_payload);

        let epic_manifest_v1 = parse_epic_manifest(&epic_bin).unwrap();
        let epic_root = unique_temp_dir("epic-e2e");
        let mut opts = InstallOptions {
            workers: 2,
            allow_loopback: true,
            ..Default::default()
        };
        opts.excluded_tags.insert("hi_res_textures".into());

        let throttle = DownloadThrottle::default();
        throttle.stop_after_chunks.store(1, Ordering::SeqCst);
        let partial =
            install_or_update(&epic_manifest_v1, &epic_root, &opts, Some(&throttle)).unwrap();
        assert!(partial.paused_or_interrupted);
        assert_eq!(partial.downloaded_chunks, 1);

        let resumed = install_or_update(&epic_manifest_v1, &epic_root, &opts, None).unwrap();
        assert!(!resumed.paused_or_interrupted);
        assert_eq!(resumed.downloaded_chunks, 1);
        assert_eq!(resumed.reused_chunks, 1);
        assert!(!epic_root.join("Content/HiResPack.pak").exists());

        let game_exe = epic_root.join("Binaries/Game.exe");
        {
            let mut f = OpenOptions::new()
                .read(true)
                .write(true)
                .open(&game_exe)
                .unwrap();
            f.seek(SeekFrom::Start(chunk_a.len() as u64 + 17)).unwrap();
            f.write_all(&[0xFF]).unwrap();
        }
        let repaired = verify_and_repair(&epic_manifest_v1, &epic_root, &opts).unwrap();
        assert_eq!(repaired.corrupted_files, vec!["Binaries/Game.exe"]);
        assert_eq!(repaired.repaired_chunks, 1);
        assert!(repaired.all_valid_after_repair);

        let epic_json_v2 = serde_json::json!({
            "app_name": "TestEpicGame",
            "build_version": "2.0.0",
            "launch_exe": "Binaries/Game.exe",
            "cdn_bases": [good_cdn.base_url],
            "chunks": [
                {"guid": "cA", "url_path": "chunks/a.bin", "sha256": to_hex(&sha256(&chunk_a)), "compressed_size": z_a.len(), "uncompressed_size": chunk_a.len(), "zlib": true},
                {"guid": "cC", "url_path": "chunks/c.bin", "sha256": to_hex(&sha256(&chunk_c_v2)), "compressed_size": z_c.len(), "uncompressed_size": chunk_c_v2.len(), "zlib": true}
            ],
            "files": [
                {
                    "filename": "Binaries/Game.exe",
                    "file_size": file1_v2.len(),
                    "sha256": to_hex(&sha256(&file1_v2)),
                    "install_tags": [],
                    "parts": [
                        {"guid": "cA", "chunk_offset": 0, "file_offset": 0, "size": chunk_a.len()},
                        {"guid": "cC", "chunk_offset": 0, "file_offset": chunk_a.len(), "size": chunk_c_v2.len()}
                    ]
                }
            ]
        });
        let epic_manifest_v2 =
            parse_epic_manifest(&serde_json::to_vec(&epic_json_v2).unwrap()).unwrap();
        let delta = plan_delta(&epic_manifest_v2, &epic_root, &HashSet::new()).unwrap();
        assert_eq!(delta.chunks_reused, 1);
        assert_eq!(delta.chunks_to_download, vec!["cC"]);
        let upd = install_or_update(&epic_manifest_v2, &epic_root, &opts, None).unwrap();
        assert_eq!(upd.downloaded_chunks, 1);
        assert_eq!(upd.reused_chunks, 1);

        let removed = uninstall_manifest(&epic_manifest_v2, &epic_root).unwrap();
        assert_eq!(removed, 1);
        assert!(!game_exe.exists());
        let _ = std::fs::remove_dir_all(&epic_root);

        let gog_json = serde_json::json!({
            "product_id": "1207658924",
            "build_id": "b1",
            "cdn_bases": [good_cdn.base_url],
            "files": [{
                "path": "game/data.pak",
                "sha256": to_hex(&sha256(&chunk_a)),
                "chunks": [{
                    "compressed_md5": to_hex(&md5(&z_a)),
                    "md5": to_hex(&md5(&chunk_a)),
                    "compressed_size": z_a.len(),
                    "size": chunk_a.len(),
                    "url_path": "chunks/a.bin",
                    "zlib": true
                }]
            }]
        });
        let gog_zlib = zlib_compress(&serde_json::to_vec(&gog_json).unwrap());
        let gog_manifest = parse_gog_depot_manifest(&gog_zlib).unwrap();
        let gog_root = unique_temp_dir("gog-e2e");
        let gog_client = GogClient;
        let gog_rep = gog_client
            .install(&gog_manifest, &gog_root, &opts, None)
            .unwrap();
        assert_eq!(gog_rep.downloaded_chunks, 1);
        let gog_ver = gog_client
            .verify_and_repair(&gog_manifest, &gog_root, &opts)
            .unwrap();
        assert!(gog_ver.all_valid_after_repair);
        assert_eq!(gog_client.uninstall(&gog_manifest, &gog_root).unwrap(), 1);
        let _ = std::fs::remove_dir_all(&gog_root);

        let amz_json = serde_json::json!({
            "product_id": "amzn1.adg.product.test",
            "build_id": "fuel-1",
            "cdn_bases": [good_cdn.base_url],
            "files": [{
                "path": "bin/app.bin",
                "size": chunk_a.len(),
                "sha256": to_hex(&sha256(&chunk_a)),
                "url_path": "raw/a.bin",
                "zlib": false
            }]
        });
        let amz_manifest =
            parse_fuel_json_manifest(&serde_json::to_vec(&amz_json).unwrap()).unwrap();
        let amz_root = unique_temp_dir("amz-e2e");
        let amz_client = AmazonClient;
        let amz_rep = amz_client
            .install(&amz_manifest, &amz_root, &opts, None)
            .unwrap();
        assert_eq!(amz_rep.downloaded_chunks, 1);
        assert_eq!(amz_client.uninstall(&amz_manifest, &amz_root).unwrap(), 1);
        let _ = std::fs::remove_dir_all(&amz_root);

        let mut proto_entry = Vec::new();
        proto_entry.push(0x0A);
        proto_entry.push(11);
        proto_entry.extend_from_slice(b"bin/app.bin");
        proto_entry.push(0x10);
        proto_entry.push(42);
        proto_entry.push(0x1A);
        proto_entry.push(32);
        proto_entry.extend_from_slice(&sha256(b"fuel"));
        let mut proto_msg = vec![0x0A, proto_entry.len() as u8];
        proto_msg.extend_from_slice(&proto_entry);
        let entries = decode_fuel_proto_file_entries(&proto_msg).unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].path, "bin/app.bin");
        assert_eq!(entries[0].size, 42);
        assert_eq!(entries[0].sha256_hex, to_hex(&sha256(b"fuel")));

        let gog_plan = gog_client
            .plan_launch(
                &GogGameItem {
                    product_id: "1".into(),
                    title: "Cyber".into(),
                    build_id: "1".into(),
                    executable: "game.exe".into(),
                    anti_cheat: AntiCheatClass::OfflineAntiCheat,
                },
                &[],
            )
            .unwrap();
        assert!(gog_plan.policy.allow_launch_wrappers);

        let amz_plan = amz_client
            .plan_launch(
                &AmazonGameItem {
                    product_id: "1".into(),
                    title: "Amz".into(),
                    executable: "app.exe".into(),
                    anti_cheat: AntiCheatClass::Unknown,
                },
                &[],
            )
            .unwrap();
        assert_eq!(
            amz_plan.policy.effective_class,
            AntiCheatClass::CompetitivePvpOnline
        );
    }

    #[test]
    fn steam_zero_copy_vdf_acf_v41_shard_and_mock_ipc_work() {
        let lib_vdf = r#"
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Program Files (x86)\\Steam"
                    "apps"
                    {
                        "730" "45000000000"
                        "1245620" "65000000000"
                    }
                }
            }
        "#;
        let folders = parse_library_folders(lib_vdf).unwrap();
        assert_eq!(folders.len(), 1);
        assert_eq!(folders[0].app_ids, vec![730, 1245620]);

        let acf = r#"
            "AppState"
            {
                "appid" "730"
                "name" "Counter-Strike 2"
                "installdir" "Counter-Strike Global Offensive"
                "StateFlags" "4"
                "SizeOnDisk" "45000000000"
                "buildid" "16000000"
            }
        "#;
        let m = parse_app_manifest(acf).unwrap().unwrap();
        assert_eq!(m.app_id, 730);
        assert_eq!(m.name, "Counter-Strike 2");
        assert_eq!(m.state_flags, 4);

        let v41 = build_synthetic_appinfo_v41(64);
        let recs = parse_binary_appinfo(&v41).unwrap();
        assert_eq!(recs.len(), 64);
        assert_eq!(recs[0].app_id, 1000);
        assert_eq!(recs[0].app_type, "Game");

        let shard = build_synthetic_appinfo_shard(32);
        let srecs = parse_binary_appinfo(&shard).unwrap();
        assert_eq!(srecs.len(), 32);
        assert_eq!(srecs[0].app_id, 5000);

        let mut ipc = MockSteamClientIpc::default();
        assert_eq!(ipc.install_or_update(730), SteamIpcResult::Ok);
        assert_eq!(ipc.install_state(730), Some(4));
        assert_eq!(ipc.uninstall(730), SteamIpcResult::Ok);
        assert_eq!(ipc.install_state(730), Some(0));

        let (uri, pol) = steam_launch_uri(730, AntiCheatClass::Unknown);
        assert_eq!(uri, "steam://rungameid/730");
        assert_eq!(pol.effective_class, AntiCheatClass::CompetitivePvpOnline);
    }

    #[test]
    fn epic_cloud_save_sync_and_official_exchange_launch_enforce_valid_token() {
        let local = vec![
            CloudSaveFileMeta {
                rel_path: "slot1.sav".into(),
                sha256: "aaa".into(),
                modified_unix: 200,
            },
            CloudSaveFileMeta {
                rel_path: "slot2.sav".into(),
                sha256: "bbb".into(),
                modified_unix: 100,
            },
        ];
        let remote = vec![
            CloudSaveFileMeta {
                rel_path: "slot1.sav".into(),
                sha256: "old".into(),
                modified_unix: 150,
            },
            CloudSaveFileMeta {
                rel_path: "slot2.sav".into(),
                sha256: "new".into(),
                modified_unix: 180,
            },
        ];
        let plan = plan_cloud_save_sync(&local, &remote);
        assert!(plan.contains(&CloudSyncAction::Upload("slot1.sav".into())));
        assert!(plan.contains(&CloudSyncAction::Download("slot2.sav".into())));

        assert!(build_official_launch_args("game.exe", "App", "ns", "id", "u1", "").is_err());
        let epic = EpicClient;
        let item = EpicLibraryItem {
            app_name: "App".into(),
            title: "Test".into(),
            catalog_namespace: "ns".into(),
            catalog_item_id: "id".into(),
            build_version: "1".into(),
            optional_tags: Vec::new(),
            anti_cheat: AntiCheatClass::Unknown,
        };
        let (launch, pol) = epic
            .plan_launch(&item, "Bin/Game.exe", "u1", "exch-123")
            .unwrap();
        assert!(launch.args.iter().any(|a| a == "-AUTH_TYPE=exchangecode"));
        assert_eq!(pol.effective_class, AntiCheatClass::CompetitivePvpOnline);
    }
}
