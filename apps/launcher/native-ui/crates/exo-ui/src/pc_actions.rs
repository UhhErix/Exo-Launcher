use crate::pc_state::PcTab;
use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, AppsIconsReply, AppsListReply, AudioReadReply, AutopilotState, DevicesImageReply,
    DevicesListReply, DrivesReply, EqStateReply, ExoAccountState, ExoAiState, HdrState,
    KeyboardBackupsReply, KeyboardReadReply, LauncherSettings, METHOD_APP_CHECK_UPDATE,
    METHOD_APP_INSTALL_UPDATE, METHOD_APP_OPEN_LOGS, METHOD_APP_REPAIR_CACHE, METHOD_APPS_ICONS,
    METHOD_APPS_LIST, METHOD_APPS_TIDY, METHOD_APPS_UNINSTALL, METHOD_AUDIO_READ,
    METHOD_AUDIO_SET_MUTE, METHOD_AUDIO_SET_VOLUME, METHOD_AUTOPILOT_STATE, METHOD_DEVICES_IMAGE,
    METHOD_DEVICES_LIST, METHOD_EQ, METHOD_EXO_ACCOUNT_SIGN_OUT, METHOD_EXO_ACCOUNT_STATE,
    METHOD_EXO_ACCOUNT_SYNC, METHOD_EXO_AI_SET, METHOD_EXO_AI_STATE, METHOD_HDR_STATE,
    METHOD_JUNK_CLEAN, METHOD_KEYBOARD_BACKUP, METHOD_KEYBOARD_BACKUPS, METHOD_KEYBOARD_OPEN_APP,
    METHOD_KEYBOARD_PROFILE, METHOD_KEYBOARD_READ, METHOD_KEYBOARD_RESTORE,
    METHOD_KEYBOARD_SETTINGS, METHOD_PLAYTIME_RIOT_REFRESH, METHOD_SETTINGS_SET,
    METHOD_SHADERS_ADDON, METHOD_SHADERS_ESTIMATES, METHOD_SHADERS_PREPARE_GAMES,
    METHOD_SHADERS_PREPARE_START, METHOD_STARTUP_CLEAN, METHOD_STARTUP_LIST, METHOD_STARTUP_REMOVE,
    METHOD_STARTUP_RESTORE, METHOD_SYSTEM_ABOUT, METHOD_SYSTEM_DRIVES, METHOD_SYSTEM_TUNING,
    ShadersAddonStatus, ShadersEstimatesReply, ShadersPrepareGamesReply, StartupListReply,
    SystemAbout, SystemTuningState, UpdateCheckReply,
};
use serde_json::{Map, Value, json};

impl AppState {
    pub fn load_settings_room(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, ExoAccountState>(METHOD_EXO_ACCOUNT_STATE, &json!({}), move |res| {
                if let Ok(acct) = res {
                    h1.update(move |s, _| s.pc.account = acct);
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, HdrState>(METHOD_HDR_STATE, &json!({}), move |res| {
                if let Ok(hdr) = res {
                    h2.update(move |s, _| s.pc.hdr = Some(hdr));
                }
            });
            let h3 = handle.clone();
            rpc.call::<_, SystemAbout>(METHOD_SYSTEM_ABOUT, &json!({}), move |res| {
                if let Ok(about) = res {
                    h3.update(move |s, _| s.pc.system_about = Some(about));
                }
            });
            if self.pc.update_check.is_none() && !self.pc.checking_update {
                self.check_app_update();
            }
        }
    }

    pub fn set_bool_setting(&mut self, key: &str, value: bool) {
        match key {
            "reduceMotion" => self.settings.reduce_motion = value,
            "startWithWindows" => self.settings.start_with_windows = value,
            "startMaximized" => self.settings.start_maximized = value,
            "showLibraryTools" => self.settings.show_library_tools = value,
            "pauseDownloadsWhilePlaying" => self.settings.pause_downloads_while_playing = value,
            "keepAwakeDownloads" => self.settings.keep_awake_downloads = value,
            "autoUpdateGames" => self.settings.auto_update_games = value,
            "storeClientsQuiet" => self.settings.store_clients_quiet = value,
            "achievementBanners" => self.settings.achievement_banners = value,
            "fpsCounter" => self.settings.fps_counter = value,
            "hdrForGames" => self.settings.hdr_for_games = value,
            "autoUpdateUpscalers" => self.settings.auto_update_upscalers = value,
            "autoUpdateChipset" => self.settings.auto_update_chipset = value,
            _ => {}
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            map.insert(key.to_string(), Value::Bool(value));
            rpc.call::<_, LauncherSettings>(METHOD_SETTINGS_SET, &Value::Object(map), move |res| {
                if let Ok(set) = res {
                    handle.update(move |s, _| s.settings = set);
                }
            });
        }
    }

    pub fn set_string_setting(&mut self, key: &str, value: &str) {
        if key == "fpsCounterPosition" {
            self.settings.fps_counter_position = Some(value.to_string());
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            map.insert(key.to_string(), Value::String(value.to_string()));
            rpc.call::<_, LauncherSettings>(METHOD_SETTINGS_SET, &Value::Object(map), move |res| {
                if let Ok(set) = res {
                    handle.update(move |s, _| s.settings = set);
                }
            });
        }
    }

    pub fn check_app_update(&mut self) {
        if self.status == HostStatus::Standalone {
            self.notice = Some("Exo Launcher is up to date.".into());
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.checking_update = true;
            rpc.call::<_, UpdateCheckReply>(
                METHOD_APP_CHECK_UPDATE,
                &json!({ "manual": true }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.checking_update = false;
                        match res {
                            Ok(reply) => {
                                s.notice = reply.message.clone();
                                s.pc.update_check = Some(reply);
                            }
                            Err(e) => s.notice = Some(format!("Update check failed: {e}")),
                        }
                    });
                },
            );
        }
    }

    pub fn install_app_update(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.installing_update = true;
            rpc.call::<_, UpdateCheckReply>(METHOD_APP_INSTALL_UPDATE, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.pc.installing_update = false;
                    match res {
                        Ok(reply) => {
                            s.notice = reply.message.clone();
                            s.pc.update_check = Some(reply);
                        }
                        Err(e) => s.notice = Some(format!("Update failed: {e}")),
                    }
                });
            });
        }
    }

    pub fn open_app_logs(&mut self) {
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_APP_OPEN_LOGS, &json!({}), |_| {});
        }
    }

    pub fn repair_app_cache(&mut self) {
        self.notice = Some("Repairing cover cache…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_APP_REPAIR_CACHE, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(r) => s.notice = r.message.or_else(|| Some("Cache repaired.".into())),
                    Err(e) => s.notice = Some(format!("Repair failed: {e}")),
                });
            });
        }
    }

    pub fn sync_exo_account(&mut self) {
        self.notice = Some("Syncing Exo account…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call_raw(METHOD_EXO_ACCOUNT_SYNC, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.notice = Some(match res {
                        Ok(_) => "Account synced.".into(),
                        Err(e) => format!("Sync failed: {e}"),
                    });
                });
            });
        }
    }

    pub fn sign_out_exo_account(&mut self) {
        self.pc.account.signed_in = false;
        self.pc.account.email = None;
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_EXO_ACCOUNT_SIGN_OUT, &json!({}), |_| {});
        }
    }

    pub fn refresh_riot_playtime(&mut self) {
        self.notice = Some("Refreshing Riot playtime…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call_raw(METHOD_PLAYTIME_RIOT_REFRESH, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.notice = Some(match res {
                        Ok(_) => "Riot playtime refreshed.".into(),
                        Err(e) => format!("Riot refresh failed: {e}"),
                    });
                });
            });
        }
    }

    pub fn switch_pc_tab(&mut self, tab: PcTab) {
        self.pc.pc_tab = tab;
        self.load_pc_tab(tab);
    }

    pub fn load_pc_tab(&mut self, tab: PcTab) {
        if self.status == HostStatus::Standalone {
            return;
        }
        match tab {
            PcTab::ExoAi => self.load_pc_exo_ai(),
            PcTab::Apps => self.load_pc_apps(),
            PcTab::Storage => self.load_pc_drives(),
            PcTab::Devices => self.load_pc_devices(),
        }
    }

    pub fn load_pc_exo_ai(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, ExoAiState>(METHOD_EXO_AI_STATE, &json!({}), move |res| {
                if let Ok(ai) = res {
                    h1.update(move |s, _| s.pc.exo_ai = Some(ai));
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, AutopilotState>(METHOD_AUTOPILOT_STATE, &json!({}), move |res| {
                if let Ok(ap) = res {
                    h2.update(move |s, _| s.pc.autopilot = Some(ap));
                }
            });
            let h3 = handle.clone();
            rpc.call::<_, SystemTuningState>(METHOD_SYSTEM_TUNING, &json!({}), move |res| {
                if let Ok(t) = res {
                    h3.update(move |s, _| s.pc.tuning = Some(t));
                }
            });
            let h4 = handle.clone();
            rpc.call::<_, HdrState>(METHOD_HDR_STATE, &json!({}), move |res| {
                if let Ok(hdr) = res {
                    h4.update(move |s, _| s.pc.hdr = Some(hdr));
                }
            });
            let h5 = handle.clone();
            rpc.call::<_, ShadersAddonStatus>(METHOD_SHADERS_ADDON, &json!({}), move |res| {
                if let Ok(sh) = res {
                    h5.update(move |s, _| {
                        if let Some(ref snap) = sh.snapshot {
                            s.pc.shader_snap_games = snap.games.clone();
                        }
                        s.pc.shaders = Some(sh);
                    });
                }
            });
            let h5b = handle.clone();
            rpc.call::<_, ShadersEstimatesReply>(METHOD_SHADERS_ESTIMATES, &json!({}), move |res| {
                if let Ok(est) = res
                    && est.ok
                {
                    h5b.update(move |s, _| s.pc.shader_estimates = est.seconds);
                }
            });
            let h6 = handle.clone();
            rpc.call::<_, ShadersPrepareGamesReply>(
                METHOD_SHADERS_PREPARE_GAMES,
                &json!({}),
                move |res| {
                    if let Ok(reply) = res {
                        h6.update(move |s, _| s.pc.shader_games = reply.games);
                    }
                },
            );
            let h7 = handle.clone();
            rpc.call::<_, SystemAbout>(METHOD_SYSTEM_ABOUT, &json!({}), move |res| {
                if let Ok(about) = res {
                    h7.update(move |s, _| s.pc.system_about = Some(about));
                }
            });
            if self.pc.drives.is_none() {
                self.load_pc_drives();
            }
        }
    }

    pub fn compile_shaders_all(&mut self) {
        let ids: Vec<String> = self
            .pc
            .shader_games
            .iter()
            .filter(|g| g.state != "prepared" && g.state != "running")
            .map(|g| g.id.clone())
            .collect();
        if let Some(first) = ids.first() {
            self.notice = Some("Starting shader pre-compilation…".into());
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                rpc.call::<_, ActionResult>(
                    METHOD_SHADERS_PREPARE_START,
                    &json!({ "id": first }),
                    move |res| {
                        handle.update(move |s, _| {
                            if let Ok(r) = res {
                                s.notice = r.message.or_else(|| Some("Shader compile started.".into()));
                            }
                        });
                    },
                );
            }
        }
    }

    pub fn set_exo_ai_bool(&mut self, field: &str, value: bool) {
        if let Some(ref mut ai) = self.pc.exo_ai {
            match field {
                "autoApplyBestSettings" => ai.auto_apply_best_settings = value,
                "backgroundDriftCheck" => ai.background_drift_check = value,
                _ => {}
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            map.insert(field.to_string(), Value::Bool(value));
            rpc.call::<_, ExoAiState>(METHOD_EXO_AI_SET, &Value::Object(map), move |res| {
                if let Ok(ai) = res {
                    handle.update(move |s, _| s.pc.exo_ai = Some(ai));
                }
            });
        }
    }

    pub fn set_exo_ai_fan_preset(&mut self, preset: &str) {
        if let Some(ref mut ai) = self.pc.exo_ai {
            ai.fans.preset = preset.to_string();
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAiState>(
                METHOD_EXO_AI_SET,
                &json!({ "fanPreset": preset }),
                move |res| {
                    if let Ok(ai) = res {
                        handle.update(move |s, _| s.pc.exo_ai = Some(ai));
                    }
                },
            );
        }
    }

    pub fn run_exo_ai_now(&mut self) {
        self.notice = Some("Running Exo AI optimization pass…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAiState>(METHOD_EXO_AI_SET, &json!({ "runNow": true }), move |res| {
                handle.update(move |s, _| match res {
                    Ok(ai) => {
                        s.notice = Some(format!(
                            "Exo AI ready: {} optimizations active.",
                            ai.auto_applied.len()
                        ));
                        s.pc.exo_ai = Some(ai);
                    }
                    Err(e) => s.notice = Some(format!("Exo AI failed: {e}")),
                });
            });
        }
    }

    pub fn run_latency_check(&mut self) {
        if self.pc.latency_running {
            return;
        }
        self.pc.latency_running = true;
        self.notice = Some("Running 10-second kernel DPC/ISR latency check…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SystemTuningState>(
                METHOD_SYSTEM_TUNING,
                &json!({ "action": "latency" }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.latency_running = false;
                        match res {
                            Ok(t) => {
                                s.notice = t.message.clone().or_else(|| Some("Latency check complete.".into()));
                                s.pc.tuning = Some(t);
                            }
                            Err(e) => s.notice = Some(format!("Latency check failed: {e}")),
                        }
                    });
                },
            );
        }
    }

    pub fn toggle_tuning_item(&mut self, key: &str, turn_on: bool) {
        if let Some(ref mut t) = self.pc.tuning
            && let Some(item) = t.items.iter_mut().find(|i| i.key == key)
        {
            item.on = turn_on;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let action = if turn_on { "on" } else { "off" };
            rpc.call::<_, SystemTuningState>(
                METHOD_SYSTEM_TUNING,
                &json!({ "action": action, "item": key }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(t) => {
                            if let Some(ref m) = t.message {
                                s.notice = Some(m.clone());
                            }
                            s.pc.tuning = Some(t);
                        }
                        Err(e) => s.notice = Some(format!("Tuning failed: {e}")),
                    });
                },
            );
        }
    }

    pub fn load_pc_apps(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.apps_loading = true;
            let h1 = handle.clone();
            let rpc_icons = rpc.clone();
            rpc.call::<_, AppsListReply>(METHOD_APPS_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    let mut by_size: Vec<&exo_rpc::InstalledAppItem> = list.programs.iter().collect();
                    by_size.sort_by(|a, b| b.bytes.unwrap_or(0).cmp(&a.bytes.unwrap_or(0)));
                    let all_ids: Vec<String> = by_size.into_iter().map(|p| p.id.clone()).collect();
                    let h_ic = h1.clone();
                    h1.update(move |s, _| {
                        s.pc.apps_loading = false;
                        s.pc.apps_list = Some(list);
                    });
                    for chunk in all_ids.chunks(100) {
                        let chunk_vec: Vec<String> = chunk.to_vec();
                        let h_c = h_ic.clone();
                        rpc_icons.call::<_, AppsIconsReply>(
                            METHOD_APPS_ICONS,
                            &json!({ "ids": chunk_vec }),
                            move |ic_res| {
                                if let Ok(reply) = ic_res {
                                    h_c.update(move |s, _| {
                                        for (k, v) in reply.icons {
                                            if let Some(data_url) = v {
                                                s.pc.app_icons.insert(k, data_url);
                                            }
                                        }
                                    });
                                }
                            },
                        );
                    }
                } else {
                    h1.update(|s, _| s.pc.apps_loading = false);
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, StartupListReply>(METHOD_STARTUP_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    h2.update(move |s, _| s.pc.startup_list = Some(list));
                }
            });
        }
    }

    pub fn uninstall_app(&mut self, id: &str) {
        self.notice = Some("Starting application uninstaller…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_APPS_UNINSTALL, &json!({ "id": id }), move |res| {
                handle.update(move |s, _| {
                    if let Ok(r) = res {
                        s.notice = r.message;
                    }
                    s.load_pc_apps();
                });
            });
        }
    }

    pub fn tidy_app_leftovers(&mut self) {
        self.notice = Some("Scanning leftover app files…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_APPS_TIDY, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.notice = match res {
                        Ok(r) => r.message.or_else(|| Some("Leftover check complete.".into())),
                        Err(e) => Some(format!("Tidy failed: {e}")),
                    };
                });
            });
        }
    }

    pub fn remove_startup_item(&mut self, id: &str) {
        if let Some(ref mut st) = self.pc.startup_list {
            st.items.retain(|i| i.id != id);
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StartupListReply>(
                METHOD_STARTUP_REMOVE,
                &json!({ "id": id }),
                move |res| {
                    if let Ok(list) = res {
                        handle.update(move |s, _| {
                            if let Some(ref m) = list.message {
                                s.notice = Some(m.clone());
                            }
                            s.pc.startup_list = Some(list);
                        });
                    }
                },
            );
        }
    }

    pub fn restore_startup_item(&mut self, id: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StartupListReply>(
                METHOD_STARTUP_RESTORE,
                &json!({ "id": id }),
                move |res| {
                    if let Ok(list) = res {
                        handle.update(move |s, _| s.pc.startup_list = Some(list));
                    }
                },
            );
        }
    }

    pub fn clean_startup_unneeded(&mut self) {
        self.notice = Some("Cleaning unneeded startup entries…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StartupListReply>(METHOD_STARTUP_CLEAN, &json!({}), move |res| {
                if let Ok(list) = res {
                    handle.update(move |s, _| {
                        s.notice = list.message.clone().or_else(|| Some("Startup cleaned.".into()));
                        s.pc.startup_list = Some(list);
                    });
                }
            });
        }
    }

    pub fn load_pc_drives(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.drives_loading = true;
            rpc.call::<_, DrivesReply>(
                METHOD_SYSTEM_DRIVES,
                &json!({ "space": true }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.drives_loading = false;
                        if let Ok(drv) = res {
                            s.pc.drives = Some(drv);
                        }
                    });
                },
            );
        }
    }

    pub fn apply_drive_fix(&mut self, key: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, DrivesReply>(
                METHOD_SYSTEM_DRIVES,
                &json!({ "action": key, "space": true }),
                move |res| {
                    if let Ok(drv) = res {
                        handle.update(move |s, _| {
                            if let Some(ref m) = drv.message {
                                s.notice = Some(m.clone());
                            }
                            s.pc.drives = Some(drv);
                        });
                    }
                },
            );
        }
    }

    pub fn clean_system_junk(&mut self) {
        self.notice = Some("Cleaning temporary files and caches…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_JUNK_CLEAN, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.notice = match res {
                        Ok(r) => r.message.or_else(|| Some("Temporary files cleaned.".into())),
                        Err(e) => Some(format!("Cleanup failed: {e}")),
                    };
                    s.load_pc_drives();
                });
            });
        }
    }

    pub fn load_pc_devices(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.devices_loading = true;
            let h1 = handle.clone();
            let rpc_audio = rpc.clone();
            rpc.call::<_, DevicesListReply>(METHOD_DEVICES_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    let out_id = list
                        .devices
                        .iter()
                        .find(|d| d.kind == "audio" && d.role.as_deref() != Some("mic"))
                        .map(|d| d.id.clone());
                    let mic_id = list
                        .devices
                        .iter()
                        .find(|d| d.kind == "audio" && d.role.as_deref() == Some("mic"))
                        .map(|d| d.id.clone());
                    let h_aud = h1.clone();
                    let out_clone = out_id.clone();
                    let mic_clone = mic_id.clone();
                    let photo_reqs: Vec<(String, serde_json::Value)> = list
                        .devices
                        .iter()
                        .map(|d| {
                            (
                                d.id.clone(),
                                json!({
                                    "kind": d.kind,
                                    "name": d.name,
                                    "model": d.model,
                                    "maker": d.maker.as_deref().or(d.brand.as_deref()),
                                }),
                            )
                        })
                        .collect();
                    let rpc_img = rpc_audio.clone();
                    let h_img_base = h1.clone();
                    h1.update(move |s, _| {
                        s.pc.devices_loading = false;
                        s.pc.audio_output_id = out_id;
                        s.pc.audio_mic_id = mic_id;
                        s.pc.devices = Some(list);
                    });
                    for (dev_id, payload) in photo_reqs {
                        let h_i = h_img_base.clone();
                        rpc_img.call::<_, DevicesImageReply>(
                            METHOD_DEVICES_IMAGE,
                            &payload,
                            move |img_res| {
                                if let Ok(reply) = img_res
                                    && let Some(url) = reply.url
                                {
                                    h_i.update(move |s, _| {
                                        s.pc.device_images.insert(dev_id, url);
                                    });
                                }
                            },
                        );
                    }
                    rpc_audio.call::<_, AudioReadReply>(
                        METHOD_AUDIO_READ,
                        &json!({ "output": out_clone, "mic": mic_clone }),
                        move |aud_res| {
                            if let Ok(aud) = aud_res {
                                h_aud.update(move |s, _| s.pc.audio = Some(aud));
                            }
                        },
                    );
                } else {
                    h1.update(|s, _| s.pc.devices_loading = false);
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, KeyboardReadReply>(METHOD_KEYBOARD_READ, &json!({}), move |res| {
                if let Ok(kb) = res
                    && kb.ok
                {
                    h2.update(move |s, _| s.pc.keyboard = Some(kb));
                }
            });
            let h3 = handle.clone();
            rpc.call::<_, KeyboardBackupsReply>(METHOD_KEYBOARD_BACKUPS, &json!({}), move |res| {
                if let Ok(b) = res {
                    h3.update(move |s, _| s.pc.keyboard_backups = b.backups);
                }
            });
            let h4 = handle.clone();
            let rpc_eq_img = rpc.clone();
            rpc.call::<_, EqStateReply>(METHOD_EQ, &json!({ "action": "state" }), move |res| {
                if let Ok(eq) = res
                    && let Some(hp) = eq.headphone
                {
                    let look = if hp.form == "over-ear" { "headphones" } else { "iem" };
                    let img_payload = json!({
                        "kind": "audio",
                        "model": look,
                        "name": hp.name,
                    });
                    let h4_img = h4.clone();
                    h4.update(move |s, _| s.pc.eq_headphone = Some(hp));
                    rpc_eq_img.call::<_, DevicesImageReply>(
                        METHOD_DEVICES_IMAGE,
                        &img_payload,
                        move |img_res| {
                            if let Ok(reply) = img_res
                                && let Some(url) = reply.url
                            {
                                h4_img.update(move |s, _| {
                                    if let Some(out_id) = s.pc.audio_output_id.clone() {
                                        s.pc.device_images.insert(out_id, url.clone());
                                    }
                                    s.pc.device_images.insert("audio:worn".into(), url);
                                });
                            }
                        },
                    );
                }
            });
        }
    }

    pub fn set_audio_volume(&mut self, is_output: bool, volume: i32) {
        let vol = volume.clamp(0, 100);
        if let Some(ref mut aud) = self.pc.audio {
            if is_output {
                if let Some(ref mut o) = aud.output {
                    o.volume = Some(vol);
                }
            } else if let Some(ref mut m) = aud.mic {
                m.volume = Some(vol);
            }
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(id)) = (self.rpc.clone(), target_id) {
            rpc.call_raw(
                METHOD_AUDIO_SET_VOLUME,
                &json!({ "id": id, "volume": vol }),
                |_| {},
            );
        }
    }

    pub fn set_audio_mute(&mut self, is_output: bool, muted: bool) {
        if let Some(ref mut aud) = self.pc.audio {
            if is_output {
                if let Some(ref mut o) = aud.output {
                    o.muted = muted;
                }
            } else if let Some(ref mut m) = aud.mic {
                m.muted = muted;
            }
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(id)) = (self.rpc.clone(), target_id) {
            rpc.call_raw(
                METHOD_AUDIO_SET_MUTE,
                &json!({ "id": id, "muted": muted }),
                |_| {},
            );
        }
    }

    pub fn switch_keyboard_profile(&mut self, slot: i32) {
        if let Some(ref mut kb) = self.pc.keyboard {
            kb.active_profile = slot;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_PROFILE,
                &json!({ "slot": slot }),
                move |res| {
                    if let Ok(kb) = res
                        && kb.ok
                    {
                        handle.update(move |s, _| s.pc.keyboard = Some(kb));
                    }
                },
            );
        }
    }

    pub fn update_keyboard_settings(
        &mut self,
        actuation_mm: Option<f64>,
        rapid_trigger: Option<bool>,
        rapid_trigger_mm: Option<f64>,
        tachyon: Option<bool>,
    ) {
        if let Some(ref mut kb) = self.pc.keyboard {
            if let Some(a) = actuation_mm {
                kb.actuation_mm = Some((a * 10.0).round() / 10.0);
            }
            if let Some(rt) = rapid_trigger {
                kb.rapid_trigger = rt;
            }
            if let Some(rtm) = rapid_trigger_mm {
                kb.rapid_trigger_mm = Some(rtm);
            }
            if let Some(t) = tachyon {
                kb.tachyon = t;
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            if let Some(a) = actuation_mm {
                map.insert("actuationMm".into(), json!((a * 10.0).round() / 10.0));
            }
            if let Some(rt) = rapid_trigger {
                map.insert("rapidTrigger".into(), json!(rt));
            }
            if let Some(rtm) = rapid_trigger_mm {
                map.insert("rapidTriggerMm".into(), json!(rtm));
            }
            if let Some(t) = tachyon {
                map.insert("tachyon".into(), json!(t));
            }
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_SETTINGS,
                &Value::Object(map),
                move |res| {
                    if let Ok(kb) = res
                        && kb.ok
                    {
                        handle.update(move |s, _| s.pc.keyboard = Some(kb));
                    }
                },
            );
        }
    }

    pub fn backup_keyboard_profiles(&mut self) {
        self.notice = Some("Backing up Wooting profiles…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeyboardBackupsReply>(METHOD_KEYBOARD_BACKUP, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(b) if b.ok => {
                        s.pc.keyboard_backups = b.backups;
                        s.notice = Some("Keyboard profiles backed up.".into());
                    }
                    _ => s.notice = Some("Keyboard backup failed.".into()),
                });
            });
        }
    }

    pub fn restore_keyboard_backup(&mut self, file_name: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_RESTORE,
                &json!({ "fileName": file_name }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(kb) if kb.ok => {
                            s.pc.keyboard = Some(kb);
                            s.notice = Some("Keyboard backup restored.".into());
                        }
                        _ => s.notice = Some("Could not restore keyboard backup.".into()),
                    });
                },
            );
        }
    }

    pub fn open_wootility(&mut self) {
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_KEYBOARD_OPEN_APP, &json!({}), |_| {});
        }
    }
}
