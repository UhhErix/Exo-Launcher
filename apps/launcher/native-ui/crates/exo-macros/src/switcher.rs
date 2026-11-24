use crate::model::{MacroDelivery, MacroProfile};
use std::collections::{HashSet, VecDeque};

pub const DEFAULT_DEBOUNCE_MS: u64 = 350;
pub const DEFAULT_EXIT_GRACE_MS: u64 = 1500;
pub const DEFAULT_HARDWARE_COOLDOWN_MS: u64 = 2000;
pub const MAX_HARDWARE_WRITES_PER_MINUTE: usize = 10;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SwitchDecision {
    pub changed: bool,
    pub profile_id: String,
    pub game_id: Option<String>,
    pub software_allowed: bool,
    pub hardware_write_allowed: bool,
    pub blocked_reason: Option<String>,
}

pub struct ProfileSwitcher {
    debounce_ms: u64,
    exit_grace_ms: u64,
    hardware_cooldown_ms: u64,
    running_set: HashSet<String>,
    running_order: Vec<String>,
    foreground_exe: Option<String>,
    active_profile_id: String,
    active_game_id: Option<String>,
    active_onboard_hash: Option<u64>,
    last_hw_write_ms: Option<u64>,
    hw_write_window: VecDeque<u64>,
    pending: Option<(String, Option<String>, u64)>,
    grace_hold: Option<(String, String, u64)>,
    pub applied_switches: u32,
    pub suppressed_flaps: u32,
    pub hardware_writes: u32,
    pub suppressed_hardware_writes: u32,
}

impl Default for ProfileSwitcher {
    fn default() -> Self {
        Self::new(
            DEFAULT_DEBOUNCE_MS,
            DEFAULT_EXIT_GRACE_MS,
            DEFAULT_HARDWARE_COOLDOWN_MS,
        )
    }
}

impl ProfileSwitcher {
    pub fn new(debounce_ms: u64, exit_grace_ms: u64, hardware_cooldown_ms: u64) -> Self {
        Self {
            debounce_ms,
            exit_grace_ms,
            hardware_cooldown_ms,
            running_set: HashSet::new(),
            running_order: Vec::new(),
            foreground_exe: None,
            active_profile_id: "desktop".to_string(),
            active_game_id: None,
            active_onboard_hash: None,
            last_hw_write_ms: None,
            hw_write_window: VecDeque::new(),
            pending: None,
            grace_hold: None,
            applied_switches: 0,
            suppressed_flaps: 0,
            hardware_writes: 0,
            suppressed_hardware_writes: 0,
        }
    }

    pub fn active_profile_id(&self) -> &str {
        &self.active_profile_id
    }

    pub fn set_initial(&mut self, profile: &MacroProfile) {
        self.active_profile_id = profile.id.clone();
        self.active_game_id = None;
        self.active_onboard_hash = onboard_hash(profile);
        self.pending = None;
        self.grace_hold = None;
    }

    pub fn observe_game(
        &mut self,
        game_id: &str,
        running: bool,
        now_ms: u64,
        profiles: &[MacroProfile],
    ) {
        let key = game_id.to_ascii_lowercase();
        if running {
            if self.running_set.insert(key.clone()) {
                self.running_order.push(key.clone());
            }
            if self
                .grace_hold
                .as_ref()
                .is_some_and(|(_, gid, _)| gid == &key)
            {
                self.grace_hold = None;
                self.suppressed_flaps += 1;
            }
        } else if self.running_set.remove(&key) {
            self.running_order.retain(|g| g != &key);
            if self.active_game_id.as_deref() == Some(key.as_str()) && self.exit_grace_ms > 0 {
                self.grace_hold = Some((
                    self.active_profile_id.clone(),
                    key,
                    now_ms.saturating_add(self.exit_grace_ms),
                ));
            }
        } else {
            return;
        }
        self.queue_candidate(now_ms, profiles);
    }

    pub fn observe_foreground_app(
        &mut self,
        exe: Option<&str>,
        now_ms: u64,
        profiles: &[MacroProfile],
    ) {
        let norm = exe.and_then(normalize_exe);
        if self.foreground_exe == norm {
            return;
        }
        self.foreground_exe = norm;
        self.queue_candidate(now_ms, profiles);
    }

    pub fn poll<F>(
        &mut self,
        now_ms: u64,
        profiles: &[MacroProfile],
        software_allowed_for_game: F,
    ) -> SwitchDecision
    where
        F: Fn(&str) -> bool,
    {
        if let Some((_, _, until)) = &self.grace_hold
            && now_ms >= *until
        {
            self.grace_hold = None;
            self.queue_candidate(now_ms, profiles);
        }

        let Some((target_id, target_game, ready_at)) = self.pending.clone() else {
            return self.no_change();
        };
        if now_ms < ready_at {
            return self.no_change();
        }
        self.pending = None;

        if target_id == self.active_profile_id && target_game == self.active_game_id {
            self.suppressed_flaps += 1;
            return self.no_change();
        }

        let Some(profile) = profiles.iter().find(|p| p.id == target_id) else {
            return self.no_change();
        };

        let has_software = profile
            .bindings
            .iter()
            .any(|b| b.enabled && b.delivery == MacroDelivery::SoftwareSidecar);

        let (software_allowed, blocked_reason) = match target_game.as_deref() {
            Some(gid) if has_software && !software_allowed_for_game(gid) => (
                false,
                Some("Online, competitive anti-cheat, and unknown games allow Tier 1 onboard hardware macros only.".into()),
            ),
            _ => (true, None),
        };

        let next_hash = onboard_hash(profile);
        let hardware_write_allowed = if next_hash.is_some() && next_hash != self.active_onboard_hash
        {
            if self.try_reserve_hw_write(now_ms) {
                self.active_onboard_hash = next_hash;
                true
            } else {
                false
            }
        } else {
            false
        };

        self.active_profile_id = profile.id.clone();
        self.active_game_id = target_game.clone();
        self.applied_switches += 1;

        SwitchDecision {
            changed: true,
            profile_id: self.active_profile_id.clone(),
            game_id: target_game,
            software_allowed,
            hardware_write_allowed,
            blocked_reason,
        }
    }

    fn no_change(&self) -> SwitchDecision {
        SwitchDecision {
            changed: false,
            profile_id: self.active_profile_id.clone(),
            game_id: self.active_game_id.clone(),
            software_allowed: true,
            hardware_write_allowed: false,
            blocked_reason: None,
        }
    }

    fn queue_candidate(&mut self, now_ms: u64, profiles: &[MacroProfile]) {
        let (candidate_id, candidate_game) = self.resolve_target(now_ms, profiles);
        if candidate_id == self.active_profile_id && candidate_game == self.active_game_id {
            if self.pending.take().is_some() {
                self.suppressed_flaps += 1;
            }
            return;
        }
        if let Some((prev_id, _, _)) = &self.pending
            && prev_id != &candidate_id
        {
            self.suppressed_flaps += 1;
        }
        self.pending = Some((
            candidate_id,
            candidate_game,
            now_ms.saturating_add(self.debounce_ms),
        ));
    }

    fn resolve_target(&self, now_ms: u64, profiles: &[MacroProfile]) -> (String, Option<String>) {
        for gid in self.running_order.iter().rev() {
            if let Some(p) = profiles
                .iter()
                .find(|p| !p.is_default && p.game_ids.iter().any(|g| g.eq_ignore_ascii_case(gid)))
            {
                return (p.id.clone(), Some(gid.clone()));
            }
        }
        if let Some((pid, gid, until)) = &self.grace_hold
            && now_ms < *until
        {
            return (pid.clone(), Some(gid.clone()));
        }
        if let Some(exe) = self.foreground_exe.as_deref()
            && let Some(p) = profiles.iter().find(|p| {
                !p.is_default
                    && p.app_executables
                        .iter()
                        .filter_map(|e| normalize_exe(e))
                        .any(|e| e == exe)
            })
        {
            return (p.id.clone(), None);
        }
        let fallback = profiles
            .iter()
            .find(|p| p.is_default)
            .or_else(|| profiles.first())
            .map(|p| p.id.clone())
            .unwrap_or_else(|| "desktop".to_string());
        (fallback, None)
    }

    fn try_reserve_hw_write(&mut self, now_ms: u64) -> bool {
        while let Some(&ts) = self.hw_write_window.front() {
            if now_ms.saturating_sub(ts) > 60_000 {
                self.hw_write_window.pop_front();
            } else {
                break;
            }
        }
        let in_cooldown = self
            .last_hw_write_ms
            .is_some_and(|last| now_ms.saturating_sub(last) < self.hardware_cooldown_ms);
        if in_cooldown || self.hw_write_window.len() >= MAX_HARDWARE_WRITES_PER_MINUTE {
            self.suppressed_hardware_writes += 1;
            return false;
        }
        self.last_hw_write_ms = Some(now_ms);
        self.hw_write_window.push_back(now_ms);
        self.hardware_writes += 1;
        true
    }
}

pub fn onboard_hash(profile: &MacroProfile) -> Option<u64> {
    let mut onboard: Vec<&crate::model::MacroDef> = profile
        .bindings
        .iter()
        .filter(|b| b.enabled && b.delivery == MacroDelivery::OnboardHardware)
        .collect();
    if onboard.is_empty() {
        return None;
    }
    onboard.sort_by(|a, b| a.id.cmp(&b.id));
    let bytes = serde_json::to_vec(&onboard).ok()?;
    let mut h: u64 = 0xcbf29ce484222325;
    for b in bytes {
        h ^= u64::from(b);
        h = h.wrapping_mul(0x100000001b3);
    }
    Some(h)
}

fn normalize_exe(raw: &str) -> Option<String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let name = trimmed.rsplit(['\\', '/']).next().unwrap_or(trimmed);
    if name.is_empty() {
        None
    } else {
        Some(name.to_ascii_lowercase())
    }
}
