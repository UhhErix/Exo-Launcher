use crate::pc_actions::parse_shader_snap_games;
use crate::pc_state::{
    AutoClearedEntry, AutoTuneStageId, AutoTuneStageStatus, BloatAppInfo,
    DriverInstallOverlayState, RemovedAppEntry, best_steps_list, compute_initial_auto_tune_stages,
    format_bytes, has_interrupts_work, is_playbook_item, leftover_cleared_line, trim_stop,
};
use crate::state::{AppState, HostStatus, Room};
use exo_rpc::{
    ActionResult, Afmf3State, AppCleanReply, AppConfirmReply, AppFind, AppUndoReply, AppsListReply,
    AppsPackagesPlanReply, AppsPackagesRemoveReply, AppsPackagesReply, AutoTuneState,
    AutopilotSetReply, AutopilotState, BiosUpdateReply, CompressActReply, CompressAllReply,
    CompressList, CompressSettingsReply, CompressState, DevicesListReply, DisplayState,
    DriverProgress, DriverState, DriverStep, DriversState, DrivesReply, ExoAiState,
    FpsCounterReply, GpuDriverInstallEvent, GpuProfile, InstallerCacheCleanReply,
    InstallerCacheReply, InterruptsState, JunkCleanReply, JunkScanReply, KeepStatus,
    LauncherSettings, METHOD_APPS_ALLOW_EDGE, METHOD_APPS_CLEAN, METHOD_APPS_CONFIRM,
    METHOD_APPS_INSTALLER_CACHE, METHOD_APPS_INSTALLER_CACHE_CLEAN, METHOD_APPS_LEAVE,
    METHOD_APPS_LIST, METHOD_APPS_OPEN_DEFAULTS, METHOD_APPS_PACKAGE_ADD_BACK,
    METHOD_APPS_PACKAGES, METHOD_APPS_PACKAGES_PLAN, METHOD_APPS_PACKAGES_REMOVE,
    METHOD_APPS_RESTORE_POINT, METHOD_APPS_SCAN, METHOD_APPS_TIDY, METHOD_APPS_UNDO,
    METHOD_APPS_UNINSTALL, METHOD_AUTOPILOT_SET, METHOD_AUTOPILOT_STATE, METHOD_BIOS_UPDATE,
    METHOD_COMPRESS_ACT, METHOD_COMPRESS_ALL, METHOD_COMPRESS_LIST, METHOD_COMPRESS_SETTINGS,
    METHOD_CONTROLLER_READ, METHOD_DEVICES_LIST, METHOD_EXO_AI_SET, METHOD_FPS_COUNTER,
    METHOD_GPU_AFMF3, METHOD_GPU_CONTROL_PANEL, METHOD_GPU_DISPLAY, METHOD_GPU_DRIVER,
    METHOD_GPU_PROFILE, METHOD_JUNK_CLEAN, METHOD_JUNK_SCAN, METHOD_KEEP_STATUS,
    METHOD_REPLACED_REMOVE, METHOD_RUNTIMES_CANCEL, METHOD_RUNTIMES_INSTALL, METHOD_RUNTIMES_LIST,
    METHOD_SETTINGS_SET, METHOD_SHADERS_CALL, METHOD_SHADERS_PREPARE_CANCEL,
    METHOD_SHADERS_REBUILD, METHOD_STARTUP_CLEAN, METHOD_STARTUP_RESTORE,
    METHOD_SYSTEM_DRIVERS, METHOD_SYSTEM_DRIVES, METHOD_SYSTEM_HEALTH, METHOD_SYSTEM_INTERRUPTS,
    METHOD_SYSTEM_TUNE, METHOD_SYSTEM_TUNING, METHOD_WEB_PREFERENCES_GET,
    METHOD_WEB_PREFERENCES_SET, PcHealth, RestorePointReply, RuntimesInstallReply,
    RuntimesListReply, RuntimesProgress, ShaderRebuild, StartupListReply, SystemTuningState,
    WebClientOptions,
};
use serde_json::{Value, json};
use std::collections::HashSet;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

#[cfg(windows)]
fn call_sync<R: exo_rpc::DeserializeOwned + Send + 'static>(
    rpc: &exo_rpc::RpcClient,
    method: &str,
    params: &Value,
) -> Result<R, exo_rpc::RpcError> {
    rpc.call_blocking(method, params, Duration::from_secs(180))
}

#[derive(Debug, Clone)]
pub enum UndoTarget {
    Removed(String),
    Tidy,
    AutoCleared(String),
    InstallerCache,
}

fn find_to_removed_entry(id: String, name: String, find: AppFind) -> RemovedAppEntry {
    let picks: HashSet<i64> = find
        .items
        .iter()
        .filter(|i| i.selected)
        .map(|i| i.id)
        .collect();
    let undo_token = find.undo.clone();
    RemovedAppEntry {
        id,
        name,
        find: Some(find),
        message: None,
        cleared_line: None,
        undo_token,
        undone: false,
        review_open: false,
        picks,
        error: None,
    }
}

impl AppState {
    pub fn refresh_auto_tune_stages_if_idle(&mut self) {
        if !self.pc.auto_tune_running && !self.pc.auto_tune_finished {
            self.pc.auto_tune_stages = compute_initial_auto_tune_stages(&self.pc, &self.settings);
        }
    }

    pub fn load_pc_exo_ai_full(&mut self) {
        self.refresh_auto_tune_stages_if_idle();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h = handle.clone();
            rpc.call::<_, AutoTuneState>(METHOD_SYSTEM_TUNE, &json!({}), move |res| {
                if let Ok(t) = res {
                    h.update(move |s, _| {
                        s.pc.auto_tune = Some(t);
                    });
                }
            });

            let h = handle.clone();
            rpc.call::<_, PcHealth>(METHOD_SYSTEM_HEALTH, &json!({}), move |res| {
                if let Ok(hp) = res {
                    h.update(move |s, _| {
                        s.pc.health = Some(hp);
                        s.refresh_auto_tune_stages_if_idle();
                    });
                }
            });

            let h = handle.clone();
            rpc.call::<_, InterruptsState>(METHOD_SYSTEM_INTERRUPTS, &json!({}), move |res| {
                if let Ok(ir) = res {
                    h.update(move |s, _| {
                        s.pc.interrupts = Some(ir);
                        s.refresh_auto_tune_stages_if_idle();
                    });
                }
            });

            let h = handle.clone();
            rpc.call::<_, GpuProfile>(METHOD_GPU_PROFILE, &json!({}), move |res| {
                if let Ok(gp) = res {
                    h.update(move |s, _| {
                        if let Some(ref g) = gp.gpu
                            && !g.is_empty()
                        {
                            s.pc.gpu_short_name = Some(g.clone());
                        }
                        let screen_lbl = match (
                            gp.screen.name.as_deref().filter(|n| !n.is_empty()),
                            (gp.screen.hz > 0.0).then_some(gp.screen.hz.round() as u64),
                        ) {
                            (Some(n), Some(hz)) => Some(format!("{n} · {hz} Hz")),
                            (Some(n), None) => Some(n.to_string()),
                            (None, Some(hz)) => Some(format!("Main screen · {hz} Hz")),
                            (None, None) => None,
                        };
                        if screen_lbl.is_some() {
                            s.pc.screen_label = screen_lbl;
                        }
                        s.pc.gpu_profile = Some(gp);
                        s.refresh_auto_tune_stages_if_idle();
                    });
                }
            });

            let branch = self.pc.gpu_driver_branch.clone();
            let h = handle.clone();
            rpc.call::<_, DriverState>(
                METHOD_GPU_DRIVER,
                &json!({ "action": "state", "branch": branch }),
                move |res| {
                    if let Ok(drv) = res {
                        h.update(move |s, _| {
                            s.pc.gpu_driver = Some(drv);
                            s.refresh_auto_tune_stages_if_idle();
                        });
                    }
                },
            );

            let h = handle.clone();
            rpc.call::<_, DisplayState>(
                METHOD_GPU_DISPLAY,
                &json!({ "action": "state" }),
                move |res| {
                    if let Ok(disp) = res {
                        h.update(move |s, _| {
                            s.pc.display_state = Some(disp);
                            s.refresh_auto_tune_stages_if_idle();
                        });
                    }
                },
            );

            let h = handle.clone();
            rpc.call::<_, Afmf3State>(METHOD_GPU_AFMF3, &json!({}), move |res| {
                if let Ok(afmf) = res {
                    h.update(move |s, _| s.pc.afmf3 = Some(afmf));
                }
            });

            let h = handle.clone();
            rpc.call::<_, KeepStatus>(METHOD_KEEP_STATUS, &json!({}), move |res| {
                if let Ok(kept) = res {
                    h.update(move |s, _| s.pc.keep_status = Some(kept));
                }
            });

            let h = handle.clone();
            rpc.call::<_, DriversState>(METHOD_SYSTEM_DRIVERS, &json!({}), move |res| {
                if let Ok(ds) = res {
                    h.update(move |s, _| s.pc.drivers_state = Some(ds));
                }
            });

            let h = handle.clone();
            rpc.call::<_, BiosUpdateReply>(METHOD_BIOS_UPDATE, &json!({}), move |res| {
                if let Ok(reply) = res {
                    h.update(move |s, _| s.pc.bios_staged = reply.staged);
                }
            });

            let h = handle.clone();
            rpc.call::<_, RuntimesListReply>(METHOD_RUNTIMES_LIST, &json!({}), move |res| {
                if let Ok(reply) = res {
                    h.update(move |s, _| s.pc.runtimes = Some(reply.items));
                }
            });

            let h = handle.clone();
            rpc.call::<_, ShaderRebuild>(METHOD_SHADERS_REBUILD, &json!({}), move |res| {
                if let Ok(rb) = res
                    && rb.ok
                {
                    h.update(move |s, _| s.pc.shaders_rebuild = Some(rb));
                }
            });

            let h = handle.clone();
            rpc.call::<_, AppsPackagesReply>(METHOD_APPS_PACKAGES, &json!({}), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                {
                    let bloat: Vec<BloatAppInfo> = reply
                        .items
                        .iter()
                        .filter(|i| i.for_me && i.bloat.is_some())
                        .map(|i| BloatAppInfo {
                            id: i.id.clone(),
                            name: i.name.clone(),
                        })
                        .collect();
                    h.update(move |s, _| {
                        s.pc.windows_packages = reply.items;
                        s.pc.bloat_apps = bloat;
                        s.refresh_auto_tune_stages_if_idle();
                    });
                }
            });

            let h = handle.clone();
            rpc.call::<_, AppsListReply>(METHOD_APPS_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    h.update(move |s, _| {
                        s.pc.apps_list = Some(list);
                    });
                }
            });

            let h = handle.clone();
            rpc.call::<_, CompressList>(METHOD_COMPRESS_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    h.update(move |s, _| {
                        s.pc.compress_potential_bytes = list.potential;
                        s.pc.compress_saved_bytes = list.saved;
                        s.pc.compress_list = Some(list);
                    });
                }
            });

            let h = handle.clone();
            let rpc_ctrl = rpc.clone();
            rpc.call::<_, DevicesListReply>(METHOD_DEVICES_LIST, &json!({}), move |res| {
                if let Ok(reply) = res {
                    let pads: Vec<(String, String)> = reply
                        .devices
                        .into_iter()
                        .filter(|d| d.kind == "controller" && d.lab)
                        .map(|d| (d.id, d.name))
                        .collect();
                    if pads.is_empty() {
                        h.update(|s, _| s.pc.controller_firmwares.clear());
                        return;
                    }
                    for (id, name) in pads {
                        let h_pad = h.clone();
                        let id_c = id.clone();
                        rpc_ctrl.call::<_, Value>(
                            METHOD_CONTROLLER_READ,
                            &json!({ "id": id }),
                            move |cr| {
                                if let Ok(v) = cr
                                    && let Some(cur) = v
                                        .get("firmware")
                                        .and_then(|f| f.get("current"))
                                        .and_then(|c| c.as_bool())
                                {
                                    h_pad.update(move |s, _| {
                                        s.pc.controller_firmwares.retain(|(p, _, _)| p != &id_c);
                                        s.pc.controller_firmwares.push((id_c, name, cur));
                                    });
                                }
                            },
                        );
                    }
                }
            });
        }
    }

    pub fn run_auto_tune_pipeline(&mut self) {
        if self.pc.auto_tune_running {
            return;
        }
        self.pc.auto_tune_running = true;
        self.pc.auto_tune_finished = false;
        self.pc.auto_tune_cinema_error = None;
        let initial = compute_initial_auto_tune_stages(&self.pc, &self.settings);
        self.pc.auto_tune_stages = initial.clone();

        if self.status == HostStatus::Standalone {
            for st in &mut self.pc.auto_tune_stages {
                st.status = AutoTuneStageStatus::Done;
            }
            self.pc.auto_tune_running = false;
            self.pc.auto_tune_finished = true;
            self.pc.auto_tune_completed_at = Some(
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map(|d| d.as_secs())
                    .unwrap_or(1),
            );
            return;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let power_applied = self
                .pc
                .health
                .as_ref()
                .and_then(|h| h.items.iter().find(|i| i.key == "power"))
                .map(|i| i.applied);
            let profile_todo = self
                .pc
                .gpu_profile
                .as_ref()
                .is_some_and(|p| p.ok && matches!(p.status.as_str(), "ready" | "changed"));
            let screen_ids: Vec<String> = self
                .pc
                .display_state
                .as_ref()
                .map(|d| {
                    d.displays
                        .iter()
                        .filter(|s| !s.is_best)
                        .map(|s| s.id.clone())
                        .collect()
                })
                .unwrap_or_default();
            let hdr_todo = self
                .pc
                .hdr
                .as_ref()
                .is_some_and(|h| h.supported && h.verdict.as_deref() != Some("weak"))
                && !self.settings.hdr_for_games;
            let fg_todo =
                self.settings.frame_gen_everywhere_card && !self.settings.frame_gen_everywhere;
            let sys_todo = self
                .pc
                .tuning
                .as_ref()
                .is_some_and(|t| t.items.iter().any(|i| is_playbook_item(i) && !i.on));
            let interrupts_action = if has_interrupts_work(self.pc.interrupts.as_ref()) {
                Some(if self.pc.interrupts.as_ref().is_some_and(|i| i.reset) {
                    "reapply"
                } else {
                    "apply"
                })
            } else {
                None
            };
            let net_todo = self
                .pc
                .health
                .as_ref()
                .and_then(|h| h.items.iter().find(|i| i.key == "network"))
                .is_some_and(|i| !i.applied);
            let drives_todo = self
                .pc
                .drives
                .as_ref()
                .is_some_and(|d| d.fixes.iter().any(|f| !f.applied));
            let bloat_ids: Vec<String> = self.pc.bloat_apps.iter().map(|b| b.id.clone()).collect();
            let branch = self.pc.gpu_driver_branch.clone();

            std::thread::spawn(move || {
                let mut warned = false;
                let set_stage = |h: &rune::AppHandle<AppState>,
                                 id: AutoTuneStageId,
                                 st: AutoTuneStageStatus,
                                 detail: Option<String>| {
                    h.update(move |s, _| {
                        if let Some(stage) = s.pc.auto_tune_stages.iter_mut().find(|x| x.id == id) {
                            stage.status = st;
                            if let Some(d) = detail {
                                stage.detail = d;
                            }
                        }
                    });
                };

                for id in AutoTuneStageId::ALL {
                    set_stage(&handle, id, AutoTuneStageStatus::Active, None);
                    let (status, detail) = match id {
                        AutoTuneStageId::Cooling => {
                            let ai = call_sync::<ExoAiState>(
                                &rpc,
                                METHOD_EXO_AI_SET,
                                &json!({ "runNow": true }),
                            );
                            let mut warn_msg: Option<String> = None;
                            if power_applied == Some(false) {
                                match call_sync::<PcHealth>(
                                    &rpc,
                                    METHOD_SYSTEM_HEALTH,
                                    &json!({ "action": "apply" }),
                                ) {
                                    Ok(hp) => {
                                        if !hp.ok
                                            && let Some(m) = hp.message.clone()
                                        {
                                            warn_msg = Some(trim_stop(&m));
                                        }
                                        let h_hp = handle.clone();
                                        h_hp.update(move |s, _| s.pc.health = Some(hp));
                                    }
                                    Err(_) => warn_msg = Some("Step could not finish".into()),
                                }
                            }
                            if let Some(w) = warn_msg {
                                (AutoTuneStageStatus::Warn, w)
                            } else {
                                match ai {
                                    Ok(ai_state) => {
                                        let fan_ok = ai_state.fans.supported;
                                        let err = (!ai_state.ok)
                                            .then(|| ai_state.message.clone())
                                            .flatten();
                                        let h_ai = handle.clone();
                                        h_ai.update(move |s, _| s.pc.exo_ai = Some(ai_state));
                                        if let Some(m) = err {
                                            (AutoTuneStageStatus::Warn, trim_stop(&m))
                                        } else if fan_ok {
                                            (
                                                AutoTuneStageStatus::Done,
                                                "Exo power plan, Autopilot & automatic GPU fans active".into(),
                                            )
                                        } else {
                                            (
                                                AutoTuneStageStatus::Done,
                                                "Exo power plan & Autopilot active".into(),
                                            )
                                        }
                                    }
                                    Err(_) => {
                                        (AutoTuneStageStatus::Warn, "Step could not finish".into())
                                    }
                                }
                            }
                        }
                        AutoTuneStageId::Graphics => {
                            let mut problems: Vec<String> = Vec::new();
                            if profile_todo {
                                match call_sync::<GpuProfile>(
                                    &rpc,
                                    METHOD_GPU_PROFILE,
                                    &json!({ "action": "apply" }),
                                ) {
                                    Ok(gp) => {
                                        if !gp.ok
                                            && let Some(ref m) = gp.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        let h_gp = handle.clone();
                                        h_gp.update(move |s, _| s.pc.gpu_profile = Some(gp));
                                    }
                                    Err(_) => problems.push("GPU profile not applied".into()),
                                }
                            }
                            for sid in &screen_ids {
                                match call_sync::<DisplayState>(
                                    &rpc,
                                    METHOD_GPU_DISPLAY,
                                    &json!({ "action": "set", "display": sid, "best": true }),
                                ) {
                                    Ok(disp) => {
                                        if !disp.ok
                                            && let Some(ref m) = disp.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        let h_d = handle.clone();
                                        h_d.update(move |s, _| s.pc.display_state = Some(disp));
                                    }
                                    Err(_) => problems.push("Display mode not changed".into()),
                                }
                            }
                            if hdr_todo {
                                match call_sync::<LauncherSettings>(
                                    &rpc,
                                    METHOD_SETTINGS_SET,
                                    &json!({ "hdrForGames": true }),
                                ) {
                                    Ok(ns) => handle.update(move |s, _| s.settings = ns),
                                    Err(_) => problems.push("HDR preference not saved".into()),
                                }
                            }
                            if fg_todo {
                                match call_sync::<LauncherSettings>(
                                    &rpc,
                                    METHOD_SETTINGS_SET,
                                    &json!({ "frameGenEverywhere": true }),
                                ) {
                                    Ok(ns) => handle.update(move |s, _| s.settings = ns),
                                    Err(_) => {
                                        problems.push("Frame generation setting not saved".into())
                                    }
                                }
                            }
                            if problems.is_empty() {
                                (
                                    AutoTuneStageStatus::Done,
                                    "Driver profile, best refresh & output scaling applied".into(),
                                )
                            } else {
                                (AutoTuneStageStatus::Warn, problems.join(" · "))
                            }
                        }
                        AutoTuneStageId::System => {
                            let mut problems: Vec<String> = Vec::new();
                            let mut needs_restart = false;
                            if sys_todo {
                                match call_sync::<SystemTuningState>(
                                    &rpc,
                                    METHOD_SYSTEM_TUNING,
                                    &json!({ "action": "playbook" }),
                                ) {
                                    Ok(t) => {
                                        if !t.ok
                                            && let Some(ref m) = t.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        needs_restart |= t.restart;
                                        handle.update(move |s, _| s.pc.tuning = Some(t));
                                    }
                                    Err(_) => {
                                        problems.push("System tweaks could not finish".into())
                                    }
                                }
                            }
                            if let Some(act) = interrupts_action {
                                match call_sync::<InterruptsState>(
                                    &rpc,
                                    METHOD_SYSTEM_INTERRUPTS,
                                    &json!({ "action": act }),
                                ) {
                                    Ok(ir) => {
                                        if !ir.ok
                                            && let Some(ref m) = ir.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        needs_restart |= ir.restart;
                                        handle.update(move |s, _| s.pc.interrupts = Some(ir));
                                    }
                                    Err(_) => problems.push("Interrupts could not finish".into()),
                                }
                            }
                            if net_todo {
                                match call_sync::<PcHealth>(
                                    &rpc,
                                    METHOD_SYSTEM_HEALTH,
                                    &json!({ "action": "network" }),
                                ) {
                                    Ok(hp) => {
                                        if !hp.ok
                                            && let Some(ref m) = hp.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        handle.update(move |s, _| s.pc.health = Some(hp));
                                    }
                                    Err(_) => {
                                        problems.push("Network tweaks could not finish".into())
                                    }
                                }
                            }
                            if drives_todo {
                                match call_sync::<DrivesReply>(
                                    &rpc,
                                    METHOD_SYSTEM_DRIVES,
                                    &json!({ "action": "all" }),
                                ) {
                                    Ok(dr) => {
                                        if !dr.ok
                                            && let Some(ref m) = dr.message
                                        {
                                            problems.push(trim_stop(m));
                                        }
                                        needs_restart |= dr.fixes.iter().any(|f| f.restart);
                                        handle.update(move |s, _| s.pc.drives = Some(dr));
                                    }
                                    Err(_) => problems.push("Drive tweaks could not finish".into()),
                                }
                            }
                            if !problems.is_empty() {
                                (AutoTuneStageStatus::Warn, problems.join(" · "))
                            } else if needs_restart {
                                (
                                    AutoTuneStageStatus::Done,
                                    "Full deep Windows 11 playbook, interrupts & network applied (restart to finish)".into(),
                                )
                            } else {
                                (
                                    AutoTuneStageStatus::Done,
                                    "Full deep Windows 11 playbook, interrupts, services & network optimal".into(),
                                )
                            }
                        }
                        AutoTuneStageId::Bloat => {
                            if bloat_ids.is_empty() {
                                (
                                    AutoTuneStageStatus::Done,
                                    "No bloat Store apps installed".into(),
                                )
                            } else {
                                match call_sync::<AppsPackagesPlanReply>(
                                    &rpc,
                                    METHOD_APPS_PACKAGES_PLAN,
                                    &json!({ "ids": bloat_ids, "everyone": false }),
                                ) {
                                    Ok(plan) => {
                                        if let (true, Some(token)) = (plan.ok, plan.token) {
                                            match call_sync::<AppsPackagesRemoveReply>(
                                                &rpc,
                                                METHOD_APPS_PACKAGES_REMOVE,
                                                &json!({ "token": token }),
                                            ) {
                                                Ok(rem) => {
                                                    if !rem.ok && rem.removed == 0 {
                                                        (
                                                            AutoTuneStageStatus::Warn,
                                                            rem.message
                                                                .as_deref()
                                                                .map(trim_stop)
                                                                .unwrap_or_else(|| {
                                                                    "Bloat removal could not finish"
                                                                        .into()
                                                                }),
                                                        )
                                                    } else {
                                                        let s =
                                                            if rem.removed == 1 { "" } else { "s" };
                                                        (
                                                            AutoTuneStageStatus::Done,
                                                            format!(
                                                                "{} bloat app{s} removed (restore anytime in Apps)",
                                                                rem.removed
                                                            ),
                                                        )
                                                    }
                                                }
                                                Err(_) => (
                                                    AutoTuneStageStatus::Warn,
                                                    "Bloat removal could not finish".into(),
                                                ),
                                            }
                                        } else {
                                            (
                                                AutoTuneStageStatus::Done,
                                                plan.message
                                                    .as_deref()
                                                    .map(trim_stop)
                                                    .unwrap_or_else(|| "Nothing to remove".into()),
                                            )
                                        }
                                    }
                                    Err(_) => (
                                        AutoTuneStageStatus::Warn,
                                        "Bloat removal could not finish".into(),
                                    ),
                                }
                            }
                        }
                        AutoTuneStageId::Cleanup => {
                            match call_sync::<JunkScanReply>(&rpc, METHOD_JUNK_SCAN, &json!({})) {
                                Ok(scan) => {
                                    let ids: Vec<String> = scan
                                        .items
                                        .into_iter()
                                        .filter(|i| i.id != "bin" && i.bytes > 0)
                                        .map(|i| i.id)
                                        .collect();
                                    if ids.is_empty() {
                                        (
                                            AutoTuneStageStatus::Done,
                                            "Caches already clean (cookies & logins kept)".into(),
                                        )
                                    } else {
                                        match call_sync::<JunkCleanReply>(
                                            &rpc,
                                            METHOD_JUNK_CLEAN,
                                            &json!({ "ids": ids }),
                                        ) {
                                            Ok(clean) if clean.ok => {
                                                let bytes = clean.bytes.unwrap_or(0);
                                                let freed = if bytes > 0 {
                                                    format!("{} freed", format_bytes(bytes))
                                                } else {
                                                    "Temp and browser caches checked".into()
                                                };
                                                if clean.message.is_some() {
                                                    let suffix = if bytes > 0 {
                                                        "in-use files skipped (cookies & logins kept)"
                                                    } else {
                                                        "caches clean · in-use files skipped"
                                                    };
                                                    (
                                                        AutoTuneStageStatus::Done,
                                                        format!("{freed} · {suffix}"),
                                                    )
                                                } else {
                                                    (
                                                        AutoTuneStageStatus::Done,
                                                        format!(
                                                            "{freed} across {} files (cookies & logins kept)",
                                                            clean.files.unwrap_or(0)
                                                        ),
                                                    )
                                                }
                                            }
                                            Ok(clean) => (
                                                AutoTuneStageStatus::Warn,
                                                clean
                                                    .message
                                                    .as_deref()
                                                    .map(trim_stop)
                                                    .unwrap_or_else(|| {
                                                        "Cleanup could not finish".into()
                                                    }),
                                            ),
                                            Err(_) => (
                                                AutoTuneStageStatus::Warn,
                                                "Cleanup could not finish".into(),
                                            ),
                                        }
                                    }
                                }
                                Err(_) => {
                                    (AutoTuneStageStatus::Warn, "Cleanup could not finish".into())
                                }
                            }
                        }
                        AutoTuneStageId::Drivers => {
                            let gpu = call_sync::<DriverState>(
                                &rpc,
                                METHOD_GPU_DRIVER,
                                &json!({ "action": "state", "branch": branch }),
                            )
                            .ok();
                            let hp =
                                call_sync::<PcHealth>(&rpc, METHOD_SYSTEM_HEALTH, &json!({})).ok();
                            let has_gpu =
                                gpu.as_ref().is_some_and(|d| d.update && d.latest.is_some());
                            let has_chip = hp.as_ref().is_some_and(|h| {
                                h.items.iter().any(|i| {
                                    i.key == "chipset" && !i.applied && !i.current && !i.held
                                })
                            });
                            handle.update(move |s, _| {
                                if let Some(g) = gpu {
                                    s.pc.gpu_driver = Some(g);
                                }
                                if let Some(h) = hp {
                                    s.pc.health = Some(h);
                                }
                                s.load_pc_exo_ai();
                            });
                            if has_gpu || has_chip {
                                (
                                    AutoTuneStageStatus::Done,
                                    "Driver updates ready in Updates".into(),
                                )
                            } else {
                                (
                                    AutoTuneStageStatus::Done,
                                    "Graphics & chipset drivers up to date".into(),
                                )
                            }
                        }
                    };
                    if status == AutoTuneStageStatus::Warn {
                        warned = true;
                    }
                    set_stage(&handle, id, status, Some(detail));
                }

                let now = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map(|d| d.as_secs())
                    .unwrap_or(1);
                handle.update(move |s, _| {
                    s.pc.auto_tune_running = false;
                    s.pc.auto_tune_finished = true;
                    if !warned {
                        s.pc.auto_tune_completed_at = Some(now);
                    }
                });
            });
        }
    }

    pub fn run_system_tune_action(&mut self, part: &str, action: &str) {
        self.pc.tune_errors.remove(part);
        self.pc.armed_confirm = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let part_s = part.to_string();
            let action_s = action.to_string();
            let payload = if action == "start" {
                json!({ "action": action_s, "part": part_s, "mode": "faster" })
            } else {
                json!({ "action": action_s, "part": part_s })
            };
            rpc.call::<_, AutoTuneState>(METHOD_SYSTEM_TUNE, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(next) => {
                        if !next.ok {
                            let fallback = if action_s == "start" {
                                "That didn’t start. Try again."
                            } else {
                                "Not changed. Try again."
                            };
                            let msg = next
                                .message
                                .as_deref()
                                .map(trim_stop)
                                .filter(|m| !m.is_empty())
                                .unwrap_or_else(|| fallback.into());
                            s.pc.tune_errors.insert(part_s, msg);
                        }
                        s.pc.auto_tune = Some(next);
                    }
                    Err(_) => {
                        let fallback = if action_s == "start" {
                            "That didn’t start. Try again."
                        } else {
                            "Not changed. Try again."
                        };
                        s.pc.tune_errors.insert(part_s, fallback.into());
                    }
                });
            });
        }
    }

    pub fn autopilot_remove_rule(&mut self, field: &str, name: &str) {
        let Some(ref ap) = self.pc.autopilot else {
            return;
        };
        let next_list: Vec<String> = if field == "alwaysDemote" {
            ap.always_demote
                .iter()
                .filter(|x| x.as_str() != name)
                .cloned()
                .collect()
        } else {
            ap.suspend
                .iter()
                .filter(|x| x.as_str() != name)
                .cloned()
                .collect()
        };
        self.pc.autopilot_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let payload = if field == "alwaysDemote" {
                json!({ "alwaysDemote": next_list })
            } else {
                json!({ "suspend": next_list })
            };
            rpc.call::<_, AutopilotSetReply>(METHOD_AUTOPILOT_SET, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(ans) => {
                        if !ans.ok {
                            s.pc.autopilot_error = Some(
                                ans.message
                                    .unwrap_or_else(|| "Windows didn’t take that.".into()),
                            );
                        }
                        s.pc.autopilot = Some(ans.state);
                    }
                    Err(_) => s.pc.autopilot_error = Some("Not saved. Try again.".into()),
                });
            });
        }
    }

    pub fn autopilot_toggle_memory_integrity(&mut self, turn_on_tweak: bool) {
        self.pc.armed_confirm = None;
        self.pc.autopilot_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let act = if turn_on_tweak { "on" } else { "off" };
            let rpc2 = rpc.clone();
            rpc.call::<_, SystemTuningState>(
                METHOD_SYSTEM_TUNING,
                &json!({ "action": act, "item": "integrity" }),
                move |res| {
                    let h2 = handle.clone();
                    if let Ok(t) = res {
                        handle.update(move |s, _| s.pc.tuning = Some(t));
                    }
                    rpc2.call::<_, AutopilotState>(METHOD_AUTOPILOT_STATE, &json!({}), move |ap| {
                        if let Ok(state) = ap {
                            h2.update(move |s, _| s.pc.autopilot = Some(state));
                        }
                    });
                },
            );
        }
    }

    pub fn run_health_action(&mut self, action: &str) {
        self.pc.network_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let act = action.to_string();
            rpc.call::<_, PcHealth>(
                METHOD_SYSTEM_HEALTH,
                &json!({ "action": act }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(hp) => {
                            if !hp.ok {
                                let msg = hp
                                    .message
                                    .as_deref()
                                    .map(trim_stop)
                                    .filter(|m| !m.is_empty())
                                    .unwrap_or_else(|| "Not changed. Try again.".into());
                                if act == "netcheck" {
                                    s.pc.network_error = Some(("check".into(), msg));
                                } else {
                                    s.notice = Some(msg);
                                }
                            }
                            s.pc.health = Some(hp);
                            s.refresh_auto_tune_stages_if_idle();
                        }
                        Err(_) => {
                            if act == "netcheck" {
                                s.pc.network_error = Some((
                                    "check".into(),
                                    "The connection wasn’t checked. Try again.".into(),
                                ));
                            } else {
                                s.notice = Some("Not changed. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn run_network_item_toggle(&mut self, key: &str, item: &str, on: bool) {
        if self.pc.network_busy.is_some() {
            return;
        }
        self.pc.network_busy = Some(key.to_string());
        self.pc.network_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_s = key.to_string();
            let act = if on { "network-on" } else { "network-off" };
            rpc.call::<_, PcHealth>(
                METHOD_SYSTEM_HEALTH,
                &json!({ "action": act, "item": item }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.network_busy = None;
                        match res {
                            Ok(hp) => {
                                if !hp.ok {
                                    let msg = hp
                                        .message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| "Not changed. Try again.".into());
                                    s.pc.network_error = Some((key_s, msg));
                                }
                                s.pc.health = Some(hp);
                                s.refresh_auto_tune_stages_if_idle();
                            }
                            Err(_) => {
                                s.pc.network_error =
                                    Some((key_s, "Not changed. Try again.".into()));
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn run_system_tuning_item(&mut self, key: &str, on: bool) {
        if self.pc.tuning_busy.is_some() {
            return;
        }
        self.pc.tuning_busy = Some(key.to_string());
        self.pc.tuning_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_s = key.to_string();
            let act = if on { "on" } else { "off" };
            rpc.call::<_, SystemTuningState>(
                METHOD_SYSTEM_TUNING,
                &json!({ "action": act, "item": key_s }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.tuning_busy = None;
                        match res {
                            Ok(t) => {
                                if !t.ok {
                                    let msg = t
                                        .message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| "Not changed. Try again.".into());
                                    s.pc.tuning_error = Some((key_s, msg));
                                }
                                s.pc.tuning = Some(t);
                                s.refresh_auto_tune_stages_if_idle();
                            }
                            Err(_) => {
                                s.pc.tuning_error = Some((key_s, "Not changed. Try again.".into()));
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn run_best_setup_all(&mut self) {
        if self.pc.best_setup_running.is_some() {
            return;
        }
        let pending: Vec<(&'static str, String)> = best_steps_list(&self.pc, &self.settings)
            .into_iter()
            .filter(|s| !s.done)
            .map(|s| (s.key, s.what))
            .collect();
        if pending.is_empty() {
            return;
        }
        self.pc.best_setup_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            std::thread::spawn(move || {
                let mut failures: Vec<String> = Vec::new();
                for (key, what) in pending {
                    handle.update(move |s, _| s.pc.best_setup_running = Some(key.into()));
                    let problem: Option<String> = match key {
                        "power" => call_sync::<PcHealth>(
                            &rpc,
                            METHOD_SYSTEM_HEALTH,
                            &json!({ "action": "apply" }),
                        )
                        .map(|r| (!r.ok).then(|| r.message.unwrap_or_else(|| "Not changed".into())))
                        .unwrap_or_else(|_| Some("Not changed".into())),
                        "profile" => call_sync::<GpuProfile>(
                            &rpc,
                            METHOD_GPU_PROFILE,
                            &json!({ "action": "apply" }),
                        )
                        .map(|r| (!r.ok).then(|| r.message.unwrap_or_else(|| "Not changed".into())))
                        .unwrap_or_else(|_| Some("Not changed".into())),
                        "network" => call_sync::<PcHealth>(
                            &rpc,
                            METHOD_SYSTEM_HEALTH,
                            &json!({ "action": "network" }),
                        )
                        .map(|r| (!r.ok).then(|| r.message.unwrap_or_else(|| "Not changed".into())))
                        .unwrap_or_else(|_| Some("Not changed".into())),
                        "system" => call_sync::<SystemTuningState>(
                            &rpc,
                            METHOD_SYSTEM_TUNING,
                            &json!({ "action": "apply" }),
                        )
                        .map(|r| (!r.ok).then(|| r.message.unwrap_or_else(|| "Not changed".into())))
                        .unwrap_or_else(|_| Some("Not changed".into())),
                        "drives" => call_sync::<DrivesReply>(
                            &rpc,
                            METHOD_SYSTEM_DRIVES,
                            &json!({ "action": "all" }),
                        )
                        .map(|r| (!r.ok).then(|| r.message.unwrap_or_else(|| "Not changed".into())))
                        .unwrap_or_else(|_| Some("Not changed".into())),
                        "hdr" => match call_sync::<LauncherSettings>(
                            &rpc,
                            METHOD_SETTINGS_SET,
                            &json!({ "hdrForGames": true }),
                        ) {
                            Ok(ns) => {
                                handle.update(move |s, _| s.settings = ns);
                                None
                            }
                            Err(_) => Some("Not saved".into()),
                        },
                        "frameGen" => match call_sync::<LauncherSettings>(
                            &rpc,
                            METHOD_SETTINGS_SET,
                            &json!({ "frameGenEverywhere": true }),
                        ) {
                            Ok(ns) => {
                                handle.update(move |s, _| s.settings = ns);
                                None
                            }
                            Err(_) => Some("Not saved".into()),
                        },
                        _ => None,
                    };
                    if let Some(p) = problem {
                        failures.push(format!("{what}: {}", trim_stop(&p)));
                    }
                }
                handle.update(move |s, _| {
                    s.pc.best_setup_running = None;
                    if !failures.is_empty() {
                        s.pc.best_setup_error =
                            Some(format!("{}. Try again.", failures.join(". ")));
                    }
                    s.load_pc_exo_ai();
                });
            });
        }
    }

    pub fn run_interrupts_action(&mut self, action: &str, device: Option<&str>) {
        self.pc.interrupts_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let payload = match device {
                Some(dev) => json!({ "action": action, "device": dev }),
                None => json!({ "action": action }),
            };
            rpc.call::<_, InterruptsState>(METHOD_SYSTEM_INTERRUPTS, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(ir) => {
                        if !ir.ok {
                            s.pc.interrupts_error = Some(
                                ir.message
                                    .as_deref()
                                    .map(trim_stop)
                                    .filter(|m| !m.is_empty())
                                    .unwrap_or_else(|| "Not changed. Try again.".into()),
                            );
                        }
                        s.pc.interrupts = Some(ir);
                        s.refresh_auto_tune_stages_if_idle();
                    }
                    Err(_) => s.pc.interrupts_error = Some("Not changed. Try again.".into()),
                });
            });
        }
    }

    pub fn run_gpu_profile_action(&mut self, action: &str) {
        self.pc.gpu_profile_error = None;
        let vendor = self.pc.gpu_profile.as_ref().and_then(|p| p.vendor.clone());
        let vrr = self.pc.gpu_profile.as_ref().is_some_and(|p| p.screen.vrr);
        let chosen_ll = self.pc.gpu_low_latency_choice.take();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut body = serde_json::Map::new();
            body.insert("action".into(), json!(action));
            if action == "apply" {
                match vendor.as_deref() {
                    Some("nvidia") => {
                        body.insert("lowLatency".into(), json!("on"));
                        if vrr {
                            body.insert("vrr".into(), json!(true));
                        }
                    }
                    Some("intel") => {
                        let ll = chosen_ll
                            .or_else(|| {
                                self.pc
                                    .gpu_profile
                                    .as_ref()
                                    .and_then(|p| p.low_latency.clone())
                            })
                            .unwrap_or_else(|| "on".into());
                        body.insert("lowLatency".into(), json!(ll));
                    }
                    _ => {}
                }
            }
            rpc.call::<_, GpuProfile>(METHOD_GPU_PROFILE, &Value::Object(body), move |res| {
                handle.update(move |s, _| match res {
                    Ok(gp) => {
                        if !gp.ok {
                            s.pc.gpu_profile_error = Some(
                                gp.message
                                    .as_deref()
                                    .map(trim_stop)
                                    .filter(|m| !m.is_empty())
                                    .unwrap_or_else(|| {
                                        "The driver did not answer. Try again.".into()
                                    }),
                            );
                        }
                        s.pc.gpu_profile = Some(gp);
                        s.refresh_auto_tune_stages_if_idle();
                    }
                    Err(_) => s.pc.gpu_profile_error = Some("Not changed. Try again.".into()),
                });
            });
        }
    }

    pub fn set_gpu_driver_branch(&mut self, branch: &str) {
        self.pc.gpu_driver_branch = branch.to_string();
        self.pc.gpu_driver_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let b = self.pc.gpu_driver_branch.clone();
            rpc.call::<_, DriverState>(
                METHOD_GPU_DRIVER,
                &json!({ "action": "state", "branch": b }),
                move |res| {
                    if let Ok(drv) = res {
                        handle.update(move |s, _| {
                            s.pc.gpu_driver = Some(drv);
                            s.refresh_auto_tune_stages_if_idle();
                        });
                    }
                },
            );
        }
    }

    pub fn start_gpu_driver_install(&mut self, go_back: bool) {
        self.pc.gpu_driver_consent_open = false;
        self.pc.gpu_driver_error = None;
        self.pc.gpu_driver_starting = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let action = if go_back { "goback" } else { "update" };
            let branch = self.pc.gpu_driver_branch.clone();
            rpc.call::<_, DriverState>(
                METHOD_GPU_DRIVER,
                &json!({ "action": action, "branch": branch }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(ans) => {
                            if !ans.ok {
                                s.pc.gpu_driver_starting = false;
                                s.pc.gpu_driver_error = Some(
                                    ans.message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| {
                                            "The update didn’t start. Try again.".into()
                                        }),
                                );
                            }
                            s.pc.gpu_driver = Some(ans);
                        }
                        Err(_) => {
                            s.pc.gpu_driver_starting = false;
                            s.pc.gpu_driver_error =
                                Some("The update didn’t start. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn run_gpu_display_change(
        &mut self,
        action: &str,
        key: &str,
        mut fields: serde_json::Map<String, Value>,
    ) {
        if self.pc.display_busy.is_some() {
            return;
        }
        self.pc.display_busy = Some(key.to_string());
        self.pc.display_error = None;
        fields.insert("action".into(), json!(action));
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_s = key.to_string();
            rpc.call::<_, DisplayState>(METHOD_GPU_DISPLAY, &Value::Object(fields), move |res| {
                handle.update(move |s, _| {
                    s.pc.display_busy = None;
                    match res {
                        Ok(next) => {
                            if !next.ok {
                                let msg = next
                                    .message
                                    .as_deref()
                                    .map(trim_stop)
                                    .filter(|m| !m.is_empty())
                                    .unwrap_or_else(|| "Not changed. Try again".into());
                                s.pc.display_error = Some((key_s, msg));
                            }
                            s.pc.display_state = Some(next);
                            s.refresh_auto_tune_stages_if_idle();
                        }
                        Err(_) => {
                            s.pc.display_error = Some((key_s, "Not changed. Try again".into()));
                        }
                    }
                });
            });
        }
    }

    pub fn toggle_gpu_afmf3(&mut self, on: bool) {
        if self.pc.afmf3_busy {
            return;
        }
        self.pc.afmf3_busy = true;
        self.pc.afmf3_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, Afmf3State>(METHOD_GPU_AFMF3, &json!({ "on": on }), move |res| {
                handle.update(move |s, _| {
                    s.pc.afmf3_busy = false;
                    match res {
                        Ok(next) => {
                            if !next.ok {
                                s.pc.afmf3_error = Some(
                                    next.message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| "Not changed. Try again.".into()),
                                );
                            }
                            s.pc.afmf3 = Some(next);
                        }
                        Err(_) => s.pc.afmf3_error = Some("Not changed. Try again.".into()),
                    }
                });
            });
        }
    }

    pub fn open_gpu_control_panel(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_GPU_CONTROL_PANEL, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(ans) if !ans.ok => {
                        s.notice = ans
                            .message
                            .or_else(|| Some("Control panel didn’t open.".into()));
                    }
                    Err(_) => s.notice = Some("Control panel didn’t open.".into()),
                    _ => {}
                });
            });
        }
    }

    pub fn run_drivers_update_all(&mut self) {
        self.pc.drivers_error = None;
        self.pc.driver_steps.clear();
        let has_catalog = self
            .pc
            .drivers_state
            .as_ref()
            .is_some_and(|d| d.items.iter().any(|i| i.how == "install" && !i.app));
        let has_chipset = self.pc.health.as_ref().is_some_and(|h| {
            h.items
                .iter()
                .any(|i| i.key == "chipset" && !i.current && !i.applied && !i.busy)
        });
        let has_gpu = self
            .pc
            .gpu_driver
            .as_ref()
            .is_some_and(|d| d.update || d.advised.as_ref().is_some_and(|a| a.back.is_some()));

        if !has_catalog {
            if has_chipset {
                self.run_health_action("chipset");
            } else if has_gpu {
                self.pc.gpu_driver_consent_open = true;
            }
            return;
        }
        self.pc.drivers_updating_all = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, DriversState>(
                METHOD_SYSTEM_DRIVERS,
                &json!({ "action": "update-all" }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(ans) => {
                            if !ans.ok {
                                s.pc.drivers_updating_all = false;
                                s.pc.drivers_error = Some(
                                    ans.message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| {
                                            "The update didn’t start. Try again.".into()
                                        }),
                                );
                            }
                            s.pc.drivers_state = Some(ans);
                        }
                        Err(_) => {
                            s.pc.drivers_updating_all = false;
                            s.pc.drivers_error = Some("The update didn’t start. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn run_driver_install(&mut self, id: &str) {
        self.pc.drivers_error = None;
        self.pc.armed_confirm = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_s = id.to_string();
            rpc.call::<_, DriversState>(
                METHOD_SYSTEM_DRIVERS,
                &json!({ "action": "install", "id": id_s }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(ans) => {
                            if !ans.ok {
                                s.pc.drivers_error = Some(
                                    ans.message
                                        .as_deref()
                                        .map(trim_stop)
                                        .filter(|m| !m.is_empty())
                                        .unwrap_or_else(|| {
                                            "The install didn’t start. Try again.".into()
                                        }),
                                );
                            }
                            s.pc.drivers_state = Some(ans);
                        }
                        Err(_) => {
                            s.pc.drivers_error =
                                Some("The install didn’t start. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn run_bios_update(&mut self, action: &str) {
        self.pc.armed_confirm = None;
        match action {
            "bios" | "recovery" => self.pc.board_restart_error = None,
            "windows" => {
                self.pc.auto_tune_restarting = true;
                self.pc.auto_tune_cinema_error = None;
            }
            _ => {
                self.pc.bios_busy = Some(action.to_string());
                self.pc.bios_error = None;
                self.pc.bios_progress = None;
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let act = action.to_string();
            rpc.call::<_, BiosUpdateReply>(
                METHOD_BIOS_UPDATE,
                &json!({ "action": act }),
                move |res| {
                    handle.update(move |s, _| match act.as_str() {
                        "bios" | "recovery" => {
                            if let Ok(ans) = res {
                                if !ans.ok {
                                    s.pc.board_restart_error = Some((
                                        act,
                                        ans.message
                                            .as_deref()
                                            .map(trim_stop)
                                            .filter(|m| !m.is_empty())
                                            .unwrap_or_else(|| "Didn’t restart".into()),
                                    ));
                                }
                            } else {
                                s.pc.board_restart_error = Some((act, "Didn’t restart".into()));
                            }
                        }
                        "windows" => {
                            let err = match res {
                                Ok(ans) if !ans.ok => Some(ans.message.unwrap_or_else(|| {
                                    "Windows did not restart. Restart from the Start menu.".into()
                                })),
                                Err(_) => Some(
                                    "Windows did not restart. Restart from the Start menu.".into(),
                                ),
                                _ => None,
                            };
                            if let Some(e) = err {
                                s.pc.auto_tune_restarting = false;
                                s.pc.auto_tune_cinema_error = Some(e);
                            }
                        }
                        _ => {
                            s.pc.bios_busy = None;
                            match res {
                                Ok(ans) => {
                                    s.pc.bios_staged = ans.staged;
                                    if !ans.ok {
                                        s.pc.bios_error = Some(
                                            ans.message
                                                .as_deref()
                                                .map(trim_stop)
                                                .filter(|m| !m.is_empty())
                                                .unwrap_or_else(|| {
                                                    "It didn’t work. Try again.".into()
                                                }),
                                        );
                                    }
                                }
                                Err(_) => {
                                    s.pc.bios_error = Some("It didn’t work. Try again.".into());
                                }
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn run_runtimes_install(&mut self, mode: &str) {
        if self.pc.runtimes_running.is_some() {
            return;
        }
        self.pc.runtimes_running = Some(mode.to_string());
        self.pc.runtimes_results.clear();
        self.pc.runtimes_progress = None;
        self.pc.runtimes_note = None;
        self.pc.runtimes_canceling = false;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mode_s = mode.to_string();
            rpc.call::<_, RuntimesInstallReply>(
                METHOD_RUNTIMES_INSTALL,
                &json!({ "mode": mode_s }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.runtimes_running = None;
                        s.pc.runtimes_canceling = false;
                        s.pc.runtimes_progress = None;
                        match res {
                            Ok(reply) => {
                                if let Some(items) = reply.items {
                                    s.pc.runtimes = Some(items);
                                }
                                let mut installed = 0usize;
                                let mut newer = 0usize;
                                let mut restart = 0usize;
                                let mut failed = 0usize;
                                for r in &reply.results {
                                    match r.outcome.as_str() {
                                        "installed" => installed += 1,
                                        "newer" => newer += 1,
                                        "restart" => restart += 1,
                                        "failed" => failed += 1,
                                        _ => {}
                                    }
                                    s.pc.runtimes_results.insert(r.id.clone(), r.clone());
                                }
                                if reply.declined || reply.canceled || reply.results.is_empty() {
                                    if let Some(m) = reply.message {
                                        s.pc.runtimes_note =
                                            Some((m, !reply.ok && !reply.canceled));
                                    }
                                } else {
                                    let mut parts = Vec::new();
                                    if installed > 0 {
                                        parts.push(format!("{installed} installed"));
                                    }
                                    if newer > 0 {
                                        parts.push(format!("{newer} already newer"));
                                    }
                                    if restart > 0 {
                                        parts.push(format!(
                                            "{restart} {} a restart",
                                            if restart == 1 { "needs" } else { "need" }
                                        ));
                                    }
                                    if failed > 0 {
                                        parts.push(format!("{failed} failed"));
                                    }
                                    let text = if parts.is_empty() {
                                        "Nothing needed installing.".into()
                                    } else {
                                        format!("{}.", parts.join(", "))
                                    };
                                    s.pc.runtimes_note = Some((text, failed > 0));
                                }
                            }
                            Err(_) => {
                                s.pc.runtimes_note = Some((
                                    "The runtimes couldn’t be installed. Try again.".into(),
                                    true,
                                ));
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn run_runtimes_cancel(&mut self) {
        self.pc.runtimes_canceling = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_RUNTIMES_CANCEL, &json!({}), move |res| {
                if let Ok(r) = res
                    && !r.ok
                    && let Some(m) = r.message
                {
                    handle.update(move |s, _| s.pc.runtimes_note = Some((m, false)));
                }
            });
        }
    }

    pub fn apply_shaders_snapshot_value(&mut self, snap: &Value) {
        let games = parse_shader_snap_games(snap);
        if !games.is_empty() || snap.get("games").is_some() {
            self.pc.shader_snap_games = games;
        }
        if let Some(q) = snap.get("queue") {
            self.apply_shaders_queue_value(q);
        }
        if let Some(app) = snap.get("app") {
            self.pc.shaders_scanning = app
                .get("scanning")
                .and_then(|v| v.as_bool())
                .unwrap_or(false);
            if let Some(cache) = app.get("cache") {
                self.pc.shaders_cache_off = cache
                    .get("off")
                    .and_then(|v| v.as_str())
                    .filter(|s| !s.is_empty())
                    .map(str::to_string);
                self.pc.shaders_old_driver_note = cache
                    .get("oldDriverNote")
                    .and_then(|v| v.as_str())
                    .filter(|s| !s.is_empty())
                    .map(str::to_string);
            }
        }
    }

    pub fn apply_shaders_queue_value(&mut self, q: &Value) {
        self.pc.shaders_queue_running = q.get("running").and_then(|v| v.as_bool()).unwrap_or(false);
        self.pc.shaders_queue_paused = q.get("paused").and_then(|v| v.as_bool()).unwrap_or(false);
        self.pc.shaders_queue_can_start =
            q.get("canStart").and_then(|v| v.as_bool()).unwrap_or(false);
        self.pc.shaders_queue_current_id = q
            .get("current")
            .and_then(|c| c.get("id"))
            .and_then(|v| v.as_str())
            .map(str::to_string);
        self.pc.shaders_queue_current_pct = q
            .get("current")
            .and_then(|c| c.get("percent"))
            .and_then(|v| v.as_f64());
        self.pc.shaders_queue_waiting = q
            .get("waiting")
            .and_then(|v| v.as_array())
            .map(|arr| {
                arr.iter()
                    .filter_map(|item| item.get("id").and_then(|v| v.as_str()).map(str::to_string))
                    .collect()
            })
            .unwrap_or_default();
    }

    pub fn run_shaders_steps(&mut self, key: &str, steps: Vec<(&'static str, Value)>) {
        self.pc.shaders_busy = Some(key.to_string());
        self.pc.shaders_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            std::thread::spawn(move || {
                let mut err_msg: Option<String> = None;
                for (method, params) in steps {
                    match call_sync::<Value>(
                        &rpc,
                        METHOD_SHADERS_CALL,
                        &json!({ "method": method, "params": params }),
                    ) {
                        Ok(v) => {
                            if v.get("ok").and_then(|b| b.as_bool()) == Some(false) {
                                err_msg = v
                                    .get("message")
                                    .and_then(|m| m.as_str())
                                    .map(str::to_string)
                                    .or_else(|| {
                                        Some("The shader engine did not answer. Try again.".into())
                                    });
                                break;
                            }
                        }
                        Err(_) => {
                            err_msg = Some("The shader engine did not answer. Try again.".into());
                            break;
                        }
                    }
                }
                let snap = call_sync::<Value>(
                    &rpc,
                    METHOD_SHADERS_CALL,
                    &json!({ "method": "snapshot", "params": {} }),
                )
                .ok()
                .and_then(|v| v.get("result").cloned());
                handle.update(move |s, _| {
                    s.pc.shaders_busy = None;
                    if let Some(e) = err_msg {
                        s.pc.shaders_error = Some(e);
                    }
                    if let Some(sv) = snap {
                        s.apply_shaders_snapshot_value(&sv);
                    }
                });
            });
        }
    }

    pub fn cancel_shader_prepare(&mut self, id: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_s = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_SHADERS_PREPARE_CANCEL,
                &json!({ "id": id_s }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && let Some(m) = r.message
                        {
                            s.notice = Some(m);
                        }
                        s.load_pc_exo_ai();
                    });
                },
            );
        }
    }

    pub fn scan_storage_space(&mut self) {
        if self.pc.storage_scanning {
            return;
        }
        self.pc.storage_scanning = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, DrivesReply>(
                METHOD_SYSTEM_DRIVES,
                &json!({ "space": true }),
                move |res| {
                    h1.update(move |s, _| {
                        s.pc.storage_scanning = false;
                        if let Ok(drv) = res {
                            s.pc.drives = Some(drv);
                        }
                    });
                },
            );
            let h2 = handle.clone();
            rpc.call::<_, CompressList>(METHOD_COMPRESS_LIST, &json!({}), move |res| {
                if let Ok(list) = res {
                    h2.update(move |s, _| {
                        s.pc.compress_potential_bytes = list.potential;
                        s.pc.compress_saved_bytes = list.saved;
                        s.pc.compress_list = Some(list);
                    });
                }
            });
        }
    }

    pub fn apply_drive_fix(&mut self, key: &str) {
        if self.pc.drive_fix_busy.is_some() {
            return;
        }
        self.pc.drive_fix_busy = Some(key.to_string());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_s = key.to_string();
            rpc.call::<_, DrivesReply>(
                METHOD_SYSTEM_DRIVES,
                &json!({ "action": key_s }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.drive_fix_busy = None;
                        if let Ok(drv) = res {
                            if !drv.ok
                                && let Some(ref m) = drv.message
                            {
                                s.notice = Some(m.clone());
                            }
                            s.pc.drives = Some(drv);
                            s.refresh_auto_tune_stages_if_idle();
                        }
                    });
                },
            );
        }
    }

    pub fn run_compress_all(&mut self) {
        self.pc.armed_confirm = None;
        self.pc.compress_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc2 = rpc.clone();
            rpc.call::<_, CompressAllReply>(METHOD_COMPRESS_ALL, &json!({}), move |res| {
                let h2 = handle.clone();
                handle.update(move |s, _| match res {
                    Ok(ans) => {
                        if let Some(m) = ans.message {
                            s.pc.compress_error = Some(m);
                        }
                    }
                    Err(_) => {
                        s.pc.compress_error = Some("Exo couldn’t start. Try again.".into());
                    }
                });
                rpc2.call::<_, CompressList>(METHOD_COMPRESS_LIST, &json!({}), move |lr| {
                    if let Ok(list) = lr {
                        h2.update(move |s, _| {
                            s.pc.compress_potential_bytes = list.potential;
                            s.pc.compress_saved_bytes = list.saved;
                            s.pc.compress_list = Some(list);
                        });
                    }
                });
            });
        }
    }

    pub fn run_compress_act(&mut self, id: &str, action: &str, anti_cheat: Option<String>) {
        self.pc.armed_confirm = None;
        self.pc.compress_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_s = id.to_string();
            let payload = match anti_cheat {
                Some(ac) if action == "allow" => {
                    json!({ "id": id_s, "action": action, "antiCheat": ac })
                }
                _ => json!({ "id": id_s, "action": action }),
            };
            rpc.call::<_, CompressActReply>(METHOD_COMPRESS_ACT, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(ans) => {
                        if let Some(comp) = ans.compress
                            && let Some(ref mut list) = s.pc.compress_list
                            && let Some(g) = list.games.iter_mut().find(|g| g.id == id_s)
                        {
                            g.compress = comp;
                        }
                        if !ans.ok
                            && let Some(m) = ans.message
                        {
                            s.pc.compress_error = Some(m);
                        }
                    }
                    Err(_) => s.pc.compress_error = Some("Exo couldn’t do that. Try again.".into()),
                });
            });
        }
    }

    pub fn set_compress_settings(&mut self, while_idle: Option<bool>, after_updates: Option<bool>) {
        self.pc.compress_error = None;
        let mut body = serde_json::Map::new();
        if let Some(v) = while_idle {
            body.insert("whileIdle".into(), json!(v));
        }
        if let Some(v) = after_updates {
            body.insert("afterUpdates".into(), json!(v));
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CompressSettingsReply>(
                METHOD_COMPRESS_SETTINGS,
                &Value::Object(body),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(ans) => {
                            if let Some(ref mut list) = s.pc.compress_list {
                                list.settings = ans.settings;
                            }
                        }
                        Err(_) => s.pc.compress_error = Some("Not saved. Try again.".into()),
                    });
                },
            );
        }
    }

    pub fn load_pc_apps_extras(&mut self, fresh_tidy: bool) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, AppFind>(
                METHOD_APPS_TIDY,
                &json!({ "fresh": fresh_tidy }),
                move |res| {
                    if let Ok(find) = res {
                        h1.update(move |s, _| {
                            for ac in &find.auto_cleared {
                                if !s.pc.apps_auto_cleared.iter().any(|x| x.id == ac.id) {
                                    s.pc.apps_auto_cleared.push(AutoClearedEntry {
                                        id: ac.id.clone(),
                                        line: format!(
                                            "{}: {}",
                                            ac.name,
                                            leftover_cleared_line(&ac.cleared)
                                        ),
                                        undone: false,
                                        error: None,
                                    });
                                }
                            }
                            if find.ok && !find.items.is_empty() {
                                s.pc.apps_tidy = Some(find_to_removed_entry(
                                    "tidy".into(),
                                    "Left by programs that are gone".into(),
                                    find,
                                ));
                            } else {
                                s.pc.apps_tidy = None;
                            }
                        });
                    }
                },
            );

            let h2 = handle.clone();
            rpc.call::<_, JunkScanReply>(METHOD_JUNK_SCAN, &json!({}), move |res| {
                if let Ok(scan) = res {
                    h2.update(move |s, _| s.pc.junk_items = Some(scan.items));
                }
            });

            let h3 = handle.clone();
            rpc.call::<_, InstallerCacheReply>(
                METHOD_APPS_INSTALLER_CACHE,
                &json!({}),
                move |res| {
                    if let Ok(cache) = res {
                        h3.update(move |s, _| s.pc.installer_cache = Some(cache));
                    }
                },
            );

            let h4 = handle.clone();
            rpc.call::<_, AppsPackagesReply>(METHOD_APPS_PACKAGES, &json!({}), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                {
                    h4.update(move |s, _| s.pc.windows_packages = reply.items);
                }
            });
        }
    }

    pub fn remove_single_app(&mut self, id: &str) {
        self.pc.armed_uninstall = None;
        self.pc.apps_errors.remove(id);
        let Some(app) = self
            .pc
            .apps_list
            .as_ref()
            .and_then(|l| l.programs.iter().find(|p| p.id == id))
            .cloned()
        else {
            return;
        };
        self.pc.apps_busy_id = Some(id.to_string());
        self.notice = Some("Starting application uninstaller…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let app_id = app.id.clone();
            let app_name = app.name.clone();
            if let Some(replaces) = app.replaces.clone() {
                rpc.call::<_, ActionResult>(
                    METHOD_REPLACED_REMOVE,
                    &json!({ "id": replaces }),
                    move |res| {
                        handle.update(move |s, _| {
                            s.pc.apps_busy_id = None;
                            match res {
                                Ok(r) if r.ok => {
                                    s.pc.apps_removed.insert(
                                        0,
                                        RemovedAppEntry {
                                            id: app_id,
                                            name: app_name.clone(),
                                            find: None,
                                            message: Some(format!("{app_name} is gone.")),
                                            cleared_line: None,
                                            undo_token: None,
                                            undone: false,
                                            review_open: false,
                                            picks: HashSet::new(),
                                            error: None,
                                        },
                                    );
                                }
                                Ok(r) => {
                                    s.pc.apps_errors.insert(
                                        app_id,
                                        r.message.unwrap_or_else(|| {
                                            format!("{app_name} wasn't removed. Try again.")
                                        }),
                                    );
                                }
                                Err(_) => {
                                    s.pc.apps_errors.insert(
                                        app_id,
                                        format!("{app_name} wasn't removed. Try again."),
                                    );
                                }
                            }
                            s.load_pc_apps();
                        });
                    },
                );
            } else {
                let method = if app.uninstaller {
                    METHOD_APPS_UNINSTALL
                } else {
                    METHOD_APPS_SCAN
                };
                rpc.call::<_, AppFind>(method, &json!({ "id": app_id }), move |res| {
                    handle.update(move |s, _| {
                        s.pc.apps_busy_id = None;
                        match res {
                            Ok(find) if find.ok => {
                                s.pc.apps_removed.retain(|r| r.id != app_id);
                                s.pc.apps_removed
                                    .insert(0, find_to_removed_entry(app_id, app_name, find));
                            }
                            Ok(find) => {
                                s.pc.apps_errors.insert(
                                    app_id,
                                    find.message.unwrap_or_else(|| {
                                        format!("{app_name} wasn't removed. Try again.")
                                    }),
                                );
                            }
                            Err(_) => {
                                s.pc.apps_errors.insert(
                                    app_id,
                                    format!("{app_name} wasn't removed. Try again."),
                                );
                            }
                        }
                        s.load_pc_apps();
                    });
                });
            }
        }
    }

    pub fn uninstall_many_apps(&mut self, ids: Vec<String>) {
        if ids.is_empty() || self.pc.apps_busy_id.is_some() || self.pc.apps_run.is_some() {
            return;
        }
        self.pc.armed_confirm = None;
        self.pc.apps_errors.remove("many");
        let programs = self
            .pc
            .apps_list
            .as_ref()
            .map(|l| l.programs.clone())
            .unwrap_or_default();
        if programs.is_empty() {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            std::thread::spawn(move || {
                let Ok(agreed) =
                    call_sync::<AppConfirmReply>(&rpc, METHOD_APPS_CONFIRM, &json!({ "ids": ids }))
                else {
                    handle.update(|s, _| {
                        s.pc.apps_errors
                            .insert("many".into(), "The apps weren’t removed.".into());
                    });
                    return;
                };
                if !agreed.skipped.is_empty() {
                    let skips = agreed.skipped.clone();
                    handle.update(move |s, _| {
                        for sk in skips {
                            s.pc.apps_errors.insert(sk.id, sk.reason);
                        }
                    });
                }
                if !agreed.ok {
                    let msg = agreed
                        .message
                        .unwrap_or_else(|| "Nothing was removed.".into());
                    handle.update(move |s, _| {
                        s.pc.apps_errors.insert("many".into(), msg);
                    });
                    return;
                }
                let total = agreed.ids.len();
                handle.update(move |s, _| s.pc.apps_run = Some((0, total)));
                match call_sync::<RestorePointReply>(&rpc, METHOD_APPS_RESTORE_POINT, &json!({})) {
                    Ok(pt) if !pt.ok && pt.declined => {
                        let msg = pt.message.unwrap_or_else(|| {
                            "Windows’ permission was declined. Nothing was removed.".into()
                        });
                        handle.update(move |s, _| {
                            s.pc.apps_run = None;
                            s.pc.apps_errors.insert("many".into(), msg);
                        });
                        return;
                    }
                    _ => {}
                }
                handle.update(|s, _| {
                    s.pc.apps_select_mode = false;
                    s.pc.apps_picked.clear();
                });

                let chosen: Vec<_> = agreed
                    .ids
                    .iter()
                    .filter_map(|id| programs.iter().find(|p| &p.id == id).cloned())
                    .collect();
                let chosen_len = chosen.len();
                let mut went = 0usize;
                for (idx, app) in chosen.iter().enumerate() {
                    let at = idx + 1;
                    let app_id = app.id.clone();
                    let app_name = app.name.clone();
                    handle.update(move |s, _| {
                        s.pc.apps_run = Some((at, chosen_len));
                        s.pc.apps_busy_id = Some(app_id.clone());
                    });
                    if let Some(ref rep) = app.replaces {
                        match call_sync::<ActionResult>(
                            &rpc,
                            METHOD_REPLACED_REMOVE,
                            &json!({ "id": rep }),
                        ) {
                            Ok(r) if r.ok => {
                                went += 1;
                                let aid = app.id.clone();
                                let aname = app_name.clone();
                                handle.update(move |s, _| {
                                    s.pc.apps_removed.insert(
                                        0,
                                        RemovedAppEntry {
                                            id: aid,
                                            name: aname.clone(),
                                            find: None,
                                            message: Some(format!("{aname} is gone.")),
                                            cleared_line: None,
                                            undo_token: None,
                                            undone: false,
                                            review_open: false,
                                            picks: HashSet::new(),
                                            error: None,
                                        },
                                    );
                                });
                            }
                            Ok(r) => {
                                let aid = app.id.clone();
                                let msg = r.message.unwrap_or_else(|| {
                                    format!("{app_name} wasn't removed. Try again.")
                                });
                                handle.update(move |s, _| {
                                    s.pc.apps_errors.insert(aid, msg);
                                });
                            }
                            Err(_) => {
                                let aid = app.id.clone();
                                handle.update(move |s, _| {
                                    s.pc.apps_errors.insert(
                                        aid,
                                        format!("{app_name} wasn't removed. Try again."),
                                    );
                                });
                            }
                        }
                    } else {
                        let (method, payload) = if app.uninstaller {
                            (
                                METHOD_APPS_UNINSTALL,
                                json!({ "id": app.id, "quiet": true }),
                            )
                        } else {
                            (METHOD_APPS_SCAN, json!({ "id": app.id }))
                        };
                        match call_sync::<AppFind>(&rpc, method, &payload) {
                            Ok(find) if find.ok => {
                                went += 1;
                                let aid = app.id.clone();
                                let aname = app_name.clone();
                                handle.update(move |s, _| {
                                    s.pc.apps_removed.retain(|r| r.id != aid);
                                    s.pc.apps_removed
                                        .insert(0, find_to_removed_entry(aid, aname, find));
                                });
                            }
                            Ok(find) => {
                                let aid = app.id.clone();
                                let msg = find.message.unwrap_or_else(|| {
                                    format!("{app_name} wasn't removed. Try again.")
                                });
                                handle.update(move |s, _| {
                                    s.pc.apps_errors.insert(aid, msg);
                                });
                            }
                            Err(_) => {
                                let aid = app.id.clone();
                                handle.update(move |s, _| {
                                    s.pc.apps_errors.insert(
                                        aid,
                                        format!("{app_name} wasn't removed. Try again."),
                                    );
                                });
                            }
                        }
                    }
                }
                let _ = call_sync::<Value>(&rpc, METHOD_APPS_LIST, &json!({}));
                handle.update(move |s, _| {
                    s.pc.apps_run = None;
                    s.pc.apps_busy_id = None;
                    if chosen_len > 1 {
                        let stayed = chosen_len - went;
                        let line = if stayed > 0 {
                            format!(
                                "Uninstalled {went} of {chosen_len}: {stayed} {} why beside {}.",
                                if stayed == 1 { "app says" } else { "apps say" },
                                if stayed == 1 { "it" } else { "them" }
                            )
                        } else {
                            format!("Uninstalled {went} of {chosen_len}.")
                        };
                        let id = format!(
                            "run-{}",
                            SystemTime::now()
                                .duration_since(UNIX_EPOCH)
                                .map(|d| d.as_millis())
                                .unwrap_or(0)
                        );
                        s.pc.apps_removed.insert(
                            0,
                            RemovedAppEntry {
                                id,
                                name: String::new(),
                                find: None,
                                message: Some(line),
                                cleared_line: None,
                                undo_token: None,
                                undone: false,
                                review_open: false,
                                picks: HashSet::new(),
                                error: None,
                            },
                        );
                    }
                    s.load_pc_apps();
                });
            });
        }
    }

    pub fn clean_app_leftovers(&mut self, entry_id: Option<String>) {
        let (token, picks): (String, Vec<i64>) = match entry_id.as_deref() {
            None => {
                let Some(ref t) = self.pc.apps_tidy else {
                    return;
                };
                let Some(ref f) = t.find else {
                    return;
                };
                (f.token.clone(), t.picks.iter().copied().collect())
            }
            Some(id) => {
                let Some(e) = self.pc.apps_removed.iter().find(|r| r.id == id) else {
                    return;
                };
                let Some(ref f) = e.find else {
                    return;
                };
                (f.token.clone(), e.picks.iter().copied().collect())
            }
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, AppCleanReply>(
                METHOD_APPS_CLEAN,
                &json!({ "token": token, "ids": picks }),
                move |res| {
                    handle.update(move |s, _| {
                        let target = match entry_id.as_deref() {
                            None => s.pc.apps_tidy.as_mut(),
                            Some(id) => s.pc.apps_removed.iter_mut().find(|r| r.id == id),
                        };
                        if let Some(entry) = target {
                            match res {
                                Ok(ans) => {
                                    if let Some(ref cleared) = ans.cleared {
                                        entry.cleared_line = Some(leftover_cleared_line(cleared));
                                        entry.undo_token = ans.undo;
                                        entry.review_open = false;
                                    }
                                    if !ans.ok {
                                        entry.error = Some(ans.message.unwrap_or_else(|| {
                                            "Nothing was deleted. Try again.".into()
                                        }));
                                    }
                                }
                                Err(_) => {
                                    entry.error = Some("Nothing was deleted. Try again.".into());
                                }
                            }
                        }
                        s.load_pc_apps_extras(true);
                    });
                },
            );
        }
    }

    pub fn leave_app_leftovers(&mut self, entry_id: Option<String>) {
        let token = match entry_id.as_deref() {
            None => self
                .pc
                .apps_tidy
                .take()
                .and_then(|e| e.find.map(|f| f.token)),
            Some(id) => {
                let tok = self
                    .pc
                    .apps_removed
                    .iter()
                    .find(|r| r.id == id)
                    .and_then(|e| e.find.as_ref().map(|f| f.token.clone()));
                self.pc.apps_removed.retain(|r| r.id != id);
                tok
            }
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(tok)) = (self.rpc.clone(), token) {
            rpc.call::<_, ActionResult>(METHOD_APPS_LEAVE, &json!({ "token": tok }), |_| {});
        }
    }

    pub fn undo_app_cleanup(&mut self, target: UndoTarget, undo_id: String) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, AppUndoReply>(METHOD_APPS_UNDO, &json!({ "id": undo_id }), move |res| {
                handle.update(move |s, _| {
                    let err = match res {
                        Ok(r) if r.ok => None,
                        Ok(r) => Some(
                            r.message
                                .unwrap_or_else(|| "It wasn’t put back. Try again.".into()),
                        ),
                        Err(_) => Some("It wasn’t put back. Try again.".into()),
                    };
                    match target {
                        UndoTarget::Removed(ref id) => {
                            if let Some(e) = s.pc.apps_removed.iter_mut().find(|r| &r.id == id) {
                                if err.is_none() {
                                    e.undone = true;
                                } else {
                                    e.error = err;
                                }
                            }
                        }
                        UndoTarget::Tidy => {
                            if let Some(ref mut e) = s.pc.apps_tidy {
                                if err.is_none() {
                                    e.undone = true;
                                } else {
                                    e.error = err;
                                }
                            }
                        }
                        UndoTarget::AutoCleared(ref id) => {
                            if let Some(e) = s.pc.apps_auto_cleared.iter_mut().find(|r| &r.id == id)
                            {
                                if err.is_none() {
                                    e.undone = true;
                                } else {
                                    e.error = err;
                                }
                            }
                        }
                        UndoTarget::InstallerCache => {
                            if err.is_none() {
                                if let Some(ref mut d) = s.pc.installer_cache_done {
                                    d.2 = true;
                                }
                            } else {
                                s.pc.installer_cache_error = err;
                            }
                        }
                    }
                    s.load_pc_apps();
                    s.load_pc_apps_extras(true);
                });
            });
        }
    }

    pub fn open_default_apps(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_APPS_OPEN_DEFAULTS, &json!({}), move |res| {
                if let Ok(r) = res
                    && !r.ok
                {
                    handle.update(move |s, _| {
                        s.notice = r
                            .message
                            .or_else(|| Some("Default apps didn’t open. Try again.".into()));
                    });
                }
            });
        }
    }

    pub fn clean_apps_junk(&mut self) {
        self.pc.armed_confirm = None;
        let ids: Vec<String> = self
            .pc
            .junk_items
            .as_ref()
            .map(|items| items.iter().map(|i| i.id.clone()).collect())
            .unwrap_or_default();
        if ids.is_empty() {
            return;
        }
        self.pc.junk_cleaned = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, JunkCleanReply>(METHOD_JUNK_CLEAN, &json!({ "ids": ids }), move |res| {
                handle.update(move |s, _| {
                    match res {
                        Ok(r) if r.ok && r.bytes.unwrap_or(0) > 0 => {
                            s.pc.junk_cleaned = Some((
                                Some(format!("Deleted {}.", format_bytes(r.bytes.unwrap_or(0)))),
                                r.message,
                            ));
                        }
                        Ok(r) => {
                            s.pc.junk_cleaned = Some((
                                None,
                                r.message
                                    .or_else(|| Some("Nothing was deleted. Try again.".into())),
                            ));
                        }
                        Err(_) => {
                            s.pc.junk_cleaned =
                                Some((None, Some("Nothing was deleted. Try again.".into())));
                        }
                    }
                    s.load_pc_apps_extras(false);
                });
            });
        }
    }

    pub fn clean_installer_cache(&mut self) {
        self.pc.armed_confirm = None;
        self.pc.installer_cache_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, InstallerCacheCleanReply>(
                METHOD_APPS_INSTALLER_CACHE_CLEAN,
                &json!({}),
                move |res| {
                    handle.update(move |s, _| {
                        match res {
                            Ok(r) => {
                                if r.files > 0 {
                                    let line = format!(
                                        "Moved {} {} to the Recycle Bin: {} back.",
                                        r.files,
                                        if r.files == 1 {
                                            "Windows Installer file"
                                        } else {
                                            "Windows Installer files"
                                        },
                                        format_bytes(r.bytes)
                                    );
                                    s.pc.installer_cache_done = Some((line, r.undo, false));
                                }
                                if !r.ok {
                                    s.pc.installer_cache_error =
                                        Some(r.message.unwrap_or_else(|| {
                                            "Nothing was moved. Try again.".into()
                                        }));
                                }
                            }
                            Err(_) => {
                                s.pc.installer_cache_error =
                                    Some("Nothing was moved. Try again.".into());
                            }
                        }
                        s.load_pc_apps_extras(false);
                    });
                },
            );
        }
    }

    pub fn startup_clean_unneeded(&mut self) {
        self.pc.startup_errors.remove("clean");
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StartupListReply>(METHOD_STARTUP_CLEAN, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(list) => {
                        if !list.ok {
                            s.pc.startup_errors.insert(
                                "clean".into(),
                                list.message.clone().unwrap_or_else(|| {
                                    "Startup wasn’t cleaned up. Try again.".into()
                                }),
                            );
                        }
                        s.pc.startup_list = Some(list);
                    }
                    Err(_) => {
                        s.pc.startup_errors.insert(
                            "clean".into(),
                            "Startup wasn’t cleaned up. Try again.".into(),
                        );
                    }
                });
            });
        }
    }

    pub fn restore_startup_item(&mut self, id: &str) {
        self.pc.startup_errors.remove(id);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_s = id.to_string();
            rpc.call::<_, StartupListReply>(
                METHOD_STARTUP_RESTORE,
                &json!({ "id": id_s }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(list) => {
                            if !list.ok {
                                s.pc.startup_errors.insert(
                                    id_s,
                                    list.message.clone().unwrap_or_else(|| {
                                        "It wasn’t turned on. Try again.".into()
                                    }),
                                );
                            }
                            s.pc.startup_list = Some(list);
                        }
                        Err(_) => {
                            s.pc.startup_errors
                                .insert(id_s, "It wasn’t turned on. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn add_back_windows_package(&mut self, id: &str) {
        self.pc.apps_packages_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_s = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_APPS_PACKAGE_ADD_BACK,
                &json!({ "id": id_s }),
                move |res| {
                    handle.update(move |s, _| {
                        match res {
                            Ok(r) if r.ok => {
                                s.notice = Some("Windows app restored.".into());
                            }
                            Ok(r) => {
                                s.pc.apps_packages_error = Some(
                                    r.message
                                        .unwrap_or_else(|| "Could not add app back.".into()),
                                );
                            }
                            Err(_) => {
                                s.pc.apps_packages_error = Some("Could not add app back.".into());
                            }
                        }
                        s.load_pc_apps();
                        s.load_pc_apps_extras(false);
                    });
                },
            );
        }
    }

    pub fn allow_edge_browser(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_APPS_ALLOW_EDGE, &json!({}), move |res| {
                handle.update(move |s, _| {
                    match res {
                        Ok(r) if r.ok => {
                            s.notice = Some("Microsoft Edge allowed again.".into());
                        }
                        Ok(r) => {
                            s.notice = r
                                .message
                                .or_else(|| Some("Could not allow Microsoft Edge.".into()));
                        }
                        Err(_) => s.notice = Some("Could not allow Microsoft Edge.".into()),
                    }
                    s.load_pc_apps();
                });
            });
        }
    }

    pub fn load_fps_counter_state(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, FpsCounterReply>(METHOD_FPS_COUNTER, &json!({}), move |res| {
                if let Ok(st) = res {
                    handle.update(move |s, _| {
                        s.settings.fps_counter = st.on;
                        s.pc.fps_counter_state = Some(st);
                    });
                }
            });
        }
    }

    pub fn toggle_fps_counter(&mut self, on: bool) {
        self.settings.fps_counter = on;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, FpsCounterReply>(METHOD_FPS_COUNTER, &json!({ "on": on }), move |res| {
                handle.update(move |s, _| match res {
                    Ok(st) => {
                        s.settings.fps_counter = st.on;
                        if let Some(ref m) = st.message {
                            s.notice = Some(m.clone());
                        }
                        s.pc.fps_counter_state = Some(st);
                    }
                    Err(e) => s.notice = Some(format!("FPS counter failed: {e}")),
                });
            });
        }
    }

    pub fn load_web_preferences(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, WebClientOptions>(METHOD_WEB_PREFERENCES_GET, &json!({}), move |res| {
                if let Ok(opts) = res {
                    handle.update(move |s, _| {
                        let missing_svc = opts.music_service.is_none();
                        s.pc.web_preferences = Some(opts);
                        if missing_svc {
                            let svc = s.media.music_service.id();
                            s.save_web_preferences(json!({ "musicService": svc }));
                        }
                    });
                }
            });
        }
    }

    pub fn save_web_preferences(&mut self, patch: Value) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, WebClientOptions>(METHOD_WEB_PREFERENCES_SET, &patch, move |res| {
                handle.update(move |s, _| match res {
                    Ok(opts) => s.pc.web_preferences = Some(opts),
                    Err(_) => s.notice = Some("The change could not be saved. Try again.".into()),
                });
            });
        }
    }

    pub fn notify_shaders_visibility(&mut self, visible: bool) {
        #[cfg(windows)]
        if (!visible || self.pc.shaders.as_ref().is_some_and(|sh| sh.present))
            && let Some(rpc) = self.rpc.clone()
        {
            rpc.call_raw(
                METHOD_SHADERS_CALL,
                &json!({ "method": "ui.visible", "params": { "visible": visible } }),
                |_| {},
            );
        }
        #[cfg(not(windows))]
        let _ = visible;
    }

    fn advance_driver_install_overlay(
        &mut self,
        phase: &str,
        version: Option<String>,
        share: Option<f64>,
        message: Option<String>,
        restored: Option<Vec<String>>,
        not_restored: Option<Vec<String>>,
    ) {
        const ORDER: &[&str] = &[
            "starting",
            "downloading",
            "unpacking",
            "waiting",
            "installing",
            "reloading",
            "restoring",
            "done",
            "failed",
        ];
        if let Some(ref cur) = self.pc.driver_install_overlay
            && cur.kind == "chipset"
        {
            return;
        }
        if let Some(ref cur) = self.pc.driver_install_overlay
            && phase != "failed"
        {
            let next_idx = ORDER.iter().position(|p| *p == phase).unwrap_or(0);
            let cur_idx = ORDER.iter().position(|p| *p == cur.phase).unwrap_or(0);
            if next_idx < cur_idx {
                return;
            }
        }
        let vendor_name = self
            .pc
            .gpu_driver
            .as_ref()
            .and_then(|d| d.vendor.as_deref())
            .map(|v| match v {
                "nvidia" => "NVIDIA".to_string(),
                "amd" => "AMD".to_string(),
                "intel" => "Intel".to_string(),
                other => other.to_string(),
            });
        let latest_size = self
            .pc
            .gpu_driver
            .as_ref()
            .and_then(|d| d.latest.as_ref())
            .map(|l| l.size);
        let latest_ver = self
            .pc
            .gpu_driver
            .as_ref()
            .and_then(|d| d.latest.as_ref())
            .map(|l| l.version.clone());
        let entry =
            self.pc
                .driver_install_overlay
                .get_or_insert_with(|| DriverInstallOverlayState {
                    kind: "gpu".into(),
                    phase: phase.into(),
                    name: vendor_name,
                    version: latest_ver,
                    share: None,
                    size: latest_size,
                    message: None,
                    restored: Vec::new(),
                    not_restored: Vec::new(),
                });
        entry.phase = phase.into();
        if let Some(v) = version {
            entry.version = Some(v);
        }
        entry.share = share;
        if message.is_some() || phase == "failed" {
            entry.message = message;
        }
        if let Some(r) = restored {
            entry.restored = r;
        }
        if let Some(nr) = not_restored {
            entry.not_restored = nr;
        }
    }

    pub fn dismiss_driver_install_overlay(&mut self) {
        self.pc.driver_install_overlay = None;
    }

    pub fn handle_gpu_driver_progress(&mut self, prog: DriverProgress) -> bool {
        let finished = prog.stage == "done" || prog.stage == "failed";
        self.pc.gpu_driver_starting = false;
        let stage = prog.stage.clone();
        let share = if stage == "downloading" {
            Some((prog.percent / 100.0).clamp(0.0, 1.0))
        } else {
            None
        };
        let msg = prog.message.clone();
        self.pc.gpu_driver_progress = Some(prog);
        if matches!(
            stage.as_str(),
            "downloading" | "unpacking" | "waiting" | "installing"
        ) {
            self.advance_driver_install_overlay(&stage, None, share, msg, None, None);
        } else if stage == "failed" && self.pc.driver_install_overlay.is_some() {
            self.advance_driver_install_overlay("failed", None, None, msg, None, None);
        }
        if finished {
            let b = self.pc.gpu_driver_branch.clone();
            self.set_gpu_driver_branch(&b);
        }
        true
    }

    pub fn handle_gpu_driver_install_event(&mut self, ev: GpuDriverInstallEvent) -> bool {
        let finished = ev.phase == "done" || ev.phase == "failed";
        self.advance_driver_install_overlay(
            &ev.phase,
            ev.version,
            None,
            ev.message,
            Some(ev.restored),
            Some(ev.not_restored),
        );
        if finished {
            let b = self.pc.gpu_driver_branch.clone();
            self.set_gpu_driver_branch(&b);
        }
        true
    }

    pub fn handle_driver_step_event(&mut self, step: DriverStep) -> bool {
        if step.id.is_empty() {
            self.pc.drivers_updating_all = false;
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                rpc.call::<_, DriversState>(METHOD_SYSTEM_DRIVERS, &json!({}), move |res| {
                    if let Ok(ds) = res {
                        handle.update(move |s, _| s.pc.drivers_state = Some(ds));
                    }
                });
            }
        } else {
            self.pc.driver_steps.insert(step.id.clone(), step);
        }
        self.room == Room::ExoAi
    }

    pub fn handle_bios_progress_event(&mut self, share: f64) -> bool {
        self.pc.bios_progress = Some(share);
        self.room == Room::ExoAi
    }

    pub fn handle_compress_progress_event(&mut self, id: &str, comp: CompressState) -> bool {
        if let Some(ref mut list) = self.pc.compress_list
            && let Some(game) = list.games.iter_mut().find(|g| g.id == id)
        {
            game.compress = comp;
            return self.room == Room::ExoAi;
        }
        false
    }

    pub fn handle_runtimes_progress_event(&mut self, prog: RuntimesProgress) -> bool {
        if self.pc.runtimes_running.is_some() {
            self.pc.runtimes_progress = Some(prog);
            return self.room == Room::ExoAi;
        }
        false
    }

    pub fn handle_shaders_event(&mut self, name: &str, data: &Value) -> bool {
        match name {
            "state" => {
                self.apply_shaders_snapshot_value(data);
            }
            "progress" => {
                if let Some(q) = data.get("queue") {
                    self.apply_shaders_queue_value(q);
                }
            }
            "ready" | "addon" => {
                let state = data.get("state").and_then(|v| v.as_str());
                if name == "addon" && state == Some("starting") {
                    return false;
                }
                self.run_shaders_steps("refresh", Vec::new());
            }
            _ => return false,
        }
        self.room == Room::ExoAi
    }

    pub fn handle_shaders_rebuild_event(&mut self, mut rb: ShaderRebuild) -> bool {
        rb.ok = true;
        self.pc.shaders_rebuild = Some(rb);
        self.room == Room::ExoAi
    }

    pub fn on_pc_blocker_changed(&mut self) -> bool {
        if self
            .pc
            .gpu_driver
            .as_ref()
            .is_some_and(|d| d.blocked.is_some())
        {
            let b = self.pc.gpu_driver_branch.clone();
            self.set_gpu_driver_branch(&b);
            return true;
        }
        false
    }

    pub fn save_settings_patch(&mut self, patch: Value) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LauncherSettings>(METHOD_SETTINGS_SET, &patch, move |res| {
                if let Ok(set) = res {
                    handle.update(move |s, _| s.settings = set);
                }
            });
        }
    }

    pub fn toggle_fps_counter_item(&mut self, item_id: &str) {
        if let Some(pos) = self
            .settings
            .fps_counter_items
            .iter()
            .position(|x| x == item_id)
        {
            self.settings.fps_counter_items.remove(pos);
        } else {
            self.settings.fps_counter_items.push(item_id.to_string());
        }
        let next = self.settings.fps_counter_items.clone();
        self.save_settings_patch(json!({ "fpsCounterItems": next }));
    }

    pub fn set_fps_counter_position(&mut self, pos: &str) {
        self.settings.fps_counter_position = Some(pos.to_string());
        self.save_settings_patch(json!({ "fpsCounterPosition": pos }));
    }
}
