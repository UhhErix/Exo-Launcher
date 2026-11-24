use crate::edid::parse_edid;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

pub const DEFAULT_REVERT_TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayInfo {
    pub id: String,
    pub name: String,
    pub is_internal_edp: bool,
    pub max_pixel_clock_mhz: u16,
}

pub trait DisplayBackend: Send {
    fn list_displays(&self) -> Vec<DisplayInfo>;
    fn write_edid_override(&mut self, display_id: &str, edid: &[u8]) -> Result<(), String>;
    fn delete_edid_override(&mut self, display_id: &str) -> Result<(), String>;
    fn restart_display_adapter(&mut self) -> Result<(), String>;
    fn install_icc_profile(&mut self, display_id: &str, profile: &[u8]) -> Result<(), String>;
    fn ddc_get_vcp(&self, display_id: &str, vcp: u8) -> Result<u16, String>;
    fn ddc_set_vcp(&mut self, display_id: &str, vcp: u8, value: u16) -> Result<(), String>;
}

#[derive(Debug, Clone, Default)]
pub struct MockDisplayState {
    pub displays: Vec<DisplayInfo>,
    pub overrides: HashMap<String, Vec<u8>>,
    pub icc_profiles: HashMap<String, Vec<u8>>,
    pub vcp_values: HashMap<(String, u8), u16>,
    pub adapter_restarts: usize,
    pub delete_count: usize,
    pub pending_journal: Option<String>,
}

pub struct MockDisplayBackend {
    state: Arc<Mutex<MockDisplayState>>,
}

impl MockDisplayBackend {
    pub fn new(displays: Vec<DisplayInfo>) -> Self {
        Self {
            state: Arc::new(Mutex::new(MockDisplayState {
                displays,
                ..Default::default()
            })),
        }
    }

    pub fn state_handle(&self) -> Arc<Mutex<MockDisplayState>> {
        Arc::clone(&self.state)
    }
}

impl DisplayBackend for MockDisplayBackend {
    fn list_displays(&self) -> Vec<DisplayInfo> {
        self.state.lock().unwrap().displays.clone()
    }

    fn write_edid_override(&mut self, display_id: &str, edid: &[u8]) -> Result<(), String> {
        self.state
            .lock()
            .unwrap()
            .overrides
            .insert(display_id.to_string(), edid.to_vec());
        Ok(())
    }

    fn delete_edid_override(&mut self, display_id: &str) -> Result<(), String> {
        let mut st = self.state.lock().unwrap();
        st.overrides.remove(display_id);
        st.delete_count += 1;
        Ok(())
    }

    fn restart_display_adapter(&mut self) -> Result<(), String> {
        self.state.lock().unwrap().adapter_restarts += 1;
        Ok(())
    }

    fn install_icc_profile(&mut self, display_id: &str, profile: &[u8]) -> Result<(), String> {
        self.state
            .lock()
            .unwrap()
            .icc_profiles
            .insert(display_id.to_string(), profile.to_vec());
        Ok(())
    }

    fn ddc_get_vcp(&self, display_id: &str, vcp: u8) -> Result<u16, String> {
        self.state
            .lock()
            .unwrap()
            .vcp_values
            .get(&(display_id.to_string(), vcp))
            .copied()
            .ok_or_else(|| format!("VCP 0x{vcp:02X} unavailable"))
    }

    fn ddc_set_vcp(&mut self, display_id: &str, vcp: u8, value: u16) -> Result<(), String> {
        self.state
            .lock()
            .unwrap()
            .vcp_values
            .insert((display_id.to_string(), vcp), value);
        Ok(())
    }
}

pub struct SafeEdidOverrideSession<B: DisplayBackend + 'static> {
    backend: Arc<Mutex<B>>,
    journal: Arc<Mutex<Option<String>>>,
    confirmed: Arc<AtomicBool>,
    reverted: Arc<AtomicBool>,
    watchdog: Option<JoinHandle<()>>,
}

impl<B: DisplayBackend + 'static> SafeEdidOverrideSession<B> {
    pub fn apply_with_watchdog(
        backend: Arc<Mutex<B>>,
        journal: Arc<Mutex<Option<String>>>,
        display_id: &str,
        new_edid: &[u8],
        timeout: Duration,
    ) -> Result<Self, String> {
        let parsed = parse_edid(new_edid)?;
        if !parsed.checksums_valid {
            return Err("refusing EDID override with invalid checksum".into());
        }
        let info = backend
            .lock()
            .unwrap()
            .list_displays()
            .into_iter()
            .find(|d| d.id == display_id)
            .ok_or_else(|| format!("unknown display '{display_id}'"))?;
        if info.is_internal_edp
            && let Some(vrr) = parsed.vrr_range
            && vrr.max_pixel_clock_mhz > info.max_pixel_clock_mhz
        {
            return Err("laptop eDP pixel-clock overclocking is blocked".into());
        }

        *journal.lock().unwrap() = Some(display_id.to_string());
        {
            let mut b = backend.lock().unwrap();
            b.write_edid_override(display_id, new_edid)?;
            b.restart_display_adapter()?;
        }

        let confirmed = Arc::new(AtomicBool::new(false));
        let reverted = Arc::new(AtomicBool::new(false));
        let bg_confirmed = Arc::clone(&confirmed);
        let bg_reverted = Arc::clone(&reverted);
        let bg_backend = Arc::clone(&backend);
        let bg_journal = Arc::clone(&journal);
        let target_id = display_id.to_string();

        let handle = std::thread::spawn(move || {
            let step = Duration::from_millis(10);
            let mut waited = Duration::ZERO;
            while waited < timeout {
                if bg_confirmed.load(Ordering::Acquire) {
                    return;
                }
                let sleep_for = step.min(timeout - waited);
                std::thread::sleep(sleep_for);
                waited += sleep_for;
            }
            if !bg_confirmed.load(Ordering::Acquire) {
                if let Ok(mut b) = bg_backend.lock() {
                    let _ = b.delete_edid_override(&target_id);
                    let _ = b.restart_display_adapter();
                }
                *bg_journal.lock().unwrap() = None;
                bg_reverted.store(true, Ordering::Release);
            }
        });

        Ok(Self {
            backend,
            journal,
            confirmed,
            reverted,
            watchdog: Some(handle),
        })
    }

    pub fn confirm(&mut self) {
        self.confirmed.store(true, Ordering::Release);
        *self.journal.lock().unwrap() = None;
        if let Some(h) = self.watchdog.take() {
            let _ = h.join();
        }
    }

    pub fn wait_for_watchdog(&mut self) -> bool {
        if let Some(h) = self.watchdog.take() {
            let _ = h.join();
        }
        self.reverted.load(Ordering::Acquire)
    }

    pub fn recover_from_crash_journal(
        backend: &Arc<Mutex<B>>,
        journal: &Arc<Mutex<Option<String>>>,
    ) -> bool {
        let pending = journal.lock().unwrap().take();
        if let Some(display_id) = pending {
            if let Ok(mut b) = backend.lock() {
                let _ = b.delete_edid_override(&display_id);
                let _ = b.restart_display_adapter();
            }
            true
        } else {
            false
        }
    }
}

impl<B: DisplayBackend + 'static> Drop for SafeEdidOverrideSession<B> {
    fn drop(&mut self) {
        if !self.confirmed.load(Ordering::Acquire) && !self.reverted.load(Ordering::Acquire) {
            let _ = Self::recover_from_crash_journal(&self.backend, &self.journal);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::edid::{strip_4096p_modes, synthetic_oled_edid_fixture};

    #[test]
    fn watchdog_auto_reverts_unconfirmed_edid_override() {
        let mock = MockDisplayBackend::new(vec![DisplayInfo {
            id: "disp:0".into(),
            name: "Mock OLED".into(),
            is_internal_edp: false,
            max_pixel_clock_mhz: 1200,
        }]);
        let st = mock.state_handle();
        let backend = Arc::new(Mutex::new(mock));
        let journal = Arc::new(Mutex::new(None));
        let edid = strip_4096p_modes(&synthetic_oled_edid_fixture(true)).unwrap();

        let mut sess = SafeEdidOverrideSession::apply_with_watchdog(
            Arc::clone(&backend),
            Arc::clone(&journal),
            "disp:0",
            &edid,
            Duration::from_millis(60),
        )
        .unwrap();

        assert!(sess.wait_for_watchdog());
        let guard = st.lock().unwrap();
        assert!(!guard.overrides.contains_key("disp:0"));
        assert_eq!(guard.delete_count, 1);
        assert_eq!(guard.adapter_restarts, 2);
        assert!(journal.lock().unwrap().is_none());
    }

    #[test]
    fn crash_recovery_journal_cleans_up_unconfirmed_override() {
        let mock = MockDisplayBackend::new(vec![DisplayInfo {
            id: "disp:0".into(),
            name: "Mock OLED".into(),
            is_internal_edp: false,
            max_pixel_clock_mhz: 1200,
        }]);
        let st = mock.state_handle();
        let backend = Arc::new(Mutex::new(mock));
        let journal = Arc::new(Mutex::new(Some("disp:0".to_string())));
        st.lock()
            .unwrap()
            .overrides
            .insert("disp:0".into(), vec![0; 128]);

        assert!(SafeEdidOverrideSession::recover_from_crash_journal(
            &backend, &journal
        ));
        assert!(!st.lock().unwrap().overrides.contains_key("disp:0"));
    }

    #[test]
    fn laptop_edp_overclock_is_blocked() {
        let mock = MockDisplayBackend::new(vec![DisplayInfo {
            id: "edp:0".into(),
            name: "Laptop eDP".into(),
            is_internal_edp: true,
            max_pixel_clock_mhz: 600,
        }]);
        let backend = Arc::new(Mutex::new(mock));
        let journal = Arc::new(Mutex::new(None));
        let edid = synthetic_oled_edid_fixture(false);
        let res = SafeEdidOverrideSession::apply_with_watchdog(
            backend,
            journal,
            "edp:0",
            &edid,
            Duration::from_millis(50),
        );
        assert!(res.is_err());
    }
}
