use exo_macros::model::{MacroAction, MacroDelivery};
use exo_macros::scheduler::MacroOutputSink;
use std::collections::HashSet;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

pub struct GatedOutputSink {
    ac_paused: Arc<AtomicBool>,
    dry_run: bool,
    emitted: AtomicU64,
    dropped_by_ac: AtomicU64,
    held_scancodes: HashSet<u16>,
    held_buttons: HashSet<String>,
}

impl GatedOutputSink {
    pub fn new(ac_paused: Arc<AtomicBool>, dry_run: bool) -> Self {
        Self {
            ac_paused,
            dry_run,
            emitted: AtomicU64::new(0),
            dropped_by_ac: AtomicU64::new(0),
            held_scancodes: HashSet::new(),
            held_buttons: HashSet::new(),
        }
    }

    pub fn emitted_count(&self) -> u64 {
        self.emitted.load(Ordering::Acquire)
    }

    pub fn dropped_by_ac_count(&self) -> u64 {
        self.dropped_by_ac.load(Ordering::Acquire)
    }

    pub fn release_all_held(&mut self) -> usize {
        let mut count = 0;
        let keys: Vec<u16> = self.held_scancodes.drain().collect();
        for sc in keys {
            if !self.dry_run {
                dispatch_key_scancode(sc, false);
            }
            count += 1;
        }
        let buttons: Vec<String> = self.held_buttons.drain().collect();
        for button in buttons {
            if !self.dry_run {
                dispatch_mouse_button(&button, false);
            }
            count += 1;
        }
        count
    }
}

impl Drop for GatedOutputSink {
    fn drop(&mut self) {
        let _ = self.release_all_held();
    }
}

impl MacroOutputSink for GatedOutputSink {
    fn emit(&mut self, action: &MacroAction, delivery: MacroDelivery) {
        if delivery == MacroDelivery::SoftwareSidecar && self.ac_paused.load(Ordering::Acquire) {
            self.dropped_by_ac.fetch_add(1, Ordering::AcqRel);
            return;
        }

        match action {
            MacroAction::KeyDown { code } => {
                let sc = scancode_for(code);
                self.held_scancodes.insert(sc);
                if !self.dry_run {
                    dispatch_key_scancode(sc, true);
                }
            }
            MacroAction::KeyUp { code } => {
                let sc = scancode_for(code);
                self.held_scancodes.remove(&sc);
                if !self.dry_run {
                    dispatch_key_scancode(sc, false);
                }
            }
            MacroAction::MouseDown { button } => {
                self.held_buttons.insert(button.clone());
                if !self.dry_run {
                    dispatch_mouse_button(button, true);
                }
            }
            MacroAction::MouseUp { button } => {
                self.held_buttons.remove(button);
                if !self.dry_run {
                    dispatch_mouse_button(button, false);
                }
            }
            _ => {}
        }
        self.emitted.fetch_add(1, Ordering::AcqRel);
    }
}

pub fn scancode_for(code: &str) -> u16 {
    match code.to_ascii_uppercase().as_str() {
        "ESCAPE" | "ESC" => 0x01,
        "E" => 0x12,
        "A" => 0x1E,
        "SHIFTLEFT" | "LSHIFT" => 0x2A,
        "SPACE" => 0x39,
        "F10" => 0x44,
        "F13" => 0x64,
        "F14" => 0x65,
        "F15" => 0x66,
        _ => 0x64,
    }
}

#[cfg(windows)]
fn dispatch_key_scancode(scancode: u16, down: bool) {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, SendInput,
    };

    let flags = if down {
        KEYEVENTF_SCANCODE
    } else {
        KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP
    };
    let input = INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: 0,
                wScan: scancode,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    unsafe {
        let _ = SendInput(1, &raw const input, size_of::<INPUT>() as i32);
    }
}

#[cfg(not(windows))]
fn dispatch_key_scancode(_scancode: u16, _down: bool) {}

#[cfg(windows)]
fn dispatch_mouse_button(button: &str, down: bool) {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP,
        MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, MOUSEINPUT, SendInput,
    };

    let flags = match (button.to_ascii_lowercase().as_str(), down) {
        ("left", true) => MOUSEEVENTF_LEFTDOWN,
        ("left", false) => MOUSEEVENTF_LEFTUP,
        ("right", true) => MOUSEEVENTF_RIGHTDOWN,
        ("right", false) => MOUSEEVENTF_RIGHTUP,
        _ => return,
    };
    let input = INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx: 0,
                dy: 0,
                mouseData: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    unsafe {
        let _ = SendInput(1, &raw const input, size_of::<INPUT>() as i32);
    }
}

#[cfg(not(windows))]
fn dispatch_mouse_button(_button: &str, _down: bool) {}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_macros::policy::{AntiCheatClass, GameAntiCheatInput, evaluate_game_policy};

    #[test]
    fn drops_100_percent_of_software_events_while_ac_paused() {
        let ac_paused = Arc::new(AtomicBool::new(true));
        let mut sink = GatedOutputSink::new(Arc::clone(&ac_paused), true);

        for ac_class in [
            AntiCheatClass::CompetitivePvp,
            AntiCheatClass::OnlinePveGacha,
            AntiCheatClass::Unknown,
        ] {
            let p = evaluate_game_policy(&GameAntiCheatInput {
                game_id: "g".into(),
                title: "Protected".into(),
                class: ac_class,
                anti_cheat_names: vec!["Vanguard".into()],
                offline_note_acknowledged: true,
            });
            ac_paused.store(!p.software_macros_allowed, Ordering::Release);
            sink.emit(
                &MacroAction::KeyDown { code: "E".into() },
                MacroDelivery::SoftwareSidecar,
            );
        }

        assert_eq!(sink.emitted_count(), 0);
        assert_eq!(sink.dropped_by_ac_count(), 3);

        ac_paused.store(false, Ordering::Release);
        sink.emit(
            &MacroAction::KeyDown { code: "E".into() },
            MacroDelivery::SoftwareSidecar,
        );
        assert_eq!(sink.emitted_count(), 1);
        assert_eq!(sink.release_all_held(), 1);
    }
}
