use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};

pub const RENDER_ENDPOINTS_ROOT: &str =
    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render";
pub const COM_CLSID_ROOT: &str = "SOFTWARE\\Classes\\CLSID";
pub const APO_REGISTRATION_ROOT: &str = "SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects";
pub const AUDIO_POLICY_ROOT: &str = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Audio";
pub const DISABLE_PROTECTED_AUDIO_DG_VALUE: &str = "DisableProtectedAudioDG";
pub const AUDIO_SERVICE_NAME: &str = "Audiosrv";
pub const IAUDIO_PROCESSING_OBJECT_IID: &str = "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";

pub const PKEY_FX_ASSOCIATION: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},0";
pub const PKEY_FX_PREMIX_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},1";
pub const PKEY_FX_POSTMIX_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},2";
pub const PKEY_FX_STREAM_EFFECT_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},5";
pub const PKEY_FX_MODE_EFFECT_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},6";
pub const PKEY_FX_ENDPOINT_EFFECT_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},7";
pub const PKEY_COMPOSITEFX_STREAM_EFFECT_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},13";
pub const PKEY_COMPOSITEFX_MODE_EFFECT_CLSID: &str = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},14";
pub const PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID: &str =
    "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},15";
pub const PKEY_MFX_PROCESSING_MODES_STREAMING: &str = "{D3993A3F-99C2-4402-B5EC-A92A0367664B},6";
pub const PKEY_EFX_PROCESSING_MODES_STREAMING: &str = "{D3993A3F-99C2-4402-B5EC-A92A0367664B},7";
pub const PKEY_AUDIOENDPOINT_DISABLE_SYSFX: &str = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
pub const AUDIO_SIGNALPROCESSINGMODE_DEFAULT: &str = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";

pub const EXO_APO_SFX_CLSID_STR: &str = "{7E3A8C14-4F2B-4A91-9D5E-2B8C6F1A4E01}";
pub const EXO_APO_MFX_CLSID_STR: &str = "{7E3A8C14-4F2B-4A91-9D5E-2B8C6F1A4E02}";
pub const EXO_APO_EFX_CLSID_STR: &str = "{7E3A8C14-4F2B-4A91-9D5E-2B8C6F1A4E03}";
pub const EXO_APO_CLSIDS: [&str; 3] = [
    EXO_APO_SFX_CLSID_STR,
    EXO_APO_MFX_CLSID_STR,
    EXO_APO_EFX_CLSID_STR,
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", content = "value", rename_all = "camelCase")]
pub enum RegValue {
    String(String),
    MultiString(Vec<String>),
    Dword(u32),
    Binary(Vec<u8>),
}

pub trait RegistryStore {
    fn key_exists(&self, path: &str) -> bool;
    fn list_values(&self, path: &str) -> std::io::Result<BTreeMap<String, RegValue>>;
    fn read_value(&self, path: &str, name: &str) -> std::io::Result<Option<RegValue>>;
    fn write_value(&mut self, path: &str, name: &str, value: &RegValue) -> std::io::Result<()>;
    fn delete_value(&mut self, path: &str, name: &str) -> std::io::Result<()>;
    fn ensure_key(&mut self, path: &str) -> std::io::Result<()>;
    fn delete_key(&mut self, path: &str) -> std::io::Result<()>;
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct MockRegistryStore {
    keys: BTreeMap<String, BTreeMap<String, RegValue>>,
}

impl MockRegistryStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn seed_endpoint(&mut self, endpoint_guid: &str, fx_values: BTreeMap<String, RegValue>) {
        let ep_path = endpoint_key_path(endpoint_guid);
        let fx_path = endpoint_fx_key_path(endpoint_guid);
        let mut ep_vals = BTreeMap::new();
        ep_vals.insert("DeviceState".to_string(), RegValue::Dword(1));
        self.keys.insert(ep_path, ep_vals);
        self.keys.insert(fx_path, fx_values);
    }

    pub fn snapshot_fx(&self, endpoint_guid: &str) -> Option<BTreeMap<String, RegValue>> {
        self.keys.get(&endpoint_fx_key_path(endpoint_guid)).cloned()
    }
}

impl RegistryStore for MockRegistryStore {
    fn key_exists(&self, path: &str) -> bool {
        self.keys.contains_key(path)
    }

    fn list_values(&self, path: &str) -> std::io::Result<BTreeMap<String, RegValue>> {
        Ok(self.keys.get(path).cloned().unwrap_or_default())
    }

    fn read_value(&self, path: &str, name: &str) -> std::io::Result<Option<RegValue>> {
        Ok(self.keys.get(path).and_then(|m| m.get(name)).cloned())
    }

    fn write_value(&mut self, path: &str, name: &str, value: &RegValue) -> std::io::Result<()> {
        self.keys
            .entry(path.to_string())
            .or_default()
            .insert(name.to_string(), value.clone());
        Ok(())
    }

    fn delete_value(&mut self, path: &str, name: &str) -> std::io::Result<()> {
        if let Some(map) = self.keys.get_mut(path) {
            map.remove(name);
        }
        Ok(())
    }

    fn ensure_key(&mut self, path: &str) -> std::io::Result<()> {
        self.keys.entry(path.to_string()).or_default();
        Ok(())
    }

    fn delete_key(&mut self, path: &str) -> std::io::Result<()> {
        self.keys.remove(path);
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EndpointBackup {
    pub endpoint_guid: String,
    pub original_fx_values: BTreeMap<String, RegValue>,
    pub registered_clsid: String,
    pub used_composite_fx: bool,
    pub preserved_oem_clsids: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "state", content = "value", rename_all = "camelCase")]
pub enum PriorDwordState {
    Absent,
    Present(u32),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ApoSigningMode {
    UnsignedFreeRoute,
    Signed,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeepBackupLedger {
    pub version: u32,
    pub endpoints: HashMap<String, EndpointBackup>,
    #[serde(default)]
    pub registered_dll_path: Option<String>,
    #[serde(default)]
    pub com_clsids_registered: Vec<String>,
    #[serde(default)]
    pub apo_reg_clsids_registered: Vec<String>,
    #[serde(default)]
    pub prior_disable_protected_audio_dg: Option<PriorDwordState>,
    #[serde(default)]
    pub signing_mode: Option<ApoSigningMode>,
    #[serde(default)]
    pub system_eq_enabled: bool,
    #[serde(default)]
    pub drm_disclosure_accepted: bool,
}

impl KeepBackupLedger {
    pub fn new() -> Self {
        Self {
            version: 1,
            endpoints: HashMap::new(),
            registered_dll_path: None,
            com_clsids_registered: Vec::new(),
            apo_reg_clsids_registered: Vec::new(),
            prior_disable_protected_audio_dg: None,
            signing_mode: None,
            system_eq_enabled: false,
            drm_disclosure_accepted: false,
        }
    }

    pub fn to_json(&self) -> serde_json::Result<String> {
        serde_json::to_string_pretty(self)
    }

    pub fn from_json(text: &str) -> serde_json::Result<Self> {
        serde_json::from_str(text)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegisterOutcome {
    pub endpoint_guid: String,
    pub used_composite_fx: bool,
    pub preserved_oem_clsids: Vec<String>,
    pub sysfx_was_disabled: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HealStatus {
    Healthy,
    HealedAfterReset { re_chained_oem_count: usize },
    EndpointMissing,
    SysFxDisabledByWindows,
}

pub fn normalize_endpoint_guid(input: &str) -> std::io::Result<String> {
    let tail = input.rsplit('.').next().unwrap_or(input).trim();
    let ok = tail.len() == 38
        && tail.starts_with('{')
        && tail.ends_with('}')
        && tail[1..37]
            .chars()
            .all(|c| c.is_ascii_hexdigit() || c == '-');
    if !ok {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "invalid Windows audio endpoint GUID",
        ));
    }
    Ok(tail.to_ascii_lowercase())
}

pub fn endpoint_key_path(endpoint_guid: &str) -> String {
    format!(
        "{RENDER_ENDPOINTS_ROOT}\\{}",
        endpoint_guid.to_ascii_lowercase()
    )
}

pub fn endpoint_fx_key_path(endpoint_guid: &str) -> String {
    format!(
        "{RENDER_ENDPOINTS_ROOT}\\{}\\FxProperties",
        endpoint_guid.to_ascii_lowercase()
    )
}

pub fn register_endpoint<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
    endpoint_id: &str,
) -> std::io::Result<RegisterOutcome> {
    let guid = normalize_endpoint_guid(endpoint_id)?;
    let ep_path = endpoint_key_path(&guid);
    if !store.key_exists(&ep_path) {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "audio endpoint does not exist",
        ));
    }
    let fx_path = endpoint_fx_key_path(&guid);
    store.ensure_key(&fx_path)?;
    let current = store.list_values(&fx_path)?;

    if !ledger.endpoints.contains_key(&guid) {
        ledger.endpoints.insert(
            guid.clone(),
            EndpointBackup {
                endpoint_guid: guid.clone(),
                original_fx_values: current.clone(),
                registered_clsid: EXO_APO_EFX_CLSID_STR.to_string(),
                used_composite_fx: true,
                preserved_oem_clsids: Vec::new(),
            },
        );
    }

    let sysfx_was_disabled = matches!(
        current.get(PKEY_AUDIOENDPOINT_DISABLE_SYSFX),
        Some(RegValue::Dword(v)) if *v != 0
    );

    let mut composite_list: Vec<String> = Vec::new();
    let mut preserved_oem: Vec<String> = Vec::new();

    if let Some(RegValue::MultiString(existing)) =
        current.get(PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID)
    {
        for clsid in existing {
            if !clsid.is_empty() && !contains_ci(&composite_list, clsid) {
                composite_list.push(clsid.clone());
                if !clsid.eq_ignore_ascii_case(EXO_APO_EFX_CLSID_STR) {
                    preserved_oem.push(clsid.clone());
                }
            }
        }
    }

    if let Some(RegValue::String(legacy_efx)) = current.get(PKEY_FX_ENDPOINT_EFFECT_CLSID)
        && !legacy_efx.is_empty()
        && !legacy_efx.eq_ignore_ascii_case(EXO_APO_EFX_CLSID_STR)
        && !contains_ci(&composite_list, legacy_efx)
    {
        composite_list.push(legacy_efx.clone());
        preserved_oem.push(legacy_efx.clone());
    }

    if !contains_ci(&composite_list, EXO_APO_EFX_CLSID_STR) {
        composite_list.push(EXO_APO_EFX_CLSID_STR.to_string());
    }

    store.write_value(
        &fx_path,
        PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID,
        &RegValue::MultiString(composite_list),
    )?;
    if current.contains_key(PKEY_FX_ENDPOINT_EFFECT_CLSID) {
        store.delete_value(&fx_path, PKEY_FX_ENDPOINT_EFFECT_CLSID)?;
    }

    let mut modes = match current.get(PKEY_EFX_PROCESSING_MODES_STREAMING) {
        Some(RegValue::MultiString(list)) => list.clone(),
        Some(RegValue::String(one)) if !one.is_empty() => vec![one.clone()],
        _ => Vec::new(),
    };
    if !contains_ci(&modes, AUDIO_SIGNALPROCESSINGMODE_DEFAULT) {
        modes.push(AUDIO_SIGNALPROCESSINGMODE_DEFAULT.to_string());
    }
    store.write_value(
        &fx_path,
        PKEY_EFX_PROCESSING_MODES_STREAMING,
        &RegValue::MultiString(modes),
    )?;

    if !current.contains_key(PKEY_FX_ASSOCIATION) {
        store.write_value(
            &fx_path,
            PKEY_FX_ASSOCIATION,
            &RegValue::String("{00000000-0000-0000-0000-000000000000}".to_string()),
        )?;
    }

    if let Some(entry) = ledger.endpoints.get_mut(&guid) {
        entry.used_composite_fx = true;
        entry.preserved_oem_clsids = preserved_oem.clone();
    }

    Ok(RegisterOutcome {
        endpoint_guid: guid,
        used_composite_fx: true,
        preserved_oem_clsids: preserved_oem,
        sysfx_was_disabled,
    })
}

pub fn is_exo_registered_on_endpoint<R: RegistryStore>(store: &R, endpoint_id: &str) -> bool {
    let Ok(guid) = normalize_endpoint_guid(endpoint_id) else {
        return false;
    };
    let fx_path = endpoint_fx_key_path(&guid);
    let Ok(vals) = store.list_values(&fx_path) else {
        return false;
    };
    let keys = [
        PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID,
        PKEY_COMPOSITEFX_MODE_EFFECT_CLSID,
        PKEY_COMPOSITEFX_STREAM_EFFECT_CLSID,
        PKEY_FX_ENDPOINT_EFFECT_CLSID,
        PKEY_FX_MODE_EFFECT_CLSID,
        PKEY_FX_STREAM_EFFECT_CLSID,
    ];
    for key in keys {
        match vals.get(key) {
            Some(RegValue::MultiString(list)) if list.iter().any(|s| is_exo_clsid(s)) => {
                return true;
            }
            Some(RegValue::String(s)) if is_exo_clsid(s) => return true,
            _ => {}
        }
    }
    false
}

pub fn audit_and_heal_endpoints<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<BTreeMap<String, HealStatus>> {
    let guids: Vec<String> = ledger.endpoints.keys().cloned().collect();
    let mut report = BTreeMap::new();
    for guid in guids {
        let ep_path = endpoint_key_path(&guid);
        if !store.key_exists(&ep_path) {
            report.insert(guid, HealStatus::EndpointMissing);
            continue;
        }
        let fx_path = endpoint_fx_key_path(&guid);
        let vals = store.list_values(&fx_path)?;
        if matches!(
            vals.get(PKEY_AUDIOENDPOINT_DISABLE_SYSFX),
            Some(RegValue::Dword(v)) if *v != 0
        ) {
            report.insert(guid, HealStatus::SysFxDisabledByWindows);
            continue;
        }
        if is_exo_registered_on_endpoint(store, &guid) {
            report.insert(guid, HealStatus::Healthy);
            continue;
        }
        if let Some(entry) = ledger.endpoints.get_mut(&guid) {
            entry.original_fx_values = vals;
        }
        let outcome = register_endpoint(store, ledger, &guid)?;
        report.insert(
            guid,
            HealStatus::HealedAfterReset {
                re_chained_oem_count: outcome.preserved_oem_clsids.len(),
            },
        );
    }
    Ok(report)
}

pub fn uninstall_endpoint<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
    endpoint_id: &str,
) -> std::io::Result<bool> {
    let guid = normalize_endpoint_guid(endpoint_id)?;
    let Some(backup) = ledger.endpoints.remove(&guid) else {
        return Ok(false);
    };
    let fx_path = endpoint_fx_key_path(&guid);
    if !store.key_exists(&fx_path) {
        return Ok(true);
    }
    let current = store.list_values(&fx_path)?;
    for name in current.keys() {
        if !backup.original_fx_values.contains_key(name) {
            store.delete_value(&fx_path, name)?;
        }
    }
    for (name, orig_val) in &backup.original_fx_values {
        store.write_value(&fx_path, name, orig_val)?;
    }
    Ok(true)
}

pub trait AudioServiceController {
    fn restart_audio_service(&mut self) -> std::io::Result<()>;
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MockAudioServiceController {
    pub restart_count: usize,
    pub fail_next: bool,
}

impl MockAudioServiceController {
    pub fn new() -> Self {
        Self::default()
    }
}

impl AudioServiceController for MockAudioServiceController {
    fn restart_audio_service(&mut self) -> std::io::Result<()> {
        if self.fail_next {
            self.fail_next = false;
            return Err(std::io::Error::other("mock Audiosrv restart failed"));
        }
        self.restart_count += 1;
        Ok(())
    }
}

#[cfg(windows)]
pub struct Win32AudioServiceController;

#[cfg(windows)]
impl Win32AudioServiceController {
    fn wait_for_state(
        svc: windows::Win32::System::Services::SC_HANDLE,
        desired: u32,
        timeout_ms: u64,
    ) -> std::io::Result<()> {
        use std::time::{Duration, Instant};
        use windows::Win32::System::Services::{QueryServiceStatus, SERVICE_STATUS};

        let deadline = Instant::now() + Duration::from_millis(timeout_ms);
        loop {
            let mut status = SERVICE_STATUS::default();
            unsafe {
                QueryServiceStatus(svc, &mut status)
                    .map_err(|e| std::io::Error::from_raw_os_error(e.code().0))?;
            }
            if status.dwCurrentState.0 == desired {
                return Ok(());
            }
            if Instant::now() >= deadline {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    "timed out waiting for Audiosrv state transition",
                ));
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    }
}

#[cfg(windows)]
impl AudioServiceController for Win32AudioServiceController {
    fn restart_audio_service(&mut self) -> std::io::Result<()> {
        use windows::Win32::System::Services::{
            CloseServiceHandle, ControlService, OpenSCManagerW, OpenServiceW, SC_MANAGER_CONNECT,
            SERVICE_CONTROL_STOP, SERVICE_QUERY_STATUS, SERVICE_RUNNING, SERVICE_START,
            SERVICE_STATUS, SERVICE_STOP, SERVICE_STOPPED, StartServiceW,
        };
        use windows_core::PCWSTR;

        let wide_name: Vec<u16> = AUDIO_SERVICE_NAME
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let scm = unsafe { OpenSCManagerW(PCWSTR::null(), PCWSTR::null(), SC_MANAGER_CONNECT) }
            .map_err(|e| std::io::Error::from_raw_os_error(e.code().0))?;
        let svc_res = unsafe {
            OpenServiceW(
                scm,
                PCWSTR(wide_name.as_ptr()),
                SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS,
            )
        };
        let svc = match svc_res {
            Ok(h) => h,
            Err(e) => {
                unsafe {
                    let _ = CloseServiceHandle(scm);
                }
                return Err(std::io::Error::from_raw_os_error(e.code().0));
            }
        };

        let result = (|| {
            let mut status = SERVICE_STATUS::default();
            let stop_res = unsafe { ControlService(svc, SERVICE_CONTROL_STOP, &mut status) };
            if stop_res.is_ok() {
                Self::wait_for_state(svc, SERVICE_STOPPED.0, 15_000)?;
            }
            unsafe {
                StartServiceW(svc, None)
                    .map_err(|e| std::io::Error::from_raw_os_error(e.code().0))?;
            }
            Self::wait_for_state(svc, SERVICE_RUNNING.0, 15_000)?;
            Ok(())
        })();

        unsafe {
            let _ = CloseServiceHandle(svc);
            let _ = CloseServiceHandle(scm);
        }
        result
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RollbackOutcome {
    pub restored_endpoints: usize,
    pub removed_com_clsids: usize,
    pub restored_protected_audio_dg: Option<PriorDwordState>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ReenableReason {
    ProtectedAudioDgReset,
    EndpointReset,
    AudiodgRejected,
    EffectsOff,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemEqHealthReport {
    pub system_eq_enabled: bool,
    pub drm_disclosure_accepted: bool,
    pub protected_audio_dg_disabled: bool,
    pub endpoints_registered: bool,
    pub audiodg_rejected: bool,
    pub needs_reenable: bool,
    pub reason: Option<ReenableReason>,
}

pub fn com_clsid_key_path(clsid: &str) -> String {
    format!("{COM_CLSID_ROOT}\\{clsid}")
}

pub fn com_inproc_key_path(clsid: &str) -> String {
    format!("{COM_CLSID_ROOT}\\{clsid}\\InprocServer32")
}

pub fn apo_reg_key_path(clsid: &str) -> String {
    format!("{APO_REGISTRATION_ROOT}\\{clsid}")
}

pub fn read_protected_audio_dg_state<R: RegistryStore>(
    store: &R,
) -> std::io::Result<PriorDwordState> {
    match store.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)? {
        Some(RegValue::Dword(v)) => Ok(PriorDwordState::Present(v)),
        _ => Ok(PriorDwordState::Absent),
    }
}

pub fn is_protected_audio_dg_disabled<R: RegistryStore>(store: &R) -> bool {
    matches!(
        store.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE),
        Ok(Some(RegValue::Dword(1)))
    )
}

pub fn enable_unsigned_apo_policy<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<PriorDwordState> {
    let prior = if let Some(saved) = ledger.prior_disable_protected_audio_dg {
        saved
    } else {
        let current = read_protected_audio_dg_state(store)?;
        ledger.prior_disable_protected_audio_dg = Some(current);
        current
    };
    store.ensure_key(AUDIO_POLICY_ROOT)?;
    store.write_value(
        AUDIO_POLICY_ROOT,
        DISABLE_PROTECTED_AUDIO_DG_VALUE,
        &RegValue::Dword(1),
    )?;
    Ok(prior)
}

pub fn restore_unsigned_apo_policy<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<Option<PriorDwordState>> {
    let Some(prior) = ledger.prior_disable_protected_audio_dg.take() else {
        return Ok(None);
    };
    match prior {
        PriorDwordState::Absent => {
            store.delete_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)?;
        }
        PriorDwordState::Present(v) => {
            store.ensure_key(AUDIO_POLICY_ROOT)?;
            store.write_value(
                AUDIO_POLICY_ROOT,
                DISABLE_PROTECTED_AUDIO_DG_VALUE,
                &RegValue::Dword(v),
            )?;
        }
    }
    Ok(Some(prior))
}

pub fn register_com_server<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
    dll_path: &str,
) -> std::io::Result<usize> {
    if dll_path.trim().is_empty() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "dll path must not be empty",
        ));
    }
    for clsid in EXO_APO_CLSIDS {
        let clsid_key = com_clsid_key_path(clsid);
        let inproc_key = com_inproc_key_path(clsid);
        store.ensure_key(&clsid_key)?;
        store.write_value(
            &clsid_key,
            "",
            &RegValue::String("Exo Audio Processing Object".to_string()),
        )?;
        store.ensure_key(&inproc_key)?;
        store.write_value(&inproc_key, "", &RegValue::String(dll_path.to_string()))?;
        store.write_value(
            &inproc_key,
            "ThreadingModel",
            &RegValue::String("Both".to_string()),
        )?;
        if !contains_ci(&ledger.com_clsids_registered, clsid) {
            ledger.com_clsids_registered.push(clsid.to_string());
        }

        let apo_key = apo_reg_key_path(clsid);
        store.ensure_key(&apo_key)?;
        store.write_value(
            &apo_key,
            "FriendlyName",
            &RegValue::String("Exo Audio APO".to_string()),
        )?;
        store.write_value(
            &apo_key,
            "Copyright",
            &RegValue::String("Exo Launcher".to_string()),
        )?;
        store.write_value(&apo_key, "MajorVersion", &RegValue::Dword(1))?;
        store.write_value(&apo_key, "MinorVersion", &RegValue::Dword(0))?;
        store.write_value(&apo_key, "Flags", &RegValue::Dword(0x0000_0001))?;
        store.write_value(&apo_key, "MinInputConnections", &RegValue::Dword(1))?;
        store.write_value(&apo_key, "MaxInputConnections", &RegValue::Dword(1))?;
        store.write_value(&apo_key, "MinOutputConnections", &RegValue::Dword(1))?;
        store.write_value(&apo_key, "MaxOutputConnections", &RegValue::Dword(1))?;
        store.write_value(&apo_key, "MaxInstances", &RegValue::Dword(u32::MAX))?;
        store.write_value(&apo_key, "NumAPOInterfaces", &RegValue::Dword(1))?;
        store.write_value(
            &apo_key,
            "APOInterface0",
            &RegValue::String(IAUDIO_PROCESSING_OBJECT_IID.to_string()),
        )?;
        if !contains_ci(&ledger.apo_reg_clsids_registered, clsid) {
            ledger.apo_reg_clsids_registered.push(clsid.to_string());
        }
    }
    ledger.registered_dll_path = Some(dll_path.to_string());
    Ok(ledger.com_clsids_registered.len())
}

pub fn unregister_com_server<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<usize> {
    let clsids = std::mem::take(&mut ledger.com_clsids_registered);
    for clsid in &clsids {
        store.delete_key(&com_inproc_key_path(clsid))?;
        store.delete_key(&com_clsid_key_path(clsid))?;
    }
    let apo_clsids = std::mem::take(&mut ledger.apo_reg_clsids_registered);
    for clsid in &apo_clsids {
        store.delete_key(&apo_reg_key_path(clsid))?;
    }
    ledger.registered_dll_path = None;
    Ok(clsids.len())
}

pub fn register_with_explicit_consent<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
    endpoint_id: &str,
    dll_path: &str,
    explicit_user_consent: bool,
) -> std::io::Result<RegisterOutcome> {
    if !explicit_user_consent {
        return Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            "explicit user consent is required to register exo_apo.dll",
        ));
    }
    register_com_server(store, ledger, dll_path)?;
    register_endpoint(store, ledger, endpoint_id)
}

#[derive(Debug, Clone, Copy)]
pub struct EnableSystemEqRequest<'a> {
    pub endpoint_id: &'a str,
    pub dll_path: &'a str,
    pub mode: ApoSigningMode,
    pub explicit_user_consent: bool,
    pub drm_disclosure_accepted: bool,
}

pub fn enable_system_eq<R: RegistryStore, S: AudioServiceController>(
    store: &mut R,
    service: &mut S,
    ledger: &mut KeepBackupLedger,
    req: EnableSystemEqRequest<'_>,
) -> std::io::Result<RegisterOutcome> {
    if !req.explicit_user_consent {
        return Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            "explicit user consent is required to enable system-wide EQ",
        ));
    }
    if req.mode == ApoSigningMode::UnsignedFreeRoute && !req.drm_disclosure_accepted {
        return Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            "DRM protected-audio disclosure must be accepted before enabling unsigned APO",
        ));
    }
    if req.mode == ApoSigningMode::UnsignedFreeRoute {
        enable_unsigned_apo_policy(store, ledger)?;
        ledger.drm_disclosure_accepted = true;
    }
    register_com_server(store, ledger, req.dll_path)?;
    let outcome = register_endpoint(store, ledger, req.endpoint_id)?;
    ledger.signing_mode = Some(req.mode);
    ledger.system_eq_enabled = true;
    service.restart_audio_service()?;
    Ok(outcome)
}

pub fn one_click_rollback<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<RollbackOutcome> {
    let restored_endpoints = uninstall_all(store, ledger)?;
    let removed_com_clsids = unregister_com_server(store, ledger)?;
    let restored_protected_audio_dg = restore_unsigned_apo_policy(store, ledger)?;
    ledger.system_eq_enabled = false;
    ledger.signing_mode = None;
    Ok(RollbackOutcome {
        restored_endpoints,
        removed_com_clsids,
        restored_protected_audio_dg,
    })
}

pub fn disable_system_eq<R: RegistryStore, S: AudioServiceController>(
    store: &mut R,
    service: &mut S,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<RollbackOutcome> {
    let outcome = one_click_rollback(store, ledger)?;
    service.restart_audio_service()?;
    Ok(outcome)
}

pub fn check_system_eq_health<R: RegistryStore>(
    store: &R,
    ledger: &KeepBackupLedger,
    audiodg_rejected: bool,
) -> SystemEqHealthReport {
    let protected_audio_dg_disabled = is_protected_audio_dg_disabled(store);
    if !ledger.system_eq_enabled {
        return SystemEqHealthReport {
            system_eq_enabled: false,
            drm_disclosure_accepted: ledger.drm_disclosure_accepted,
            protected_audio_dg_disabled,
            endpoints_registered: false,
            audiodg_rejected: false,
            needs_reenable: false,
            reason: None,
        };
    }

    let mut endpoints_registered = !ledger.endpoints.is_empty();
    let mut effects_off = false;
    for guid in ledger.endpoints.keys() {
        let fx_path = endpoint_fx_key_path(guid);
        if let Ok(vals) = store.list_values(&fx_path)
            && matches!(
                vals.get(PKEY_AUDIOENDPOINT_DISABLE_SYSFX),
                Some(RegValue::Dword(v)) if *v != 0
            )
        {
            effects_off = true;
        }
        if !is_exo_registered_on_endpoint(store, guid) {
            endpoints_registered = false;
        }
    }

    let requires_unsigned_policy = ledger.signing_mode != Some(ApoSigningMode::Signed);
    let reason = if requires_unsigned_policy && !protected_audio_dg_disabled {
        Some(ReenableReason::ProtectedAudioDgReset)
    } else if audiodg_rejected {
        Some(ReenableReason::AudiodgRejected)
    } else if !endpoints_registered {
        Some(ReenableReason::EndpointReset)
    } else if effects_off {
        Some(ReenableReason::EffectsOff)
    } else {
        None
    };

    SystemEqHealthReport {
        system_eq_enabled: true,
        drm_disclosure_accepted: ledger.drm_disclosure_accepted,
        protected_audio_dg_disabled,
        endpoints_registered,
        audiodg_rejected,
        needs_reenable: reason.is_some(),
        reason,
    }
}

pub fn uninstall_all<R: RegistryStore>(
    store: &mut R,
    ledger: &mut KeepBackupLedger,
) -> std::io::Result<usize> {
    let guids: Vec<String> = ledger.endpoints.keys().cloned().collect();
    let mut restored = 0usize;
    for guid in guids {
        if uninstall_endpoint(store, ledger, &guid)? {
            restored += 1;
        }
    }
    Ok(restored)
}

fn contains_ci(list: &[String], needle: &str) -> bool {
    list.iter().any(|s| s.eq_ignore_ascii_case(needle))
}

fn is_exo_clsid(s: &str) -> bool {
    s.eq_ignore_ascii_case(EXO_APO_SFX_CLSID_STR)
        || s.eq_ignore_ascii_case(EXO_APO_MFX_CLSID_STR)
        || s.eq_ignore_ascii_case(EXO_APO_EFX_CLSID_STR)
}

#[cfg(windows)]
pub struct Win32RegistryStore;

#[cfg(windows)]
impl Win32RegistryStore {
    fn to_wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn decode_reg_value(ty: u32, raw: &[u8]) -> Option<RegValue> {
        use windows::Win32::System::Registry::{REG_BINARY, REG_DWORD, REG_MULTI_SZ, REG_SZ};
        if ty == REG_DWORD.0 && raw.len() >= 4 {
            return Some(RegValue::Dword(u32::from_le_bytes([
                raw[0], raw[1], raw[2], raw[3],
            ])));
        }
        if ty == REG_SZ.0 {
            let units: Vec<u16> = raw
                .as_chunks::<2>()
                .0
                .iter()
                .map(|c| u16::from_le_bytes(*c))
                .collect();
            let end = units.iter().position(|&u| u == 0).unwrap_or(units.len());
            return Some(RegValue::String(String::from_utf16_lossy(&units[..end])));
        }
        if ty == REG_MULTI_SZ.0 {
            let units: Vec<u16> = raw
                .as_chunks::<2>()
                .0
                .iter()
                .map(|c| u16::from_le_bytes(*c))
                .collect();
            let mut list = Vec::new();
            let mut start = 0usize;
            for (i, &u) in units.iter().enumerate() {
                if u == 0 {
                    if i > start {
                        list.push(String::from_utf16_lossy(&units[start..i]));
                    }
                    start = i + 1;
                }
            }
            return Some(RegValue::MultiString(list));
        }
        if ty == REG_BINARY.0 {
            return Some(RegValue::Binary(raw.to_vec()));
        }
        None
    }

    fn encode_reg_value(
        val: &RegValue,
    ) -> (windows::Win32::System::Registry::REG_VALUE_TYPE, Vec<u8>) {
        use windows::Win32::System::Registry::{REG_BINARY, REG_DWORD, REG_MULTI_SZ, REG_SZ};
        match val {
            RegValue::Dword(v) => (REG_DWORD, v.to_le_bytes().to_vec()),
            RegValue::String(s) => {
                let mut bytes = Vec::with_capacity((s.len() + 1) * 2);
                for u in s.encode_utf16().chain(std::iter::once(0)) {
                    bytes.extend_from_slice(&u.to_le_bytes());
                }
                (REG_SZ, bytes)
            }
            RegValue::MultiString(list) => {
                let mut bytes = Vec::new();
                for item in list {
                    for u in item.encode_utf16().chain(std::iter::once(0)) {
                        bytes.extend_from_slice(&u.to_le_bytes());
                    }
                }
                bytes.extend_from_slice(&0u16.to_le_bytes());
                (REG_MULTI_SZ, bytes)
            }
            RegValue::Binary(b) => (REG_BINARY, b.clone()),
        }
    }
}

#[cfg(windows)]
impl RegistryStore for Win32RegistryStore {
    fn key_exists(&self, path: &str) -> bool {
        use windows::Win32::System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_READ, RegCloseKey, RegOpenKeyExW,
        };
        use windows_core::PCWSTR;

        let wide = Self::to_wide(path);
        let mut hkey = HKEY::default();
        let status = unsafe {
            RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                PCWSTR(wide.as_ptr()),
                Some(0),
                KEY_READ,
                &mut hkey,
            )
        };
        if status.is_ok() {
            unsafe {
                let _ = RegCloseKey(hkey);
            }
            true
        } else {
            false
        }
    }

    fn list_values(&self, path: &str) -> std::io::Result<BTreeMap<String, RegValue>> {
        use windows::Win32::Foundation::ERROR_NO_MORE_ITEMS;
        use windows::Win32::System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_READ, RegCloseKey, RegEnumValueW, RegOpenKeyExW,
        };
        use windows_core::{PCWSTR, PWSTR};

        let wide = Self::to_wide(path);
        let mut hkey = HKEY::default();
        let status = unsafe {
            RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                PCWSTR(wide.as_ptr()),
                Some(0),
                KEY_READ,
                &mut hkey,
            )
        };
        if status.is_err() {
            return Ok(BTreeMap::new());
        }

        let mut out = BTreeMap::new();
        let mut index = 0u32;
        let mut name_buf = vec![0u16; 512];
        let mut data_buf = vec![0u8; 4096];
        loop {
            let mut name_len = name_buf.len() as u32;
            let mut data_len = data_buf.len() as u32;
            let mut ty = 0u32;
            let res = unsafe {
                RegEnumValueW(
                    hkey,
                    index,
                    Some(PWSTR(name_buf.as_mut_ptr())),
                    &mut name_len,
                    None,
                    Some(&mut ty),
                    Some(data_buf.as_mut_ptr()),
                    Some(&mut data_len),
                )
            };
            if res == ERROR_NO_MORE_ITEMS {
                break;
            }
            if res.is_err() {
                break;
            }
            let name = String::from_utf16_lossy(&name_buf[..name_len as usize]);
            if let Some(val) = Self::decode_reg_value(ty, &data_buf[..data_len as usize]) {
                out.insert(name, val);
            }
            index += 1;
        }
        unsafe {
            let _ = RegCloseKey(hkey);
        }
        Ok(out)
    }

    fn read_value(&self, path: &str, name: &str) -> std::io::Result<Option<RegValue>> {
        let all = self.list_values(path)?;
        Ok(all.get(name).cloned())
    }

    fn write_value(&mut self, path: &str, name: &str, value: &RegValue) -> std::io::Result<()> {
        use windows::Win32::System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_SET_VALUE, RegCloseKey, RegOpenKeyExW, RegSetValueExW,
        };
        use windows_core::PCWSTR;

        let wide_path = Self::to_wide(path);
        let wide_name = Self::to_wide(name);
        let mut hkey = HKEY::default();
        let status = unsafe {
            RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                PCWSTR(wide_path.as_ptr()),
                Some(0),
                KEY_SET_VALUE,
                &mut hkey,
            )
        };
        if status.is_err() {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
        let (ty, bytes) = Self::encode_reg_value(value);
        let write_res =
            unsafe { RegSetValueExW(hkey, PCWSTR(wide_name.as_ptr()), Some(0), ty, Some(&bytes)) };
        unsafe {
            let _ = RegCloseKey(hkey);
        }
        if write_res.is_err() {
            return Err(std::io::Error::from_raw_os_error(write_res.0 as i32));
        }
        Ok(())
    }

    fn delete_value(&mut self, path: &str, name: &str) -> std::io::Result<()> {
        use windows::Win32::System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_SET_VALUE, RegCloseKey, RegDeleteValueW, RegOpenKeyExW,
        };
        use windows_core::PCWSTR;

        let wide_path = Self::to_wide(path);
        let wide_name = Self::to_wide(name);
        let mut hkey = HKEY::default();
        let status = unsafe {
            RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                PCWSTR(wide_path.as_ptr()),
                Some(0),
                KEY_SET_VALUE,
                &mut hkey,
            )
        };
        if status.is_err() {
            return Ok(());
        }
        unsafe {
            let _ = RegDeleteValueW(hkey, PCWSTR(wide_name.as_ptr()));
            let _ = RegCloseKey(hkey);
        }
        Ok(())
    }

    fn ensure_key(&mut self, path: &str) -> std::io::Result<()> {
        use windows::Win32::System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_WRITE, REG_OPTION_NON_VOLATILE, RegCloseKey,
            RegCreateKeyExW,
        };
        use windows_core::PCWSTR;

        if self.key_exists(path) {
            return Ok(());
        }
        let wide = Self::to_wide(path);
        let mut hkey = HKEY::default();
        let status = unsafe {
            RegCreateKeyExW(
                HKEY_LOCAL_MACHINE,
                PCWSTR(wide.as_ptr()),
                Some(0),
                None,
                REG_OPTION_NON_VOLATILE,
                KEY_WRITE,
                None,
                &mut hkey,
                None,
            )
        };
        if status.is_err() {
            return Err(std::io::Error::from_raw_os_error(status.0 as i32));
        }
        unsafe {
            let _ = RegCloseKey(hkey);
        }
        Ok(())
    }

    fn delete_key(&mut self, path: &str) -> std::io::Result<()> {
        use windows::Win32::System::Registry::{HKEY_LOCAL_MACHINE, RegDeleteKeyW};
        use windows_core::PCWSTR;

        let wide = Self::to_wide(path);
        unsafe {
            let _ = RegDeleteKeyW(HKEY_LOCAL_MACHINE, PCWSTR(wide.as_ptr()));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const TEST_GUID: &str = "{9709e4ab-1234-5678-abcd-0123456789ab}";
    const OEM_REALTEK_CLSID: &str = "{11111111-2222-3333-4444-555555555555}";
    const OEM_NEW_AFTER_UPDATE: &str = "{66666666-7777-8888-9999-aaaaaaaaaaaa}";

    #[test]
    fn composite_fx_preserves_oem_apo_and_rolls_back_byte_for_byte() {
        let mut mock = MockRegistryStore::new();
        let mut original = BTreeMap::new();
        original.insert(
            PKEY_FX_ASSOCIATION.to_string(),
            RegValue::String("{00000000-0000-0000-0000-000000000000}".to_string()),
        );
        original.insert(
            PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::String(OEM_REALTEK_CLSID.to_string()),
        );
        original.insert(
            "{custom-oem-key},42".to_string(),
            RegValue::Binary(vec![0xDE, 0xAD, 0xBE, 0xEF]),
        );
        mock.seed_endpoint(TEST_GUID, original.clone());

        let mut ledger = KeepBackupLedger::new();
        let outcome = register_endpoint(&mut mock, &mut ledger, TEST_GUID).expect("register");
        assert!(outcome.used_composite_fx);
        assert_eq!(outcome.preserved_oem_clsids, vec![OEM_REALTEK_CLSID]);
        assert!(!outcome.sysfx_was_disabled);
        assert!(is_exo_registered_on_endpoint(&mock, TEST_GUID));

        let fx_after = mock.snapshot_fx(TEST_GUID).expect("fx exists");
        assert_eq!(
            fx_after.get(PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID),
            Some(&RegValue::MultiString(vec![
                OEM_REALTEK_CLSID.to_string(),
                EXO_APO_EFX_CLSID_STR.to_string(),
            ]))
        );
        assert!(!fx_after.contains_key(PKEY_FX_ENDPOINT_EFFECT_CLSID));

        let json = ledger.to_json().expect("serialize ledger");
        let mut restored_ledger = KeepBackupLedger::from_json(&json).expect("deserialize ledger");
        assert!(uninstall_endpoint(&mut mock, &mut restored_ledger, TEST_GUID).expect("uninstall"));
        assert!(!is_exo_registered_on_endpoint(&mock, TEST_GUID));

        let fx_restored = mock.snapshot_fx(TEST_GUID).expect("fx restored");
        assert_eq!(fx_restored, original);
    }

    #[test]
    fn auto_heal_restores_exo_after_windows_feature_update_wipes_fx_properties() {
        let mut mock = MockRegistryStore::new();
        let mut initial = BTreeMap::new();
        initial.insert(
            PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::MultiString(vec![OEM_REALTEK_CLSID.to_string()]),
        );
        mock.seed_endpoint(TEST_GUID, initial);

        let mut ledger = KeepBackupLedger::new();
        register_endpoint(&mut mock, &mut ledger, TEST_GUID).expect("register");
        assert!(is_exo_registered_on_endpoint(&mock, TEST_GUID));

        let healthy = audit_and_heal_endpoints(&mut mock, &mut ledger).expect("audit healthy");
        assert_eq!(healthy.get(TEST_GUID), Some(&HealStatus::Healthy));

        let mut post_update = BTreeMap::new();
        post_update.insert(
            PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::String(OEM_NEW_AFTER_UPDATE.to_string()),
        );
        mock.seed_endpoint(TEST_GUID, post_update.clone());
        assert!(!is_exo_registered_on_endpoint(&mock, TEST_GUID));

        let healed = audit_and_heal_endpoints(&mut mock, &mut ledger).expect("audit heal");
        assert_eq!(
            healed.get(TEST_GUID),
            Some(&HealStatus::HealedAfterReset {
                re_chained_oem_count: 1
            })
        );
        assert!(is_exo_registered_on_endpoint(&mock, TEST_GUID));
        let fx = mock.snapshot_fx(TEST_GUID).unwrap();
        assert_eq!(
            fx.get(PKEY_COMPOSITEFX_ENDPOINT_EFFECT_CLSID),
            Some(&RegValue::MultiString(vec![
                OEM_NEW_AFTER_UPDATE.to_string(),
                EXO_APO_EFX_CLSID_STR.to_string(),
            ]))
        );

        uninstall_all(&mut mock, &mut ledger).expect("uninstall all");
        assert_eq!(mock.snapshot_fx(TEST_GUID).unwrap(), post_update);
    }

    #[test]
    fn reports_windows_audio_enhancements_disabled() {
        let mut mock = MockRegistryStore::new();
        let mut fx = BTreeMap::new();
        fx.insert(
            PKEY_AUDIOENDPOINT_DISABLE_SYSFX.to_string(),
            RegValue::Dword(1),
        );
        mock.seed_endpoint(TEST_GUID, fx);
        let mut ledger = KeepBackupLedger::new();
        let out = register_endpoint(&mut mock, &mut ledger, TEST_GUID).expect("register");
        assert!(out.sysfx_was_disabled);

        let audit = audit_and_heal_endpoints(&mut mock, &mut ledger).expect("audit");
        assert_eq!(
            audit.get(TEST_GUID),
            Some(&HealStatus::SysFxDisabledByWindows)
        );
    }

    #[test]
    fn refuses_registration_without_explicit_user_consent() {
        let mut mock = MockRegistryStore::new();
        let mut original = BTreeMap::new();
        original.insert(
            PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::String(OEM_REALTEK_CLSID.to_string()),
        );
        mock.seed_endpoint(TEST_GUID, original);
        let before = mock.clone();
        let mut ledger = KeepBackupLedger::new();

        let err = register_with_explicit_consent(
            &mut mock,
            &mut ledger,
            TEST_GUID,
            "C:\\Program Files\\Exo\\exo_apo.dll",
            false,
        )
        .unwrap_err();
        assert_eq!(err.kind(), std::io::ErrorKind::PermissionDenied);
        assert_eq!(mock, before);
        assert!(ledger.endpoints.is_empty());
        assert!(ledger.com_clsids_registered.is_empty());
    }

    #[test]
    fn explicit_consent_register_and_one_click_rollback_preserves_protected_audiodg() {
        let mut mock = MockRegistryStore::new();
        let mut original = BTreeMap::new();
        original.insert(
            PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::String(OEM_REALTEK_CLSID.to_string()),
        );
        mock.seed_endpoint(TEST_GUID, original);
        let before = mock.clone();
        let mut ledger = KeepBackupLedger::new();

        let outcome = register_with_explicit_consent(
            &mut mock,
            &mut ledger,
            TEST_GUID,
            "C:\\Exo\\exo_apo.dll",
            true,
        )
        .expect("register with explicit consent");
        assert_eq!(outcome.preserved_oem_clsids, vec![OEM_REALTEK_CLSID]);
        assert_eq!(ledger.com_clsids_registered.len(), 3);
        assert!(mock.key_exists(&com_inproc_key_path(EXO_APO_EFX_CLSID_STR)));
        assert!(
            mock.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap()
                .is_none()
        );

        let rb = one_click_rollback(&mut mock, &mut ledger).expect("one-click rollback");
        assert_eq!(rb.restored_endpoints, 1);
        assert_eq!(rb.removed_com_clsids, 3);
        assert_eq!(mock, before);
        assert!(ledger.endpoints.is_empty());
        assert!(ledger.com_clsids_registered.is_empty());
    }

    #[test]
    fn unsigned_free_route_snapshots_and_restores_all_three_prior_states_with_service_restart() {
        for prior in [
            PriorDwordState::Absent,
            PriorDwordState::Present(0),
            PriorDwordState::Present(1),
        ] {
            let mut mock = MockRegistryStore::new();
            let mut original = BTreeMap::new();
            original.insert(
                PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
                RegValue::String(OEM_REALTEK_CLSID.to_string()),
            );
            mock.seed_endpoint(TEST_GUID, original);
            match prior {
                PriorDwordState::Absent => {
                    mock.ensure_key(AUDIO_POLICY_ROOT).unwrap();
                }
                PriorDwordState::Present(v) => {
                    mock.ensure_key(AUDIO_POLICY_ROOT).unwrap();
                    mock.write_value(
                        AUDIO_POLICY_ROOT,
                        DISABLE_PROTECTED_AUDIO_DG_VALUE,
                        &RegValue::Dword(v),
                    )
                    .unwrap();
                }
            }
            let before = mock.clone();
            let mut svc = MockAudioServiceController::new();
            let mut ledger = KeepBackupLedger::new();

            enable_system_eq(
                &mut mock,
                &mut svc,
                &mut ledger,
                EnableSystemEqRequest {
                    endpoint_id: TEST_GUID,
                    dll_path: "C:\\Exo\\exo_apo.dll",
                    mode: ApoSigningMode::UnsignedFreeRoute,
                    explicit_user_consent: true,
                    drm_disclosure_accepted: true,
                },
            )
            .expect("enable unsigned system eq");

            assert_eq!(ledger.prior_disable_protected_audio_dg, Some(prior));
            assert!(ledger.system_eq_enabled);
            assert!(ledger.drm_disclosure_accepted);
            assert_eq!(svc.restart_count, 1);
            assert_eq!(
                mock.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                    .unwrap(),
                Some(RegValue::Dword(1))
            );
            assert!(mock.key_exists(&apo_reg_key_path(EXO_APO_EFX_CLSID_STR)));

            let rb =
                disable_system_eq(&mut mock, &mut svc, &mut ledger).expect("turn off system eq");
            assert_eq!(rb.restored_endpoints, 1);
            assert_eq!(rb.removed_com_clsids, 3);
            assert_eq!(rb.restored_protected_audio_dg, Some(prior));
            assert_eq!(svc.restart_count, 2);
            assert!(!ledger.system_eq_enabled);
            assert_eq!(mock, before);
            match prior {
                PriorDwordState::Absent => assert!(
                    mock.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                        .unwrap()
                        .is_none()
                ),
                PriorDwordState::Present(v) => assert_eq!(
                    mock.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                        .unwrap(),
                    Some(RegValue::Dword(v))
                ),
            }
        }
    }

    #[test]
    fn unsigned_free_route_refuses_without_drm_disclosure_and_never_restarts_service() {
        let mut mock = MockRegistryStore::new();
        mock.seed_endpoint(TEST_GUID, BTreeMap::new());
        let before = mock.clone();
        let mut svc = MockAudioServiceController::new();
        let mut ledger = KeepBackupLedger::new();

        let err = enable_system_eq(
            &mut mock,
            &mut svc,
            &mut ledger,
            EnableSystemEqRequest {
                endpoint_id: TEST_GUID,
                dll_path: "C:\\Exo\\exo_apo.dll",
                mode: ApoSigningMode::UnsignedFreeRoute,
                explicit_user_consent: true,
                drm_disclosure_accepted: false,
            },
        )
        .unwrap_err();
        assert_eq!(err.kind(), std::io::ErrorKind::PermissionDenied);
        assert_eq!(mock, before);
        assert_eq!(svc.restart_count, 0);
        assert!(!ledger.system_eq_enabled);
    }

    #[test]
    fn launch_health_check_detects_windows_update_reset_and_audiodg_rejection_and_reenable_keeps_original_prior()
     {
        let mut mock = MockRegistryStore::new();
        mock.seed_endpoint(TEST_GUID, BTreeMap::new());
        mock.ensure_key(AUDIO_POLICY_ROOT).unwrap();
        let mut svc = MockAudioServiceController::new();
        let mut ledger = KeepBackupLedger::new();

        let off = check_system_eq_health(&mock, &ledger, false);
        assert!(!off.system_eq_enabled);
        assert!(!off.needs_reenable);

        enable_system_eq(
            &mut mock,
            &mut svc,
            &mut ledger,
            EnableSystemEqRequest {
                endpoint_id: TEST_GUID,
                dll_path: "C:\\Exo\\exo_apo.dll",
                mode: ApoSigningMode::UnsignedFreeRoute,
                explicit_user_consent: true,
                drm_disclosure_accepted: true,
            },
        )
        .unwrap();
        let ok = check_system_eq_health(&mock, &ledger, false);
        assert!(ok.system_eq_enabled);
        assert!(!ok.needs_reenable);

        let rejected = check_system_eq_health(&mock, &ledger, true);
        assert!(rejected.needs_reenable);
        assert_eq!(rejected.reason, Some(ReenableReason::AudiodgRejected));

        mock.write_value(
            AUDIO_POLICY_ROOT,
            DISABLE_PROTECTED_AUDIO_DG_VALUE,
            &RegValue::Dword(0),
        )
        .unwrap();
        let reset = check_system_eq_health(&mock, &ledger, false);
        assert!(reset.needs_reenable);
        assert_eq!(reset.reason, Some(ReenableReason::ProtectedAudioDgReset));

        enable_system_eq(
            &mut mock,
            &mut svc,
            &mut ledger,
            EnableSystemEqRequest {
                endpoint_id: TEST_GUID,
                dll_path: "C:\\Exo\\exo_apo.dll",
                mode: ApoSigningMode::UnsignedFreeRoute,
                explicit_user_consent: true,
                drm_disclosure_accepted: true,
            },
        )
        .unwrap();
        assert_eq!(
            ledger.prior_disable_protected_audio_dg,
            Some(PriorDwordState::Absent)
        );
        let healed = check_system_eq_health(&mock, &ledger, false);
        assert!(!healed.needs_reenable);

        disable_system_eq(&mut mock, &mut svc, &mut ledger).unwrap();
        assert!(
            mock.read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap()
                .is_none()
        );
    }
}
