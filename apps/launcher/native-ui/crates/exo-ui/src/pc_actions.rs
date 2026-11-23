use crate::pc_state::PcTab;
use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, AppsIconsReply, AppsListReply, AudioReadReply, AutopilotState, DevicesImageReply,
    DevicesListReply, DrivesReply, EqStateReply, ExoAccountState, ExoAiState, HdrState,
    KeyboardBackupResult, KeyboardBackupsReply, KeyboardReadReply, LauncherSettings,
    METHOD_APP_CHECK_UPDATE, METHOD_APP_INSTALL_UPDATE, METHOD_APP_OPEN_LOGS,
    METHOD_APP_REPAIR_CACHE, METHOD_APPS_ICONS, METHOD_APPS_LIST, METHOD_AUDIO_READ,
    METHOD_AUDIO_SET_MUTE, METHOD_AUDIO_SET_VOLUME, METHOD_AUTOPILOT_STATE, METHOD_DEVICES_IMAGE,
    METHOD_DEVICES_LIST, METHOD_EQ, METHOD_EXO_ACCOUNT_STATE, METHOD_EXO_ACCOUNT_SYNC,
    METHOD_EXO_AI_STATE, METHOD_HDR_STATE, METHOD_KEYBOARD_BACKUP, METHOD_KEYBOARD_BACKUPS,
    METHOD_KEYBOARD_PROFILE, METHOD_KEYBOARD_READ, METHOD_KEYBOARD_RESTORE,
    METHOD_KEYBOARD_SETTINGS, METHOD_MOUSE_PANEL_BOUNDS, METHOD_MOUSE_PANEL_CLOSE,
    METHOD_MOUSE_PANEL_OPEN, METHOD_PLAYTIME_RIOT_REFRESH, METHOD_SETTINGS_SET,
    METHOD_SHADERS_ADDON, METHOD_SHADERS_CALL, METHOD_SHADERS_ESTIMATES,
    METHOD_SHADERS_PREPARE_GAMES, METHOD_SHADERS_PREPARE_START, METHOD_STARTUP_LIST,
    METHOD_STARTUP_REMOVE, METHOD_SYSTEM_ABOUT, METHOD_SYSTEM_DRIVES, METHOD_SYSTEM_TUNING,
    ShaderEstimate, ShaderSnapGame, ShadersAddonStatus, ShadersEstimatesReply,
    ShadersPrepareGamesReply, StartupListReply, SystemAbout, SystemTuningState, UpdateCheckReply,
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
            self.load_fps_counter_state();
            self.load_web_preferences();
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
            "frameGenEverywhere" => self.settings.frame_gen_everywhere = value,
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
        self.pc.armed_uninstall = None;
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
            let rpc_sh = rpc.clone();
            rpc.call::<_, Value>(METHOD_SHADERS_ADDON, &json!({}), move |res| {
                if let Ok(v) = res {
                    let present = v.get("present").and_then(|x| x.as_bool()).unwrap_or(false);
                    let snap_games = v
                        .get("snapshot")
                        .map(parse_shader_snap_games)
                        .unwrap_or_default();
                    let has_games = !snap_games.is_empty();
                    let status = ShadersAddonStatus {
                        ok: v.get("ok").and_then(|x| x.as_bool()).unwrap_or(true),
                        present,
                        state: v.get("state").and_then(|x| x.as_str()).map(str::to_string),
                        message: v
                            .get("message")
                            .and_then(|x| x.as_str())
                            .map(str::to_string),
                        snapshot: None,
                    };
                    let h5_snap = h5.clone();
                    h5.update(move |s, _| {
                        if !snap_games.is_empty() {
                            s.pc.shader_snap_games = snap_games;
                        }
                        s.pc.shaders = Some(status);
                        if let Some(snap_val) = v.get("snapshot") {
                            s.apply_shaders_snapshot_value(snap_val);
                        }
                    });
                    if present && !has_games {
                        rpc_sh.call::<_, Value>(
                            METHOD_SHADERS_CALL,
                            &json!({ "method": "snapshot", "params": {} }),
                            move |snap_res| {
                                if let Ok(sv) = snap_res
                                    && let Some(res_val) = sv.get("result")
                                {
                                    let res_clone = res_val.clone();
                                    h5_snap.update(move |s, _| {
                                        s.apply_shaders_snapshot_value(&res_clone);
                                    });
                                }
                            },
                        );
                    }
                }
            });
            let h5b = handle.clone();
            rpc.call::<_, ShadersEstimatesReply>(
                METHOD_SHADERS_ESTIMATES,
                &json!({}),
                move |res| {
                    if let Ok(est) = res
                        && est.ok
                    {
                        h5b.update(move |s, _| s.pc.shader_estimates = est.seconds);
                    }
                },
            );
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
            let h8 = handle.clone();
            rpc.call::<_, Value>("gpu.profile", &json!({}), move |res| {
                if let Ok(v) = res {
                    let (gpu_name, screen) = parse_gpu_profile_fields(&v);
                    h8.update(move |s, _| {
                        if gpu_name.is_some() {
                            s.pc.gpu_short_name = gpu_name;
                        }
                        if screen.is_some() {
                            s.pc.screen_label = screen;
                        }
                    });
                }
            });
            let h9 = handle.clone();
            rpc.call::<_, Value>("compress.list", &json!({}), move |res| {
                if let Ok(v) = res {
                    let (potential, saved) = parse_compress_totals(&v);
                    h9.update(move |s, _| {
                        s.pc.compress_potential_bytes = potential;
                        s.pc.compress_saved_bytes = saved;
                    });
                }
            });
            if self.pc.drives.is_none() {
                self.load_pc_drives();
            }
            self.load_pc_exo_ai_full();
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
                                s.notice =
                                    r.message.or_else(|| Some("Shader compile started.".into()));
                            }
                        });
                    },
                );
            }
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
                                s.notice = t
                                    .message
                                    .clone()
                                    .or_else(|| Some("Latency check complete.".into()));
                                s.pc.tuning = Some(t);
                            }
                            Err(e) => s.notice = Some(format!("Latency check failed: {e}")),
                        }
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
                    let mut by_size: Vec<&exo_rpc::InstalledAppItem> =
                        list.programs.iter().collect();
                    by_size.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));
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
            self.load_pc_apps_extras(false);
        }
    }

    pub fn disarm_uninstall(&mut self) {
        self.pc.armed_uninstall = None;
    }

    pub fn arm_or_confirm_uninstall(&mut self, id: &str) {
        if self
            .pc
            .armed_uninstall
            .as_ref()
            .is_some_and(|(armed_id, _)| armed_id == id)
        {
            self.pc.armed_uninstall = None;
            self.uninstall_app(id);
            return;
        }
        self.pc.armed_uninstall_seq = self.pc.armed_uninstall_seq.wrapping_add(1);
        let seq = self.pc.armed_uninstall_seq;
        self.pc.armed_uninstall = Some((id.to_string(), seq));
        #[cfg(windows)]
        if let Some(handle) = self.handle.clone() {
            std::thread::spawn(move || {
                std::thread::sleep(std::time::Duration::from_secs(4));
                handle.update(move |s, _| {
                    if s.pc
                        .armed_uninstall
                        .as_ref()
                        .is_some_and(|(_, armed_seq)| *armed_seq == seq)
                    {
                        s.pc.armed_uninstall = None;
                    }
                });
            });
        }
    }

    pub fn uninstall_app(&mut self, id: &str) {
        self.remove_single_app(id);
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

    pub fn load_pc_devices(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.devices_loading = true;
            let h1 = handle.clone();
            let rpc_follow = rpc.clone();
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
                    let kb_id = list
                        .devices
                        .iter()
                        .find(|d| d.lab)
                        .or_else(|| list.devices.iter().find(|d| d.kind == "keyboard"))
                        .map(|d| d.id.clone());
                    let h_aud = h1.clone();
                    let h_kb = h1.clone();
                    let h_bak = h1.clone();
                    let out_clone = out_id.clone();
                    let mic_clone = mic_id.clone();
                    let kb_clone = kb_id.clone();
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
                                    "color": d.color,
                                    "layout": d.layout,
                                }),
                            )
                        })
                        .collect();
                    let rpc_img = rpc_follow.clone();
                    let rpc_kb = rpc_follow.clone();
                    let rpc_bak = rpc_follow.clone();
                    let h_img_base = h1.clone();
                    h1.update(move |s, _| {
                        s.pc.devices_loading = false;
                        s.pc.audio_output_id = out_id;
                        s.pc.audio_mic_id = mic_id;
                        s.pc.keyboard_id = kb_id;
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
                    rpc_follow.call::<_, AudioReadReply>(
                        METHOD_AUDIO_READ,
                        &json!({ "output": out_clone, "mic": mic_clone }),
                        move |aud_res| {
                            if let Ok(aud) = aud_res {
                                h_aud.update(move |s, _| s.pc.audio = Some(aud));
                            }
                        },
                    );
                    let kb_payload = match kb_clone {
                        Some(ref id) => json!({ "id": id }),
                        None => json!({}),
                    };
                    rpc_kb.call::<_, KeyboardReadReply>(
                        METHOD_KEYBOARD_READ,
                        &kb_payload,
                        move |kb_res| {
                            h_kb.update(move |s, _| {
                                s.apply_keyboard_read_reply(kb_res.map_err(|e| e.to_string()));
                            });
                        },
                    );
                    rpc_bak.call::<_, KeyboardBackupsReply>(
                        METHOD_KEYBOARD_BACKUPS,
                        &kb_payload,
                        move |bak_res| {
                            if let Ok(b) = bak_res {
                                h_bak.update(move |s, _| s.pc.keyboard_backups = b.backups);
                            }
                        },
                    );
                } else {
                    h1.update(|s, _| s.pc.devices_loading = false);
                }
            });
            let h4 = handle.clone();
            let rpc_eq_img = rpc.clone();
            rpc.call::<_, EqStateReply>(METHOD_EQ, &json!({ "action": "state" }), move |res| {
                if let Ok(eq) = res
                    && let Some(hp) = eq.headphone
                {
                    let look = if hp.form == "over-ear" {
                        "headphones"
                    } else {
                        "iem"
                    };
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

    pub fn keyboard_device_id(&self) -> Option<String> {
        if let Some(sel) = self.pc.selected_device_id.as_deref()
            && let Some(devs) = self.pc.devices.as_ref()
            && devs
                .devices
                .iter()
                .any(|d| d.id == sel && (d.lab || d.kind == "keyboard"))
        {
            return Some(sel.to_string());
        }
        if let Some(ref id) = self.pc.keyboard_id {
            return Some(id.clone());
        }
        self.pc.devices.as_ref().and_then(|list| {
            list.devices
                .iter()
                .find(|d| d.lab)
                .or_else(|| list.devices.iter().find(|d| d.kind == "keyboard"))
                .map(|d| d.id.clone())
        })
    }

    pub fn apply_keyboard_read_reply(&mut self, res: Result<KeyboardReadReply, String>) {
        match res {
            Ok(kb) if kb.ok => {
                self.pc.keyboard_error = None;
                self.pc.keyboard = Some(kb);
            }
            Ok(kb) => {
                self.pc.keyboard_error = kb
                    .message
                    .clone()
                    .or_else(|| Some("Could not read Wooting keyboard.".into()));
                self.pc.keyboard = Some(kb);
            }
            Err(err) => {
                self.pc.keyboard_error = Some(format!("Could not read Wooting keyboard: {err}"));
            }
        }
    }

    pub fn apply_keyboard_write_reply(
        &mut self,
        prev: Option<KeyboardReadReply>,
        res: Result<KeyboardReadReply, String>,
        fallback_err: &str,
    ) {
        match res {
            Ok(kb) if kb.ok => {
                self.pc.keyboard_error = None;
                self.pc.keyboard = Some(kb);
            }
            Ok(kb) => {
                let msg = kb
                    .message
                    .clone()
                    .unwrap_or_else(|| fallback_err.to_string());
                self.pc.keyboard = prev;
                self.pc.keyboard_error = Some(msg);
            }
            Err(err) => {
                self.pc.keyboard = prev;
                self.pc.keyboard_error = Some(format!("{fallback_err}: {err}"));
            }
        }
    }

    pub fn toggle_keyboard_key(&mut self, key_id: String) {
        if self.pc.keyboard_selected_key.as_deref() == Some(key_id.as_str()) {
            self.pc.keyboard_selected_key = None;
        } else {
            self.pc.keyboard_selected_key = Some(key_id);
        }
    }

    pub fn reload_keyboard(&mut self) {
        self.pc.keyboard_error = None;
        let kb_id = self.keyboard_device_id();
        let payload = match kb_id {
            Some(ref id) => json!({ "id": id }),
            None => json!({}),
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            let payload_bak = payload.clone();
            rpc.call::<_, KeyboardReadReply>(METHOD_KEYBOARD_READ, &payload, move |res| {
                h1.update(move |s, _| {
                    s.apply_keyboard_read_reply(res.map_err(|e| e.to_string()));
                });
            });
            rpc.call::<_, KeyboardBackupsReply>(
                METHOD_KEYBOARD_BACKUPS,
                &payload_bak,
                move |res| {
                    if let Ok(b) = res {
                        handle.update(move |s, _| s.pc.keyboard_backups = b.backups);
                    }
                },
            );
        }
    }

    pub fn switch_keyboard_profile(&mut self, slot: i32) {
        if let Some(ref kb) = self.pc.keyboard
            && !kb.can_write
        {
            self.pc.keyboard_error = kb
                .message
                .clone()
                .or_else(|| Some("Keyboard is read-only right now.".into()));
            return;
        }
        let prev = self.pc.keyboard.clone();
        self.pc.keyboard_error = None;
        if let Some(ref mut kb) = self.pc.keyboard {
            kb.active_profile = slot;
        }
        let kb_id = self.keyboard_device_id();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            if let Some(id) = kb_id {
                map.insert("id".into(), json!(id));
            }
            map.insert("slot".into(), json!(slot));
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_PROFILE,
                &Value::Object(map),
                move |res| {
                    handle.update(move |s, _| {
                        s.apply_keyboard_write_reply(
                            prev,
                            res.map_err(|e| e.to_string()),
                            "Failed to switch keyboard profile",
                        );
                    });
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
        rgb_brightness: Option<i32>,
    ) {
        if let Some(ref kb) = self.pc.keyboard
            && !kb.can_write
        {
            self.pc.keyboard_error = kb
                .message
                .clone()
                .or_else(|| Some("Keyboard is read-only right now.".into()));
            return;
        }
        let (min_act, max_act, has_rt, has_rgb) = self
            .pc
            .keyboard
            .as_ref()
            .map(|kb| {
                (
                    kb.min_actuation_mm.unwrap_or(0.1),
                    kb.max_actuation_mm.unwrap_or(4.0),
                    kb.has_rapid_trigger,
                    kb.has_rgb,
                )
            })
            .unwrap_or((0.1, 4.0, true, true));

        let clamped_act = actuation_mm.map(|a| ((a.clamp(min_act, max_act)) * 10.0).round() / 10.0);
        let eff_rt = if has_rt { rapid_trigger } else { None };
        let clamped_rt_mm = if has_rt {
            rapid_trigger_mm.map(|v| ((v.clamp(0.10, 2.35)) * 20.0).round() / 20.0)
        } else {
            None
        };
        let clamped_rgb = if has_rgb {
            rgb_brightness.map(|b| b.clamp(0, 100))
        } else {
            None
        };

        let prev = self.pc.keyboard.clone();
        self.pc.keyboard_error = None;
        if let Some(ref mut kb) = self.pc.keyboard {
            if let Some(a) = clamped_act {
                kb.actuation_mm = Some(a);
            }
            if let Some(rt) = eff_rt {
                kb.rapid_trigger = Some(rt);
            }
            if let Some(rtm) = clamped_rt_mm {
                kb.rapid_trigger_mm = Some(rtm);
            }
            if let Some(t) = tachyon {
                kb.tachyon = Some(t);
            }
            if let Some(rgb) = clamped_rgb {
                kb.rgb_brightness = Some(rgb);
            }
        }
        let kb_id = self.keyboard_device_id();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            if let Some(id) = kb_id {
                map.insert("id".into(), json!(id));
            }
            if let Some(a) = clamped_act {
                map.insert("actuationMm".into(), json!(a));
            }
            if let Some(rt) = eff_rt {
                map.insert("rapidTrigger".into(), json!(rt));
            }
            if let Some(rtm) = clamped_rt_mm {
                map.insert("rapidTriggerMm".into(), json!(rtm));
            }
            if let Some(t) = tachyon {
                map.insert("tachyon".into(), json!(t));
            }
            if let Some(rgb) = clamped_rgb {
                map.insert("rgbBrightness".into(), json!(rgb));
            }
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_SETTINGS,
                &Value::Object(map),
                move |res| {
                    handle.update(move |s, _| {
                        s.apply_keyboard_write_reply(
                            prev,
                            res.map_err(|e| e.to_string()),
                            "Failed to save keyboard settings",
                        );
                    });
                },
            );
        }
    }

    pub fn backup_keyboard_profiles(&mut self) {
        self.pc.keyboard_error = None;
        self.notice = Some("Backing up Wooting profiles…".into());
        let kb_id = self.keyboard_device_id();
        let payload = match kb_id {
            Some(ref id) => json!({ "id": id }),
            None => json!({}),
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeyboardBackupResult>(METHOD_KEYBOARD_BACKUP, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(b) if b.ok => {
                        s.pc.keyboard_backups = b.backups;
                        s.pc.keyboard_error = None;
                        s.notice = Some("Keyboard profiles backed up.".into());
                    }
                    Ok(b) => {
                        let msg = b
                            .message
                            .unwrap_or_else(|| "Keyboard backup failed.".into());
                        s.pc.keyboard_error = Some(msg.clone());
                        s.notice = Some(msg);
                    }
                    Err(e) => {
                        let msg = format!("Keyboard backup failed: {e}");
                        s.pc.keyboard_error = Some(msg.clone());
                        s.notice = Some(msg);
                    }
                });
            });
        }
    }

    pub fn restore_keyboard_backup(&mut self, file_name: String) {
        if let Some(ref kb) = self.pc.keyboard
            && !kb.can_write
        {
            self.pc.keyboard_error = kb
                .message
                .clone()
                .or_else(|| Some("Keyboard is read-only right now.".into()));
            return;
        }
        self.pc.keyboard_error = None;
        self.notice = Some("Restoring Wooting backup…".into());
        let kb_id = self.keyboard_device_id();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut map = Map::new();
            if let Some(id) = kb_id {
                map.insert("id".into(), json!(id));
            }
            map.insert("fileName".into(), json!(file_name));
            rpc.call::<_, KeyboardReadReply>(
                METHOD_KEYBOARD_RESTORE,
                &Value::Object(map),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(kb) if kb.ok => {
                            s.pc.keyboard = Some(kb);
                            s.pc.keyboard_error = None;
                            s.notice = Some("Keyboard backup restored.".into());
                        }
                        Ok(kb) => {
                            let msg = kb
                                .message
                                .unwrap_or_else(|| "Keyboard restore failed.".into());
                            s.pc.keyboard_error = Some(msg.clone());
                            s.notice = Some(msg);
                        }
                        Err(e) => {
                            let msg = format!("Keyboard restore failed: {e}");
                            s.pc.keyboard_error = Some(msg.clone());
                            s.notice = Some(msg);
                        }
                    });
                },
            );
        }
    }

    pub fn set_owner_hwnd(&mut self, hwnd: u64) {
        if hwnd == 0 || self.pc.owner_hwnd == hwnd {
            return;
        }
        self.pc.owner_hwnd = hwnd;
        if self.pc.mouse_panel_open
            && let Some(rect) = self.pc.mouse_panel_rect
        {
            let visible = self.mouse_panel_should_be_visible();
            self.pc.mouse_panel_visible = visible;
            self.send_mouse_panel_open(rect, visible);
        }
    }

    pub fn mouse_panel_should_be_visible(&self) -> bool {
        self.pc.mouse_panel_open
            && self.room == crate::state::Room::Devices
            && self.pc.mouse_panel_window_visible
            && !self.palette_open
            && !self.key_modal_open
            && self.setup.beat.is_none()
            && !self.capture_menu_open
            && !self.media.captures_open
            && !self.props_open
            && !self.library_more_open
    }

    pub fn open_mouse_panel(&mut self, dev_id: String) {
        self.pc.selected_device_id = Some(dev_id);
        self.pc.mouse_panel_open = true;
        self.pc.mouse_panel_loading = self.status != HostStatus::Standalone;
        self.pc.mouse_panel_error = None;
        self.pc.mouse_panel_rect = None;
        self.pc.mouse_panel_visible = false;
    }

    pub fn retry_mouse_panel(&mut self) {
        if !self.pc.mouse_panel_open {
            return;
        }
        self.pc.mouse_panel_loading = self.status != HostStatus::Standalone;
        self.pc.mouse_panel_error = None;
        if let Some(rect) = self.pc.mouse_panel_rect {
            let visible = self.mouse_panel_should_be_visible();
            self.pc.mouse_panel_visible = visible;
            self.send_mouse_panel_open(rect, visible);
        }
    }

    pub fn update_mouse_panel_layout(&mut self, rect: [f32; 4]) {
        if !self.pc.mouse_panel_open || rect[2] < 2.0 || rect[3] < 2.0 {
            return;
        }
        let visible = self.mouse_panel_should_be_visible();
        let first_layout = self.pc.mouse_panel_rect.is_none();
        let changed =
            self.pc.mouse_panel_rect != Some(rect) || self.pc.mouse_panel_visible != visible;
        if !first_layout && !changed {
            return;
        }
        self.pc.mouse_panel_rect = Some(rect);
        self.pc.mouse_panel_visible = visible;
        if first_layout {
            self.send_mouse_panel_open(rect, visible);
        } else {
            self.send_mouse_panel_bounds(rect, visible);
        }
    }

    pub fn sync_mouse_panel_visibility(&mut self) {
        if !self.pc.mouse_panel_open {
            return;
        }
        let visible = self.mouse_panel_should_be_visible();
        if visible == self.pc.mouse_panel_visible {
            return;
        }
        self.pc.mouse_panel_visible = visible;
        if let Some(rect) = self.pc.mouse_panel_rect {
            self.send_mouse_panel_bounds(rect, visible);
        }
    }

    pub fn set_mouse_panel_window_visible(&mut self, visible: bool) {
        self.pc.mouse_panel_window_visible = visible;
        self.sync_mouse_panel_visibility();
    }

    pub fn close_mouse_panel(&mut self) {
        let was_open = self.pc.mouse_panel_open;
        self.pc.mouse_panel_open = false;
        self.pc.mouse_panel_loading = false;
        self.pc.mouse_panel_error = None;
        self.pc.mouse_panel_rect = None;
        self.pc.mouse_panel_visible = false;
        self.pc.selected_device_id = None;
        if !was_open {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call::<_, ActionResult>(METHOD_MOUSE_PANEL_CLOSE, &json!({}), |_| {});
        }
    }

    fn send_mouse_panel_open(&mut self, rect: [f32; 4], visible: bool) {
        if self.status == HostStatus::Standalone {
            self.pc.mouse_panel_loading = false;
            return;
        }
        let owner_hwnd = self.pc.owner_hwnd;
        if owner_hwnd == 0 {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let params = json!({
                "ownerHwnd": owner_hwnd,
                "x": rect[0] as f64,
                "y": rect[1] as f64,
                "width": rect[2] as f64,
                "height": rect[3] as f64,
                "visible": visible,
            });
            rpc.call::<_, ActionResult>(METHOD_MOUSE_PANEL_OPEN, &params, move |res| {
                handle.update(move |s, _| {
                    if !s.pc.mouse_panel_open {
                        return;
                    }
                    s.pc.mouse_panel_loading = false;
                    match res {
                        Ok(r) if r.ok => {
                            s.pc.mouse_panel_error = None;
                        }
                        Ok(r) => {
                            s.pc.mouse_panel_error = Some(
                                r.message
                                    .unwrap_or_else(|| "Exo Mouse could not open.".into()),
                            );
                        }
                        Err(e) => {
                            s.pc.mouse_panel_error = Some(e.to_string());
                        }
                    }
                });
            });
        }
    }

    fn send_mouse_panel_bounds(&mut self, rect: [f32; 4], visible: bool) {
        if self.status == HostStatus::Standalone || self.pc.owner_hwnd == 0 {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let params = json!({
                "ownerHwnd": self.pc.owner_hwnd,
                "x": rect[0] as f64,
                "y": rect[1] as f64,
                "width": rect[2] as f64,
                "height": rect[3] as f64,
                "visible": visible,
            });
            rpc.call::<_, ActionResult>(METHOD_MOUSE_PANEL_BOUNDS, &params, |_| {});
        }
    }
}

pub(crate) fn parse_gpu_profile_fields(v: &Value) -> (Option<String>, Option<String>) {
    let gpu = v
        .get("gpu")
        .and_then(|x| x.as_str())
        .filter(|s| !s.is_empty())
        .map(str::to_string);
    let screen = v.get("screen").and_then(|sc| {
        let name = sc
            .get("name")
            .and_then(|x| x.as_str())
            .filter(|s| !s.is_empty());
        let hz = sc.get("hz").and_then(|x| x.as_u64()).filter(|&n| n > 0);
        match (name, hz) {
            (Some(n), Some(h)) => Some(format!("{n} · {h} Hz")),
            (Some(n), None) => Some(n.to_string()),
            (None, Some(h)) => Some(format!("Main screen · {h} Hz")),
            (None, None) => None,
        }
    });
    (gpu, screen)
}

pub(crate) fn parse_compress_totals(v: &Value) -> (u64, u64) {
    let potential = v.get("potential").and_then(|x| x.as_u64()).unwrap_or(0);
    let saved = v.get("saved").and_then(|x| x.as_u64()).unwrap_or(0);
    (potential, saved)
}

pub(crate) fn parse_shader_snap_games(v: &Value) -> Vec<ShaderSnapGame> {
    let Some(arr) = v.get("games").and_then(|x| x.as_array()) else {
        return Vec::new();
    };
    arr.iter()
        .filter_map(|g| {
            let id = g.get("id").and_then(|x| x.as_str())?.to_string();
            let name = g
                .get("name")
                .and_then(|x| x.as_str())
                .unwrap_or("")
                .to_string();
            let estimate = g.get("estimate").and_then(|e| {
                e.get("seconds")
                    .and_then(|s| s.as_f64())
                    .map(|seconds| ShaderEstimate {
                        seconds,
                        low_seconds: e.get("lowSeconds").and_then(|x| x.as_f64()),
                        learned: e.get("learned").and_then(|x| x.as_bool()).unwrap_or(false),
                    })
            });
            Some(ShaderSnapGame {
                id,
                name,
                install_dir: g
                    .get("installDir")
                    .and_then(|x| x.as_str())
                    .map(str::to_string),
                status_text: g
                    .get("statusText")
                    .and_then(|x| x.as_str())
                    .map(str::to_string),
                tone: g.get("tone").and_then(|x| x.as_str()).map(str::to_string),
                queued: g.get("queued").and_then(|x| x.as_bool()).unwrap_or(false),
                compiling: g
                    .get("compiling")
                    .and_then(|x| x.as_bool())
                    .unwrap_or(false),
                percent: g.get("percent").and_then(|x| x.as_f64()),
                estimate,
                in_add_all_ready: g
                    .get("inAddAllReady")
                    .and_then(|x| x.as_bool())
                    .unwrap_or(false),
                error: g.get("error").and_then(|x| x.as_str()).map(str::to_string),
            })
        })
        .collect()
}
