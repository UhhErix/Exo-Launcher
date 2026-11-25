use crate::backend::{HostBackend, enforce_process_policy_on_payload};
use exo_policy::anti_cheat::{
    Answer, Change, GameDescriptor, SteamWord, allows, by_title, classify, decide, dll_policy_for,
    for_store, known_game, refused_argument, wire_class,
};
use exo_policy::deletion_gate::{DeletionPolicy, refusal};
use exo_policy::network_destinations::check_url;
use exo_policy::pe_audit::audit_pe_file;
use exo_policy::player_data::{is_secret_file, lookup, ui_pref_syncs};
use exo_policy::process_termination::{ProcessNode, plan_tree};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

pub const PHASE2_SLICE2_READONLY_ROUTES: &[&str] = &[
    "system.health",
    "gpu.profile",
    "gpu.processes",
    "gpu.tuning.state",
    "system.about",
    "gpu.display",
    "hdr.state",
    "system.drives",
];

pub const PHASE2_ROUTES: &[&str] = &[
    "host.phase2.capabilities",
    "policy.antiCheat.decide",
    "policy.antiCheat.allows",
    "policy.antiCheat.refusedArg",
    "policy.process.planTree",
    "policy.deletion.check",
    "policy.network.check",
    "policy.playerData.lookup",
    "policy.pe.audit",
    "gpu.control.overview",
    "sensors.audit",
    "display.monitors.readonly",
    "thermal.status",
    "power.guard.status",
    "autopilot.status",
    "net.latency.status",
    "system.health",
    "gpu.profile",
    "gpu.processes",
    "gpu.tuning.state",
    "system.about",
    "gpu.display",
    "hdr.state",
    "system.drives",
];

fn param_action(params: &Value) -> Option<&str> {
    params
        .get("action")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
}

pub fn is_readonly_hardware_call(method: &str, params: &Value) -> bool {
    match method {
        "system.health" => param_action(params).is_none(),
        "gpu.profile" => matches!(param_action(params), None | Some("state")),
        "gpu.processes" => true,
        "gpu.tuning.state" => true,
        "system.about" => true,
        "gpu.display" => matches!(param_action(params), None | Some("state")),
        "hdr.state" => true,
        "system.drives" => param_action(params).is_none(),
        _ => false,
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RouteError {
    InvalidParams(String),
    Io(String),
}

impl std::fmt::Display for RouteError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidParams(msg) => write!(f, "invalid params: {msg}"),
            Self::Io(msg) => write!(f, "io error: {msg}"),
        }
    }
}

impl std::error::Error for RouteError {}

#[derive(Debug, Clone, PartialEq)]
pub enum RouteOutcome {
    Handled(Value),
    FallbackToDotnet,
}

pub struct HostRouter<B: HostBackend> {
    backend: B,
}

impl<B: HostBackend> HostRouter<B> {
    pub fn new(backend: B) -> Self {
        Self { backend }
    }

    pub fn backend(&self) -> &B {
        &self.backend
    }

    pub fn handles_in_process(method: &str) -> bool {
        PHASE2_ROUTES.contains(&method)
    }

    pub fn dispatch(&self, method: &str, params: &Value) -> Result<RouteOutcome, RouteError> {
        match method {
            "host.phase2.capabilities" => Ok(RouteOutcome::Handled(json!({
                "ok": true,
                "phase": 2,
                "slice": 2,
                "shadowMode": true,
                "inProcessRoutes": PHASE2_ROUTES,
                "readonlyShadowRoutes": PHASE2_SLICE2_READONLY_ROUTES,
                "wireSchemaMethods": exo_wire::METHODS.len(),
                "wireSchemaEvents": exo_wire::EVENTS.len(),
                "wireSchemaHash": format!("0x{:016x}", exo_wire::SCHEMA_HASH),
            }))),
            "policy.antiCheat.decide" => self.handle_anti_cheat_decide(params),
            "policy.antiCheat.allows" => self.handle_anti_cheat_allows(params),
            "policy.antiCheat.refusedArg" => self.handle_refused_arg(params),
            "policy.process.planTree" => self.handle_process_plan_tree(params),
            "policy.deletion.check" => self.handle_deletion_check(params),
            "policy.network.check" => self.handle_network_check(params),
            "policy.playerData.lookup" => self.handle_player_data_lookup(params),
            "policy.pe.audit" => self.handle_pe_audit(params),
            "gpu.control.overview" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_gpu_overview()).unwrap(),
            )),
            "sensors.audit" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_sensors_audit()).unwrap(),
            )),
            "display.monitors.readonly" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_display_monitors()).unwrap(),
            )),
            "thermal.status" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_thermal_status()).unwrap(),
            )),
            "power.guard.status" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_power_guard_status()).unwrap(),
            )),
            "autopilot.status" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_autopilot_status()).unwrap(),
            )),
            "net.latency.status" => Ok(RouteOutcome::Handled(
                serde_json::to_value(self.backend.read_net_latency_status()).unwrap(),
            )),
            "system.health" => {
                if !is_readonly_hardware_call(method, params) {
                    return Ok(RouteOutcome::FallbackToDotnet);
                }
                Ok(RouteOutcome::Handled(self.backend.read_system_health()))
            }
            "gpu.profile" => {
                if !is_readonly_hardware_call(method, params) {
                    return Ok(RouteOutcome::FallbackToDotnet);
                }
                Ok(RouteOutcome::Handled(self.backend.read_gpu_profile()))
            }
            "gpu.processes" => {
                let limit = params
                    .get("limit")
                    .and_then(Value::as_u64)
                    .map(|n| n.clamp(1, 100) as usize)
                    .unwrap_or(25);
                let mut payload = self.backend.read_gpu_processes(limit);
                enforce_process_policy_on_payload(&mut payload);
                Ok(RouteOutcome::Handled(payload))
            }
            "gpu.tuning.state" => Ok(RouteOutcome::Handled(self.backend.read_gpu_tuning_state())),
            "system.about" => Ok(RouteOutcome::Handled(self.backend.read_system_about())),
            "gpu.display" => {
                if !is_readonly_hardware_call(method, params) {
                    return Ok(RouteOutcome::FallbackToDotnet);
                }
                Ok(RouteOutcome::Handled(self.backend.read_gpu_display()))
            }
            "hdr.state" => Ok(RouteOutcome::Handled(self.backend.read_hdr_state())),
            "system.drives" => {
                if !is_readonly_hardware_call(method, params) {
                    return Ok(RouteOutcome::FallbackToDotnet);
                }
                Ok(RouteOutcome::Handled(self.backend.read_system_drives()))
            }
            _ => Ok(RouteOutcome::FallbackToDotnet),
        }
    }

    fn handle_anti_cheat_decide(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Req {
            game: GameDescriptor,
            #[serde(default)]
            install_found: bool,
            #[serde(default)]
            disk: Option<String>,
            #[serde(default)]
            service: Option<String>,
            #[serde(default = "default_steam_word")]
            steam: SteamWord,
            #[serde(default)]
            without_entry: bool,
        }
        fn default_steam_word() -> SteamWord {
            SteamWord::NotSteam
        }
        let req: Req = serde_json::from_value(params.clone())
            .map_err(|e| RouteError::InvalidParams(e.to_string()))?;
        let answer = decide(
            for_store(req.game.store),
            known_game(&req.game.id),
            req.install_found,
            req.disk.as_deref(),
            req.service.as_deref(),
            req.steam,
            by_title(&req.game.title),
            req.without_entry,
        );
        let class = classify(&req.game, &answer);
        let dll_policy = dll_policy_for(class, &answer);
        let wire = wire_class(&req.game, Some(&answer));
        Ok(RouteOutcome::Handled(json!({
            "answer": answer,
            "class": class,
            "effectiveClass": class.effective(),
            "allowsExoFlow": class.allows_exo_flow(),
            "dllPolicy": dll_policy,
            "wireClass": wire,
        })))
    }

    fn handle_anti_cheat_allows(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Req {
            game: GameDescriptor,
            answer: Answer,
            change: Change,
            #[serde(default)]
            consented: Option<String>,
        }
        let req: Req = serde_json::from_value(params.clone())
            .map_err(|e| RouteError::InvalidParams(e.to_string()))?;
        let ok = allows(&req.game, &req.answer, req.change, req.consented.as_deref());
        Ok(RouteOutcome::Handled(json!({ "allowed": ok })))
    }

    fn handle_refused_arg(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        let args = params
            .get("arguments")
            .and_then(Value::as_str)
            .unwrap_or("");
        let refused = refused_argument(args);
        Ok(RouteOutcome::Handled(json!({
            "allowed": refused.is_none(),
            "refused": refused,
        })))
    }

    fn handle_process_plan_tree(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Req {
            root: ProcessNode,
            #[serde(default)]
            descendants: Vec<ProcessNode>,
        }
        let req: Req = serde_json::from_value(params.clone())
            .map_err(|e| RouteError::InvalidParams(e.to_string()))?;
        let plan = plan_tree(&req.root, &req.descendants);
        Ok(RouteOutcome::Handled(serde_json::to_value(plan).unwrap()))
    }

    fn handle_deletion_check(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Req {
            path: String,
            #[serde(default = "default_true")]
            clean: bool,
            #[serde(default)]
            owners: Vec<String>,
            #[serde(default)]
            kept_roots: Vec<String>,
            #[serde(default)]
            exo_data: Option<String>,
            #[serde(default)]
            shader_roots: Vec<String>,
            #[serde(default)]
            running_processes: Option<Vec<String>>,
            #[serde(default)]
            shader_busy: bool,
            #[serde(default)]
            allow_app_browser_files: bool,
        }
        fn default_true() -> bool {
            true
        }
        let req: Req = serde_json::from_value(params.clone())
            .map_err(|e| RouteError::InvalidParams(e.to_string()))?;
        let policy = DeletionPolicy {
            kept_roots: req.kept_roots.into_iter().map(PathBuf::from).collect(),
            exo_data: req.exo_data.map(PathBuf::from),
            shader_roots: req.shader_roots.into_iter().map(PathBuf::from).collect(),
            running_processes: req.running_processes,
            shader_busy: req.shader_busy,
        };
        let owner_refs: Vec<&str> = req.owners.iter().map(String::as_str).collect();
        let why = refusal(
            &req.path,
            req.clean,
            &owner_refs,
            &policy,
            req.allow_app_browser_files,
        );
        Ok(RouteOutcome::Handled(json!({
            "allowed": why.is_none(),
            "refusal": why,
        })))
    }

    fn handle_network_check(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        let url = params
            .get("url")
            .and_then(Value::as_str)
            .ok_or_else(|| RouteError::InvalidParams("missing url".into()))?;
        let res = check_url(url);
        Ok(RouteOutcome::Handled(serde_json::to_value(res).unwrap()))
    }

    fn handle_player_data_lookup(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        #[derive(Serialize)]
        #[serde(rename_all = "camelCase")]
        struct Out {
            found: bool,
            of_this_machine: bool,
            secret: bool,
            ui_pref_syncs: Option<bool>,
        }
        let path = params.get("path").and_then(Value::as_str).unwrap_or("");
        let pref = params.get("uiPrefKey").and_then(Value::as_str);
        let entry = lookup(path);
        let out = Out {
            found: entry.is_some(),
            of_this_machine: entry.is_some_and(|e| e.of_this_machine),
            secret: is_secret_file(path),
            ui_pref_syncs: pref.map(ui_pref_syncs),
        };
        Ok(RouteOutcome::Handled(serde_json::to_value(out).unwrap()))
    }

    fn handle_pe_audit(&self, params: &Value) -> Result<RouteOutcome, RouteError> {
        let path = params
            .get("path")
            .and_then(Value::as_str)
            .ok_or_else(|| RouteError::InvalidParams("missing path".into()))?;
        let report = audit_pe_file(Path::new(path)).map_err(RouteError::Io)?;
        Ok(RouteOutcome::Handled(json!({
            "clean": report.is_clean(),
            "totalImports": report.total_imports,
            "bannedFound": report.banned_found,
        })))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::backend::MockHostBackend;
    use std::time::Instant;

    #[test]
    fn phase2_routes_dispatch_in_process_and_unmigrated_routes_fall_back_to_dotnet() {
        let router = HostRouter::new(MockHostBackend::default());

        for &route in &[
            "host.phase2.capabilities",
            "gpu.control.overview",
            "sensors.audit",
            "display.monitors.readonly",
            "thermal.status",
            "power.guard.status",
            "autopilot.status",
            "net.latency.status",
            "system.health",
            "gpu.profile",
            "gpu.processes",
            "gpu.tuning.state",
            "system.about",
            "gpu.display",
            "hdr.state",
            "system.drives",
        ] {
            let outcome = router.dispatch(route, &json!({})).unwrap();
            assert!(
                matches!(outcome, RouteOutcome::Handled(_)),
                "expected {route} handled in-process"
            );
        }

        for &unmigrated in &[
            "library.get",
            "shell.bootstrap",
            "discord.state",
            "store.home",
            "gpu.tuning.apply",
            "gpu.tuning.reset",
            "gpu.tuning.test",
            "gpu.tuning.undervolt",
            "gpu.tuning.waiver",
            "hdr.calibrate",
            "hid.write",
            "mouse.apply",
            "keyboard.apply",
            "audio.setVolume",
        ] {
            let outcome = router.dispatch(unmigrated, &json!({})).unwrap();
            assert_eq!(
                outcome,
                RouteOutcome::FallbackToDotnet,
                "expected {unmigrated} to stay on .NET host"
            );
        }
    }

    #[test]
    fn write_actions_on_readonly_families_always_fall_back_to_dotnet() {
        let router = HostRouter::new(MockHostBackend::default());
        let write_calls = [
            ("system.health", json!({ "action": "apply" })),
            ("system.health", json!({ "action": "network" })),
            ("system.health", json!({ "action": "gamemode-on" })),
            ("gpu.profile", json!({ "action": "apply" })),
            ("gpu.profile", json!({ "action": "undo" })),
            ("gpu.display", json!({ "action": "set" })),
            ("gpu.display", json!({ "action": "adjust" })),
            ("gpu.display", json!({ "action": "ddc" })),
            ("gpu.display", json!({ "action": "override" })),
            ("system.drives", json!({ "action": "cache" })),
            ("system.drives", json!({ "action": "trim" })),
            ("system.drives", json!({ "action": "all" })),
        ];
        for (method, params) in write_calls {
            assert!(!is_readonly_hardware_call(method, &params));
            assert_eq!(
                router.dispatch(method, &params).unwrap(),
                RouteOutcome::FallbackToDotnet,
                "write call {method} {params} must not execute in Rust slice 2"
            );
        }
    }

    #[test]
    fn policy_routes_enforce_hard_stops_in_process() {
        let router = HostRouter::new(MockHostBackend::default());

        let RouteOutcome::Handled(val) = router
            .dispatch(
                "policy.antiCheat.decide",
                &json!({
                    "game": { "id": "riot:valorant", "title": "VALORANT", "store": "riot" },
                    "installFound": true
                }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(val["wireClass"], "competitivePvpOnline");
        assert_eq!(val["dllPolicy"], "disallowed");
        assert_eq!(val["allowsExoFlow"], false);

        let RouteOutcome::Handled(arg) = router
            .dispatch(
                "policy.antiCheat.refusedArg",
                &json!({ "arguments": "-dx12 -noeac" }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(arg["allowed"], false);
        assert_eq!(arg["refused"], "-noeac");

        let RouteOutcome::Handled(tree) = router
            .dispatch(
                "policy.process.planTree",
                &json!({
                    "root": { "pid": 10, "name": "game.exe" },
                    "descendants": [
                        { "pid": 11, "name": "EasyAntiCheat_EOS.exe" },
                        { "pid": 12, "name": "worker.exe" }
                    ]
                }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(tree["kill"], json!([12, 10]));
        assert_eq!(tree["refused"], json!([11]));

        let RouteOutcome::Handled(net) = router
            .dispatch(
                "policy.network.check",
                &json!({ "url": "https://evil.invalid/steal" }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(net["allowed"], false);

        let RouteOutcome::Handled(del) = router
            .dispatch(
                "policy.deletion.check",
                &json!({
                    "path": "C:\\Users\\Test\\.ssh\\id_rsa",
                    "clean": true
                }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(del["allowed"], false);

        let RouteOutcome::Handled(pd) = router
            .dispatch(
                "policy.playerData.lookup",
                &json!({ "path": "account-session.bin", "uiPrefKey": "discord" }),
            )
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(pd["found"], false);
        assert_eq!(pd["secret"], true);
        assert_eq!(pd["uiPrefSyncs"], true);
    }

    #[test]
    fn current_test_binary_passes_pe_banned_import_audit() {
        let exe = std::env::current_exe().unwrap();
        let router = HostRouter::new(MockHostBackend::default());
        let RouteOutcome::Handled(res) = router
            .dispatch("policy.pe.audit", &json!({ "path": exe.to_string_lossy() }))
            .unwrap()
        else {
            panic!("expected Handled");
        };
        assert_eq!(res["clean"], true, "unexpected banned imports: {res}");
        assert!(res["totalImports"].as_u64().unwrap() > 0);
    }

    #[test]
    #[ignore]
    fn bench_in_process_phase2_dispatch() {
        let router = HostRouter::new(MockHostBackend::default());
        let ac_params = json!({
            "game": { "id": "steam:3513350", "title": "Wuthering Waves", "store": "steam" },
            "installFound": true,
            "disk": "Anti-Cheat Expert",
            "steam": "none"
        });
        let gpu_params = json!({});

        for _ in 0..500 {
            let _ = router
                .dispatch("policy.antiCheat.decide", &ac_params)
                .unwrap();
            let _ = router
                .dispatch("gpu.control.overview", &gpu_params)
                .unwrap();
        }

        let runs = 5_000usize;
        let mut ac_us = Vec::with_capacity(runs);
        let mut gpu_us = Vec::with_capacity(runs);
        for _ in 0..runs {
            let t0 = Instant::now();
            let _ = router
                .dispatch("policy.antiCheat.decide", &ac_params)
                .unwrap();
            ac_us.push(t0.elapsed().as_secs_f64() * 1_000_000.0);

            let t1 = Instant::now();
            let _ = router
                .dispatch("gpu.control.overview", &gpu_params)
                .unwrap();
            gpu_us.push(t1.elapsed().as_secs_f64() * 1_000_000.0);
        }
        ac_us.sort_by(|a, b| a.total_cmp(b));
        gpu_us.sort_by(|a, b| a.total_cmp(b));
        println!(
            "IN_PROCESS_BENCH: policy.antiCheat.decide p50={:.2}us p95={:.2}us p99={:.2}us | gpu.control.overview p50={:.2}us p95={:.2}us p99={:.2}us",
            ac_us[runs / 2],
            ac_us[(runs * 95) / 100],
            ac_us[(runs * 99) / 100],
            gpu_us[runs / 2],
            gpu_us[(runs * 95) / 100],
            gpu_us[(runs * 99) / 100],
        );
    }
}
