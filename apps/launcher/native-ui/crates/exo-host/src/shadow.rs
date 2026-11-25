use crate::backend::{HostBackend, LiveReadOnlyHostBackend};
use crate::router::{HostRouter, RouteOutcome, is_readonly_hardware_call};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ShadowFamily {
    Sensors,
    GpuTelemetry,
    SystemInfo,
    DisplayInfo,
    DriveInfo,
}

impl ShadowFamily {
    pub const ALL: &[Self] = &[
        Self::Sensors,
        Self::GpuTelemetry,
        Self::SystemInfo,
        Self::DisplayInfo,
        Self::DriveInfo,
    ];

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Sensors => "sensors",
            Self::GpuTelemetry => "gpuTelemetry",
            Self::SystemInfo => "systemInfo",
            Self::DisplayInfo => "displayInfo",
            Self::DriveInfo => "driveInfo",
        }
    }
}

pub fn classify_readonly_shadow(method: &str, params: &Value) -> Option<ShadowFamily> {
    if !is_readonly_hardware_call(method, params) {
        return None;
    }
    match method {
        "system.health" => Some(ShadowFamily::Sensors),
        "gpu.profile" | "gpu.processes" | "gpu.tuning.state" => Some(ShadowFamily::GpuTelemetry),
        "system.about" => Some(ShadowFamily::SystemInfo),
        "gpu.display" | "hdr.state" => Some(ShadowFamily::DisplayInfo),
        "system.drives" => Some(ShadowFamily::DriveInfo),
        _ => None,
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadowParityDiff {
    pub family: ShadowFamily,
    pub method: String,
    pub field: String,
    pub dotnet_value: Value,
    pub rust_value: Value,
    pub detail: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadowComparison {
    pub family: ShadowFamily,
    pub method: String,
    pub matched: bool,
    pub diffs: Vec<ShadowParityDiff>,
    pub rust_us: f64,
    pub dotnet_us: f64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadowFamilyStats {
    pub calls: u64,
    pub matches: u64,
    pub mismatches: u64,
    pub total_rust_us: f64,
    pub total_dotnet_us: f64,
}

fn push_diff(
    diffs: &mut Vec<ShadowParityDiff>,
    family: ShadowFamily,
    method: &str,
    field: impl Into<String>,
    dotnet_value: &Value,
    rust_value: &Value,
    detail: impl Into<String>,
) {
    diffs.push(ShadowParityDiff {
        family,
        method: method.to_owned(),
        field: field.into(),
        dotnet_value: dotnet_value.clone(),
        rust_value: rust_value.clone(),
        detail: detail.into(),
    });
}

fn cmp_exact_field(
    diffs: &mut Vec<ShadowParityDiff>,
    family: ShadowFamily,
    method: &str,
    field: &str,
    dotnet: &Value,
    rust: &Value,
) {
    let dv = dotnet.get(field).unwrap_or(&Value::Null);
    let rv = rust.get(field).unwrap_or(&Value::Null);
    if dv != rv {
        push_diff(diffs, family, method, field, dv, rv, "exact value mismatch");
    }
}

fn cmp_opt_f64_tol(
    diffs: &mut Vec<ShadowParityDiff>,
    family: ShadowFamily,
    method: &str,
    field: &str,
    dv: &Value,
    rv: &Value,
    tolerance: f64,
) {
    match (dv.as_f64(), rv.as_f64()) {
        (Some(a), Some(b)) => {
            if (a - b).abs() > tolerance {
                push_diff(
                    diffs,
                    family,
                    method,
                    field,
                    dv,
                    rv,
                    format!(
                        "delta {:.3} exceeded tolerance {:.3}",
                        (a - b).abs(),
                        tolerance
                    ),
                );
            }
        }
        (None, None) => {}
        _ => push_diff(
            diffs,
            family,
            method,
            field,
            dv,
            rv,
            "numeric presence mismatch",
        ),
    }
}

pub fn compare_shadow_response(
    family: ShadowFamily,
    method: &str,
    dotnet: &Value,
    rust: &Value,
) -> Vec<ShadowParityDiff> {
    let mut diffs = Vec::new();
    match (family, method) {
        (ShadowFamily::Sensors, "system.health") => {
            cmp_exact_field(&mut diffs, family, method, "ok", dotnet, rust);
            let d_items = dotnet.get("items").and_then(Value::as_array);
            let r_items = rust.get("items").and_then(Value::as_array);
            match (d_items, r_items) {
                (Some(di), Some(ri)) => {
                    if di.len() != ri.len() {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            "items.len",
                            &Value::from(di.len()),
                            &Value::from(ri.len()),
                            "health item count mismatch",
                        );
                    }
                    for (idx, (d_item, r_item)) in di.iter().zip(ri.iter()).enumerate() {
                        for key in ["key", "label", "applied", "cpu"] {
                            let dv = d_item.get(key).unwrap_or(&Value::Null);
                            let rv = r_item.get(key).unwrap_or(&Value::Null);
                            if dv != rv {
                                push_diff(
                                    &mut diffs,
                                    family,
                                    method,
                                    format!("items[{idx}].{key}"),
                                    dv,
                                    rv,
                                    "health item field mismatch",
                                );
                            }
                        }
                    }
                }
                _ => push_diff(
                    &mut diffs,
                    family,
                    method,
                    "items",
                    dotnet.get("items").unwrap_or(&Value::Null),
                    rust.get("items").unwrap_or(&Value::Null),
                    "items array missing",
                ),
            }
        }
        (ShadowFamily::GpuTelemetry, "gpu.profile") => {
            for field in ["ok", "vendor", "gpu", "series", "status", "rebar"] {
                cmp_exact_field(&mut diffs, family, method, field, dotnet, rust);
            }
            if let (Some(ds), Some(rs)) = (dotnet.get("screen"), rust.get("screen")) {
                for field in ["name", "hz", "vrr"] {
                    let dv = ds.get(field).unwrap_or(&Value::Null);
                    let rv = rs.get(field).unwrap_or(&Value::Null);
                    if dv != rv {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            format!("screen.{field}"),
                            dv,
                            rv,
                            "screen field mismatch",
                        );
                    }
                }
            }
        }
        (ShadowFamily::GpuTelemetry, "gpu.processes") => {
            cmp_exact_field(&mut diffs, family, method, "ok", dotnet, rust);
            let d_ad = dotnet.get("adapters").and_then(Value::as_array);
            let r_ad = rust.get("adapters").and_then(Value::as_array);
            match (d_ad, r_ad) {
                (Some(da), Some(ra)) => {
                    if da.len() != ra.len() {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            "adapters.len",
                            &Value::from(da.len()),
                            &Value::from(ra.len()),
                            "adapter count mismatch",
                        );
                    }
                    for (idx, (d_a, r_a)) in da.iter().zip(ra.iter()).enumerate() {
                        for key in ["name", "vendorId", "software"] {
                            let dv = d_a.get(key).unwrap_or(&Value::Null);
                            let rv = r_a.get(key).unwrap_or(&Value::Null);
                            if dv != rv {
                                push_diff(
                                    &mut diffs,
                                    family,
                                    method,
                                    format!("adapters[{idx}].{key}"),
                                    dv,
                                    rv,
                                    "adapter field mismatch",
                                );
                            }
                        }
                    }
                }
                _ => push_diff(
                    &mut diffs,
                    family,
                    method,
                    "adapters",
                    dotnet.get("adapters").unwrap_or(&Value::Null),
                    rust.get("adapters").unwrap_or(&Value::Null),
                    "adapters array missing",
                ),
            }
            if let (Some(dp), Some(rp)) = (
                dotnet.get("processes").and_then(Value::as_array),
                rust.get("processes").and_then(Value::as_array),
            ) {
                if dp.len() != rp.len() {
                    push_diff(
                        &mut diffs,
                        family,
                        method,
                        "processes.len",
                        &Value::from(dp.len()),
                        &Value::from(rp.len()),
                        "process list count mismatch",
                    );
                }
                for (idx, (d_p, r_p)) in dp.iter().zip(rp.iter()).enumerate() {
                    for key in ["pid", "name", "protectedReason"] {
                        let dv = d_p.get(key).unwrap_or(&Value::Null);
                        let rv = r_p.get(key).unwrap_or(&Value::Null);
                        if dv != rv {
                            push_diff(
                                &mut diffs,
                                family,
                                method,
                                format!("processes[{idx}].{key}"),
                                dv,
                                rv,
                                "process entry mismatch",
                            );
                        }
                    }
                }
            }
        }
        (ShadowFamily::GpuTelemetry, "gpu.tuning.state") => {
            for field in ["ok", "mode", "pausedAfterCrash"] {
                cmp_exact_field(&mut diffs, family, method, field, dotnet, rust);
            }
            if let (Some(dl), Some(rl)) = (dotnet.get("live"), rust.get("live")) {
                cmp_opt_f64_tol(
                    &mut diffs,
                    family,
                    method,
                    "live.tempC",
                    dl.get("tempC").unwrap_or(&Value::Null),
                    rl.get("tempC").unwrap_or(&Value::Null),
                    5.0,
                );
                cmp_opt_f64_tol(
                    &mut diffs,
                    family,
                    method,
                    "live.watts",
                    dl.get("watts").unwrap_or(&Value::Null),
                    rl.get("watts").unwrap_or(&Value::Null),
                    15.0,
                );
            }
            if let (Some(dc), Some(rc)) = (dotnet.get("capabilities"), rust.get("capabilities")) {
                for key in ["vendor", "deviceName", "backend"] {
                    let dv = dc.get(key).unwrap_or(&Value::Null);
                    let rv = rc.get(key).unwrap_or(&Value::Null);
                    if dv != rv {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            format!("capabilities.{key}"),
                            dv,
                            rv,
                            "capability field mismatch",
                        );
                    }
                }
            }
        }
        (ShadowFamily::SystemInfo, "system.about") => {
            for field in [
                "processor",
                "cores",
                "threads",
                "graphics",
                "videoMemoryGb",
                "memoryGb",
                "memoryType",
                "memorySpeed",
                "memoryRated",
                "memorySlotsUsed",
                "memorySlots",
                "motherboard",
                "bios",
                "biosDate",
                "windows",
                "windowsVersion",
                "build",
                "secureBoot",
            ] {
                cmp_exact_field(&mut diffs, family, method, field, dotnet, rust);
            }
        }
        (ShadowFamily::DisplayInfo, "gpu.display") => {
            cmp_exact_field(&mut diffs, family, method, "ok", dotnet, rust);
            let d_disp = dotnet.get("displays").and_then(Value::as_array);
            let r_disp = rust.get("displays").and_then(Value::as_array);
            match (d_disp, r_disp) {
                (Some(dd), Some(rd)) => {
                    if dd.len() != rd.len() {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            "displays.len",
                            &Value::from(dd.len()),
                            &Value::from(rd.len()),
                            "display count mismatch",
                        );
                    }
                    for (idx, (d_row, r_row)) in dd.iter().zip(rd.iter()).enumerate() {
                        for key in [
                            "id",
                            "name",
                            "main",
                            "hdr",
                            "hdrSupported",
                            "vendor",
                            "refresh",
                        ] {
                            let dv = d_row.get(key).unwrap_or(&Value::Null);
                            let rv = r_row.get(key).unwrap_or(&Value::Null);
                            if dv != rv {
                                push_diff(
                                    &mut diffs,
                                    family,
                                    method,
                                    format!("displays[{idx}].{key}"),
                                    dv,
                                    rv,
                                    "display field mismatch",
                                );
                            }
                        }
                    }
                }
                _ => push_diff(
                    &mut diffs,
                    family,
                    method,
                    "displays",
                    dotnet.get("displays").unwrap_or(&Value::Null),
                    rust.get("displays").unwrap_or(&Value::Null),
                    "displays array missing",
                ),
            }
        }
        (ShadowFamily::DisplayInfo, "hdr.state") => {
            for field in ["supported", "internal", "colorMode"] {
                cmp_exact_field(&mut diffs, family, method, field, dotnet, rust);
            }
            if let (Some(dd), Some(rd)) = (
                dotnet.get("displays").and_then(Value::as_array),
                rust.get("displays").and_then(Value::as_array),
            ) {
                if dd.len() != rd.len() {
                    push_diff(
                        &mut diffs,
                        family,
                        method,
                        "displays.len",
                        &Value::from(dd.len()),
                        &Value::from(rd.len()),
                        "hdr display count mismatch",
                    );
                }
                for (idx, (d_row, r_row)) in dd.iter().zip(rd.iter()).enumerate() {
                    for key in ["name", "gdiName", "main", "supported", "on", "colorMode"] {
                        let dv = d_row.get(key).unwrap_or(&Value::Null);
                        let rv = r_row.get(key).unwrap_or(&Value::Null);
                        if dv != rv {
                            push_diff(
                                &mut diffs,
                                family,
                                method,
                                format!("displays[{idx}].{key}"),
                                dv,
                                rv,
                                "hdr display field mismatch",
                            );
                        }
                    }
                }
            }
        }
        (ShadowFamily::DriveInfo, "system.drives") => {
            cmp_exact_field(&mut diffs, family, method, "ok", dotnet, rust);
            let d_dr = dotnet.get("drives").and_then(Value::as_array);
            let r_dr = rust.get("drives").and_then(Value::as_array);
            match (d_dr, r_dr) {
                (Some(dd), Some(rd)) => {
                    if dd.len() != rd.len() {
                        push_diff(
                            &mut diffs,
                            family,
                            method,
                            "drives.len",
                            &Value::from(dd.len()),
                            &Value::from(rd.len()),
                            "drive count mismatch",
                        );
                    }
                    for (idx, (d_row, r_row)) in dd.iter().zip(rd.iter()).enumerate() {
                        for key in ["id", "model", "kind"] {
                            let dv = d_row.get(key).unwrap_or(&Value::Null);
                            let rv = r_row.get(key).unwrap_or(&Value::Null);
                            if dv != rv {
                                push_diff(
                                    &mut diffs,
                                    family,
                                    method,
                                    format!("drives[{idx}].{key}"),
                                    dv,
                                    rv,
                                    "drive identity mismatch",
                                );
                            }
                        }
                        if let (Some(dt), Some(rt)) =
                            (d_row.get("temperature"), r_row.get("temperature"))
                        {
                            cmp_opt_f64_tol(
                                &mut diffs,
                                family,
                                method,
                                &format!("drives[{idx}].temperature.now"),
                                dt.get("now").unwrap_or(&Value::Null),
                                rt.get("now").unwrap_or(&Value::Null),
                                3.0,
                            );
                        }
                        if let (Some(dh), Some(rh)) = (d_row.get("health"), r_row.get("health")) {
                            for key in ["lifeUsed", "spare", "mediaErrors"] {
                                let dv = dh.get(key).unwrap_or(&Value::Null);
                                let rv = rh.get(key).unwrap_or(&Value::Null);
                                if dv != rv {
                                    push_diff(
                                        &mut diffs,
                                        family,
                                        method,
                                        format!("drives[{idx}].health.{key}"),
                                        dv,
                                        rv,
                                        "drive health mismatch",
                                    );
                                }
                            }
                        }
                        if let (Some(dv_arr), Some(rv_arr)) = (
                            d_row.get("volumes").and_then(Value::as_array),
                            r_row.get("volumes").and_then(Value::as_array),
                        ) {
                            if dv_arr.len() != rv_arr.len() {
                                push_diff(
                                    &mut diffs,
                                    family,
                                    method,
                                    format!("drives[{idx}].volumes.len"),
                                    &Value::from(dv_arr.len()),
                                    &Value::from(rv_arr.len()),
                                    "volume count mismatch",
                                );
                            }
                            for (vidx, (dv_item, rv_item)) in
                                dv_arr.iter().zip(rv_arr.iter()).enumerate()
                            {
                                let dl = dv_item.get("letter").unwrap_or(&Value::Null);
                                let rl = rv_item.get("letter").unwrap_or(&Value::Null);
                                if dl != rl {
                                    push_diff(
                                        &mut diffs,
                                        family,
                                        method,
                                        format!("drives[{idx}].volumes[{vidx}].letter"),
                                        dl,
                                        rl,
                                        "volume letter mismatch",
                                    );
                                }
                                cmp_opt_f64_tol(
                                    &mut diffs,
                                    family,
                                    method,
                                    &format!("drives[{idx}].volumes[{vidx}].totalGb"),
                                    dv_item.get("totalGb").unwrap_or(&Value::Null),
                                    rv_item.get("totalGb").unwrap_or(&Value::Null),
                                    0.5,
                                );
                                cmp_opt_f64_tol(
                                    &mut diffs,
                                    family,
                                    method,
                                    &format!("drives[{idx}].volumes[{vidx}].freeGb"),
                                    dv_item.get("freeGb").unwrap_or(&Value::Null),
                                    rv_item.get("freeGb").unwrap_or(&Value::Null),
                                    1.0,
                                );
                            }
                        }
                    }
                }
                _ => push_diff(
                    &mut diffs,
                    family,
                    method,
                    "drives",
                    dotnet.get("drives").unwrap_or(&Value::Null),
                    rust.get("drives").unwrap_or(&Value::Null),
                    "drives array missing",
                ),
            }
        }
        _ => {
            if dotnet != rust {
                push_diff(
                    &mut diffs,
                    family,
                    method,
                    "$",
                    dotnet,
                    rust,
                    "payload mismatch",
                );
            }
        }
    }
    diffs
}

type MismatchLogger = Arc<dyn Fn(&ShadowComparison) + Send + Sync>;

pub struct ShadowHarness<B: HostBackend> {
    router: HostRouter<B>,
    stats: Mutex<BTreeMap<ShadowFamily, ShadowFamilyStats>>,
    on_mismatch: MismatchLogger,
}

impl<B: HostBackend> ShadowHarness<B> {
    pub fn new(router: HostRouter<B>) -> Self {
        Self::with_logger(router, |cmp| {
            eprintln!(
                "[exo-host:shadow-mismatch] family={} method={} diffs={} rust_us={:.2} dotnet_us={:.2} first={:?}",
                cmp.family.as_str(),
                cmp.method,
                cmp.diffs.len(),
                cmp.rust_us,
                cmp.dotnet_us,
                cmp.diffs.first().map(|d| (&d.field, &d.detail)),
            );
        })
    }

    pub fn with_logger(
        router: HostRouter<B>,
        logger: impl Fn(&ShadowComparison) + Send + Sync + 'static,
    ) -> Self {
        Self {
            router,
            stats: Mutex::new(BTreeMap::new()),
            on_mismatch: Arc::new(logger),
        }
    }

    pub fn observe_and_compare(
        &self,
        method: &str,
        params: &Value,
        dotnet_reply: &Value,
        dotnet_duration: Duration,
    ) -> Option<ShadowComparison> {
        let family = classify_readonly_shadow(method, params)?;
        let t0 = Instant::now();
        let rust_reply = match self.router.dispatch(method, params) {
            Ok(RouteOutcome::Handled(val)) => val,
            Ok(RouteOutcome::FallbackToDotnet) | Err(_) => return None,
        };
        let rust_us = t0.elapsed().as_secs_f64() * 1_000_000.0;
        let dotnet_us = dotnet_duration.as_secs_f64() * 1_000_000.0;
        let diffs = compare_shadow_response(family, method, dotnet_reply, &rust_reply);
        let matched = diffs.is_empty();
        let cmp = ShadowComparison {
            family,
            method: method.to_owned(),
            matched,
            diffs,
            rust_us,
            dotnet_us,
        };
        {
            let mut map = self.stats.lock().unwrap_or_else(|e| e.into_inner());
            let entry = map.entry(family).or_default();
            entry.calls += 1;
            if matched {
                entry.matches += 1;
            } else {
                entry.mismatches += 1;
            }
            entry.total_rust_us += rust_us;
            entry.total_dotnet_us += dotnet_us;
        }
        if !matched {
            (self.on_mismatch)(&cmp);
        }
        Some(cmp)
    }

    pub fn stats_snapshot(&self) -> BTreeMap<ShadowFamily, ShadowFamilyStats> {
        self.stats.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }
}

#[cfg(windows)]
pub fn attach_live_shadow_observer(client: &exo_rpc::RpcClient) {
    if std::env::var("EXO_HOST_SHADOW")
        .map(|v| v.trim() == "0" || v.eq_ignore_ascii_case("false"))
        .unwrap_or(false)
    {
        return;
    }
    let (tx, rx) = std::sync::mpsc::sync_channel::<(String, Value, String, Duration)>(16);
    let _ = std::thread::Builder::new()
        .name("exo-host-shadow".into())
        .spawn(move || {
            let harness = ShadowHarness::new(HostRouter::new(LiveReadOnlyHostBackend));
            while let Ok((method, params, raw_dotnet, elapsed)) = rx.recv() {
                if let Ok(dotnet_val) = serde_json::from_str::<Value>(&raw_dotnet) {
                    let _ = harness.observe_and_compare(&method, &params, &dotnet_val, elapsed);
                }
            }
        });
    client.set_shadow_observer(Arc::new(move |method, params, raw_dotnet, elapsed| {
        if classify_readonly_shadow(method, &params).is_some() {
            let _ = tx.try_send((method.to_owned(), params, raw_dotnet, elapsed));
        }
    }));
}
