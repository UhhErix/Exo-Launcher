use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::policy::{
    AntiCheatClass, HookDecision, evaluate_legacy_hook_policy, parse_anti_cheat_class,
};

#[derive(Debug, Clone, Deserialize)]
pub struct HostRequest {
    #[serde(default)]
    pub id: i64,
    #[serde(default)]
    pub method: String,
    #[serde(default)]
    pub params: Value,
}

#[derive(Debug, Clone, Serialize)]
pub struct CaptureConfig {
    pub quality: u32,
    pub replay_seconds: u32,
    pub replay_max_bytes: u64,
    pub max_fps: u32,
    pub replay_mode: String,
    pub cursor: bool,
    pub folder: String,
    pub call_voices: bool,
    pub mic: String,
    pub clean_clips: bool,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        let base = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".to_string());
        Self {
            quality: 50,
            replay_seconds: 60,
            replay_max_bytes: 512 * 1024 * 1024,
            max_fps: 60,
            replay_mode: "games".to_string(),
            cursor: true,
            folder: format!("{base}\\Videos\\Exo Captures"),
            call_voices: false,
            mic: String::new(),
            clean_clips: false,
        }
    }
}

impl CaptureConfig {
    pub fn apply_json(&mut self, p: &Value) {
        if let Some(q) = p.get("quality").and_then(Value::as_u64) {
            self.quality = (q as u32).clamp(0, 100);
        }
        if let Some(s) = p.get("replaySeconds").and_then(Value::as_u64) {
            self.replay_seconds = (s as u32).clamp(5, 600);
        }
        if let Some(b) = p.get("replayMaxBytes").and_then(Value::as_u64) {
            self.replay_max_bytes = b.clamp(64 << 20, 8 << 30);
        }
        if let Some(fps) = p.get("maxFps").and_then(Value::as_u64) {
            self.max_fps = if fps >= 120 { 120 } else { 60 };
        }
        if let Some(mode) = p.get("replayMode").and_then(Value::as_str)
            && matches!(mode, "games" | "always" | "off")
        {
            self.replay_mode = mode.to_string();
        }
        if let Some(c) = p.get("cursor").and_then(Value::as_bool) {
            self.cursor = c;
        }
        if let Some(f) = p.get("folder").and_then(Value::as_str)
            && !f.is_empty()
        {
            self.folder = f.to_string();
        }
        if let Some(cv) = p.get("callVoices").and_then(Value::as_bool) {
            self.call_voices = cv;
        }
        if let Some(mic) = p.get("mic").and_then(Value::as_str) {
            self.mic = mic.to_string();
        }
        if let Some(cc) = p.get("cleanClips").and_then(Value::as_bool) {
            self.clean_clips = cc;
        }
    }

    pub fn target_bitrate_bps(&self) -> u32 {
        let min_bps = 6_000_000u64;
        let max_bps = 35_000_000u64;
        (min_bps + ((max_bps - min_bps) * (self.quality as u64)) / 100) as u32
    }
}

#[derive(Debug, Clone)]
pub struct GameTarget {
    pub id: String,
    pub title: String,
    pub folder: String,
    pub pid: Option<u32>,
    pub anti_cheat_class: AntiCheatClass,
    pub hook_requested: bool,
}

impl GameTarget {
    pub fn from_json(p: &Value) -> Option<Self> {
        let id = p.get("id").and_then(Value::as_str)?.trim();
        if id.is_empty() {
            return None;
        }
        let title = p
            .get("title")
            .and_then(Value::as_str)
            .unwrap_or(id)
            .to_string();
        let folder = p
            .get("folder")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string();
        let pid = p.get("pid").and_then(Value::as_u64).map(|v| v as u32);
        let anti_cheat_class = p
            .get("antiCheatClass")
            .and_then(Value::as_str)
            .map(parse_anti_cheat_class)
            .unwrap_or(AntiCheatClass::Unknown);
        let hook_requested = p.get("hook").and_then(Value::as_bool).unwrap_or(false);
        Some(Self {
            id: id.to_string(),
            title,
            folder,
            pid,
            anti_cheat_class,
            hook_requested,
        })
    }

    pub fn hook_decision(&self) -> HookDecision {
        evaluate_legacy_hook_policy(self.anti_cheat_class, self.hook_requested)
    }
}

pub fn format_event(name: &str, data: Value) -> String {
    json!({ "event": name, "data": data }).to_string()
}

pub fn format_reply(id: i64, result: Value) -> String {
    json!({ "id": id, "result": result }).to_string()
}

pub fn format_error(id: i64, error: &str) -> String {
    json!({ "id": id, "error": error }).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn game_target_parses_anticheat_and_blocks_competitive_hook() {
        let val = json!({
            "id": "valorant",
            "title": "VALORANT",
            "folder": "C:\\Riot Games\\VALORANT",
            "antiCheatClass": "CompetitivePvpOnline",
            "hook": true
        });
        let target = GameTarget::from_json(&val).expect("parsed");
        let decision = target.hook_decision();
        assert!(!decision.allowed);
        assert_eq!(decision.reason, "blocked_competitive_pvp_anticheat");

        let sp = json!({
            "id": "hades",
            "title": "Hades",
            "antiCheatClass": "NoAntiCheat",
            "hook": true
        });
        assert!(GameTarget::from_json(&sp).unwrap().hook_decision().allowed);
    }

    #[test]
    fn ndjson_reply_and_event_envelopes_match_capture_addon_wire_format() {
        let evt = format_event("ready", json!({ "replayMemoryBudget": true }));
        let v: Value = serde_json::from_str(&evt).unwrap();
        assert_eq!(v["event"], "ready");
        assert_eq!(v["data"]["replayMemoryBudget"], true);

        let rep = format_reply(7, json!({ "buffering": true }));
        let rv: Value = serde_json::from_str(&rep).unwrap();
        assert_eq!(rv["id"], 7);
        assert_eq!(rv["result"]["buffering"], true);

        let err = format_error(8, "Nothing to save yet");
        let ev: Value = serde_json::from_str(&err).unwrap();
        assert_eq!(ev["id"], 8);
        assert_eq!(ev["error"], "Nothing to save yet");
    }
}
