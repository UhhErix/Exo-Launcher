#![allow(clippy::missing_safety_doc)]

pub mod apo_com;
pub mod autoeq_db;
pub mod dsp;
pub mod endpoint_reg;
pub mod shm_ipc;

use autoeq_db::{
    ActiveGameAudioSession, AutoEqCatalog, AutoEqEntry, EqSoundProfile, GameAudioRouter,
    HeadphoneMatch,
};
use dsp::{DspConfig, SpatialMode};
use endpoint_reg::{HealStatus, KeepBackupLedger, RegistryStore, audit_and_heal_endpoints};
use exo_rpc::anticheat::AntiCheatClass;
use shm_ipc::ShmChannel;
use std::collections::BTreeMap;

pub struct LiveEqController {
    endpoint_id: String,
    shm: ShmChannel,
    catalog: AutoEqCatalog,
    headphone: Option<AutoEqEntry>,
    desktop_profile: EqSoundProfile,
    active_game: Option<ActiveGameAudioSession>,
    night_shift: bool,
    spatial_mode: SpatialMode,
    spatial_strength: f64,
    ab_compare_bypass: bool,
    last_published_gen: u64,
}

impl LiveEqController {
    pub fn new(endpoint_id: impl Into<String>, shm: ShmChannel, catalog: AutoEqCatalog) -> Self {
        Self {
            endpoint_id: endpoint_id.into(),
            shm,
            catalog,
            headphone: None,
            desktop_profile: EqSoundProfile::Reference,
            active_game: None,
            night_shift: false,
            spatial_mode: SpatialMode::Off,
            spatial_strength: 0.5,
            ab_compare_bypass: false,
            last_published_gen: 0,
        }
    }

    pub fn endpoint_id(&self) -> &str {
        &self.endpoint_id
    }

    pub fn last_published_generation(&self) -> u64 {
        self.last_published_gen
    }

    pub fn active_game(&self) -> Option<&ActiveGameAudioSession> {
        self.active_game.as_ref()
    }

    pub fn recognize_connected_headset(
        &self,
        endpoint_name: &str,
        pnp_or_usb_id: Option<&str>,
    ) -> Option<HeadphoneMatch> {
        self.catalog.recognize_device(endpoint_name, pnp_or_usb_id)
    }

    pub fn set_headphone(&mut self, entry: Option<AutoEqEntry>) -> u64 {
        self.headphone = entry;
        self.push_live()
    }

    pub fn set_desktop_profile(&mut self, profile: EqSoundProfile) -> u64 {
        self.desktop_profile = profile;
        self.push_live()
    }

    pub fn set_spatial(&mut self, mode: SpatialMode, strength: f64) -> u64 {
        self.spatial_mode = mode;
        self.spatial_strength = strength;
        self.push_live()
    }

    pub fn set_night_shift(&mut self, enabled: bool) -> u64 {
        self.night_shift = enabled;
        self.push_live()
    }

    pub fn set_ab_compare_bypass(&mut self, bypass: bool) -> u64 {
        self.ab_compare_bypass = bypass;
        self.push_live()
    }

    pub fn on_game_start(
        &mut self,
        game_id: impl Into<String>,
        title: impl Into<String>,
        anti_cheat_class: AntiCheatClass,
        override_profile: Option<EqSoundProfile>,
    ) -> (ActiveGameAudioSession, u64) {
        let session =
            GameAudioRouter::session_for_game(game_id, title, anti_cheat_class, override_profile);
        self.active_game = Some(session.clone());
        let generation = self.push_live();
        (session, generation)
    }

    pub fn on_game_exit(&mut self) -> u64 {
        self.active_game = None;
        self.push_live()
    }

    pub fn current_config(&self) -> DspConfig {
        let profile = self
            .active_game
            .as_ref()
            .map(|g| g.profile)
            .unwrap_or(self.desktop_profile);
        GameAudioRouter::build_dsp_config(
            self.headphone.as_ref(),
            profile,
            self.night_shift,
            self.spatial_mode,
            self.spatial_strength,
            self.ab_compare_bypass,
        )
    }

    pub fn push_live(&mut self) -> u64 {
        let cfg = self.current_config();
        let generation = self.shm.publish(&cfg);
        self.last_published_gen = generation;
        generation
    }

    pub fn startup_heal<R: RegistryStore>(
        store: &mut R,
        ledger: &mut KeepBackupLedger,
    ) -> std::io::Result<BTreeMap<String, HealStatus>> {
        audit_and_heal_endpoints(store, ledger)
    }
}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllGetClassObject(
    rclsid: *const windows_core::GUID,
    riid: *const windows_core::GUID,
    ppv: *mut *mut core::ffi::c_void,
) -> windows_core::HRESULT {
    unsafe { apo_com::dll_get_class_object(rclsid, riid, ppv) }
}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub extern "system" fn DllCanUnloadNow() -> windows_core::HRESULT {
    apo_com::dll_can_unload_now()
}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub extern "system" fn DllRegisterServer() -> windows_core::HRESULT {
    windows::Win32::Foundation::S_OK
}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub extern "system" fn DllUnregisterServer() -> windows_core::HRESULT {
    windows::Win32::Foundation::S_OK
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dsp::ParametricBand;
    use crate::shm_ipc::ShmChannel;

    #[test]
    fn clean_room_zero_injection_and_zero_gpl_audit() {
        let banned = [
            "CreateRemoteThread",
            "WriteProcessMemory",
            "VirtualAllocEx",
            "SetWindowsHookEx",
            "OpenProcess",
            "EqualizerAPO.dll",
            "GPL-2.0",
            "GPL-3.0",
        ];
        let files = [
            include_str!("lib.rs"),
            include_str!("apo_com.rs"),
            include_str!("dsp.rs"),
            include_str!("shm_ipc.rs"),
            include_str!("autoeq_db.rs"),
            include_str!("endpoint_reg.rs"),
        ];
        for src in files {
            for needle in banned {
                assert!(
                    !src.contains(needle) || src.contains("let banned ="),
                    "forbidden symbol {needle} found in exo-audio-apo source"
                );
            }
        }
    }

    #[test]
    fn live_controller_switches_between_music_and_fps_without_restarts() {
        let (writer, reader) = ShmChannel::in_memory("{live-controller-test}");
        let mut ctrl = LiveEqController::new(
            "{live-controller-test}",
            writer,
            AutoEqCatalog::builtin_usb_catalog(),
        );
        let mut seen = 0u64;

        let g1 = ctrl.set_headphone(Some(AutoEqEntry {
            id: "oratory1990/over-ear/Sennheiser HD 600".to_string(),
            name: "Sennheiser HD 600".to_string(),
            measurer: "oratory1990".to_string(),
            form: autoeq_db::HeadphoneForm::OverEar,
            usb_vid: None,
            usb_pid: None,
            bands: vec![ParametricBand::low_shelf(105.0, 5.5, 0.71)],
        }));
        let cfg1 = reader.try_read_if_newer(&mut seen).expect("headphone cfg");
        assert_eq!(seen, g1);
        assert_eq!(cfg1.band_count, 1);

        let (session, g2) = ctrl.on_game_start(
            "steam:730",
            "Counter-Strike 2",
            AntiCheatClass::Unknown,
            None,
        );
        assert_eq!(session.profile, EqSoundProfile::CompetitiveClarity);
        assert_eq!(
            session.effective_anti_cheat_class,
            AntiCheatClass::CompetitivePvpOnline
        );
        assert!(session.uses_windows_apo_only);
        assert!(session.forbids_game_process_touch);

        let cfg2 = reader.try_read_if_newer(&mut seen).expect("fps cfg");
        assert_eq!(seen, g2);
        assert_eq!(cfg2.band_count, 8);

        let g3 = ctrl.set_ab_compare_bypass(true);
        let cfg3 = reader.try_read_if_newer(&mut seen).expect("ab bypass");
        assert_eq!(seen, g3);
        assert!(cfg3.bypass);

        let g4 = ctrl.set_ab_compare_bypass(false);
        let cfg4 = reader.try_read_if_newer(&mut seen).expect("ab restore");
        assert_eq!(seen, g4);
        assert!(!cfg4.bypass);

        let g5 = ctrl.on_game_exit();
        let cfg5 = reader.try_read_if_newer(&mut seen).expect("return flat");
        assert_eq!(seen, g5);
        assert_eq!(cfg5.band_count, 1);
    }
}
