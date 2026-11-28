use exo_kit::{FG, RADIUS_CARD, amoled_card, bad, fg_muted, type_body, type_small};
use rune::{El, col, row, text};
use serde_json::Value;

use crate::settings::set_primary_btn;
use crate::state::AppState;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct MissingRuntime {
    pub id: String,
    pub name: String,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct SetupRuntimes {
    pub missing: Vec<MissingRuntime>,
    pub busy: bool,
    pub note: Option<(String, bool)>,
}

#[derive(Clone, Debug, Default, PartialEq)]
struct InstallOutcome {
    failures: Vec<String>,
    restart: bool,
    stopped: bool,
}

fn missing_installable(reply: &Value) -> Vec<MissingRuntime> {
    reply
        .get("items")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|item| {
            let present = item
                .get("status")
                .and_then(Value::as_str)
                .is_some_and(|st| st.eq_ignore_ascii_case("present"));
            let offered = item
                .get("canOfferInstall")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            !present && offered
        })
        .filter_map(|item| {
            let id = item.get("id").and_then(Value::as_str)?;
            let name = item.get("name").and_then(Value::as_str).unwrap_or(id);
            Some(MissingRuntime {
                id: id.to_string(),
                name: name.to_string(),
            })
        })
        .collect()
}

fn install_outcome(reply: &Value) -> InstallOutcome {
    let flag = |key: &str| reply.get(key).and_then(Value::as_bool).unwrap_or(false);
    let results: Vec<&Value> = reply
        .get("results")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .collect();
    let outcome_is =
        |item: &Value, want: &str| item.get("outcome").and_then(Value::as_str) == Some(want);
    let stopped = flag("canceled") || flag("declined");
    let mut failures: Vec<String> = results
        .iter()
        .filter(|item| outcome_is(item, "failed"))
        .map(|item| {
            let word = |key: &str| item.get(key).and_then(Value::as_str).unwrap_or("");
            format!("{}: {}", word("name"), word("message"))
        })
        .collect();
    if !flag("ok")
        && failures.is_empty()
        && !stopped
        && let Some(message) = reply
            .get("message")
            .and_then(Value::as_str)
            .filter(|m| !m.is_empty())
    {
        failures.push(message.to_string());
    }
    InstallOutcome {
        failures,
        restart: results.iter().any(|item| outcome_is(item, "restart")),
        stopped,
    }
}

fn install_note(outcome: &InstallOutcome, still_missing: usize) -> Option<(String, bool)> {
    if !outcome.failures.is_empty() {
        return Some((outcome.failures.join(" "), true));
    }
    if outcome.stopped {
        return None;
    }
    if still_missing > 0 {
        return Some((
            "Windows doesn’t list them all yet. Restart Windows, then check again.".into(),
            false,
        ));
    }
    Some((
        if outcome.restart {
            "Runtimes installed. Restart Windows to finish.".into()
        } else {
            "Runtimes installed.".into()
        },
        false,
    ))
}

impl AppState {
    pub(crate) fn setup_load_runtimes(&mut self) {
        #[cfg(windows)]
        self.call_host_async(
            "deps.list",
            serde_json::json!({}),
            |s, res: Result<Value, String>| {
                if let Ok(reply) = res {
                    s.setup.runtimes.missing = missing_installable(&reply);
                }
            },
        );
    }

    pub(crate) fn setup_install_runtimes(&mut self) {
        if self.setup.runtimes.busy || self.setup.runtimes.missing.is_empty() {
            return;
        }
        self.setup.runtimes.busy = true;
        self.setup.runtimes.note = None;
        #[cfg(windows)]
        {
            let ids: Vec<String> = self
                .setup
                .runtimes
                .missing
                .iter()
                .map(|item| item.id.clone())
                .collect();
            self.call_host_async(
                "runtimes.install",
                serde_json::json!({ "mode": "missing", "ids": ids }),
                |s, res: Result<Value, String>| match res {
                    Ok(reply) => s.setup_runtimes_installed(install_outcome(&reply)),
                    Err(_) => {
                        s.setup.runtimes.busy = false;
                        s.setup.runtimes.note = Some((
                            "The runtimes couldn’t be installed. Try again.".into(),
                            true,
                        ));
                    }
                },
            );
        }
    }

    #[cfg_attr(not(windows), allow(dead_code))]
    fn setup_runtimes_installed(&mut self, outcome: InstallOutcome) {
        #[cfg(windows)]
        self.call_host_async(
            "deps.list",
            serde_json::json!({}),
            move |s, res: Result<Value, String>| {
                if let Ok(reply) = res {
                    s.setup.runtimes.missing = missing_installable(&reply);
                }
                s.setup_runtimes_settle(&outcome);
            },
        );
        #[cfg(not(windows))]
        self.setup_runtimes_settle(&outcome);
    }

    fn setup_runtimes_settle(&mut self, outcome: &InstallOutcome) {
        self.setup.runtimes.busy = false;
        self.setup.runtimes.note = install_note(outcome, self.setup.runtimes.missing.len());
    }
}

pub(crate) fn card_view(s: &AppState) -> Option<El> {
    let state = &s.setup.runtimes;
    if state.missing.is_empty() && state.note.is_none() {
        return None;
    }
    let mut card = amoled_card(RADIUS_CARD)
        .key("setup-runtimes-card")
        .w_full()
        .padding(18.0)
        .gap(10.0);
    let mut head = row().w_full().gap(10.0).justify_between().center_y();
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(2.0)
        .child(text("Game runtimes").font(type_body()).color(FG));
    if !state.missing.is_empty() {
        let names = state
            .missing
            .iter()
            .map(|item| item.name.as_str())
            .collect::<Vec<_>>()
            .join(", ");
        left = left.child(
            text(format!("{} missing: {names}", state.missing.len()))
                .key("setup-runtimes-missing")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }
    head = head.child(left);
    if !state.missing.is_empty() {
        let mut install = set_primary_btn(
            "setup-runtimes-install",
            if state.busy {
                "Installing…"
            } else {
                "Install"
            },
            None,
        )
        .disabled(state.busy);
        if !state.busy {
            install = install.on_press(|s: &mut AppState| s.setup_install_runtimes());
        }
        head = head.child(install);
    }
    card = card.child(head);
    if let Some((note, is_error)) = state.note.as_ref() {
        card = card.child(
            text(note.clone())
                .key("setup-runtimes-note")
                .role(if *is_error { "alert" } else { "status" })
                .font(type_small())
                .color(if *is_error { bad() } else { fg_muted() })
                .wrap(),
        );
    }
    Some(card)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn only_missing_runtimes_the_host_offers_to_install_are_listed() {
        let reply = json!({ "items": [
            { "id": "vc2022", "name": "Visual C++ 2015-2022", "status": "missing", "canOfferInstall": true },
            { "id": "dx9", "name": "DirectX 9", "status": "Present", "canOfferInstall": true },
            { "id": "net35", "name": ".NET 3.5", "status": "missing", "canOfferInstall": false },
        ] });
        assert_eq!(
            missing_installable(&reply),
            vec![MissingRuntime {
                id: "vc2022".into(),
                name: "Visual C++ 2015-2022".into()
            }]
        );
        assert!(missing_installable(&json!({ "items": null })).is_empty());
    }

    #[test]
    fn install_note_reports_failures_then_restart_then_success() {
        let failed = install_outcome(&json!({ "ok": false, "results": [
            { "name": "DirectX 9", "outcome": "failed", "message": "Download failed." },
        ] }));
        assert_eq!(
            install_note(&failed, 1),
            Some(("DirectX 9: Download failed.".into(), true))
        );
        let whole = install_outcome(&json!({ "ok": false, "message": "No connection." }));
        assert_eq!(
            install_note(&whole, 1),
            Some(("No connection.".into(), true))
        );
        let restart = install_outcome(&json!({ "ok": true, "results": [
            { "name": "VC", "outcome": "restart", "message": "" },
        ] }));
        assert_eq!(
            install_note(&restart, 0),
            Some((
                "Runtimes installed. Restart Windows to finish.".into(),
                false
            ))
        );
        let done = install_outcome(&json!({ "ok": true, "results": [] }));
        assert_eq!(
            install_note(&done, 0),
            Some(("Runtimes installed.".into(), false))
        );
        assert_eq!(install_note(&done, 2).map(|n| n.1), Some(false));
    }

    #[test]
    fn a_declined_windows_prompt_leaves_no_note() {
        let declined =
            install_outcome(&json!({ "ok": false, "declined": true, "message": "Declined." }));
        assert_eq!(install_note(&declined, 2), None);
    }

    #[test]
    fn card_is_hidden_until_something_is_missing_and_install_guards_against_a_second_press() {
        let mut s = AppState::default();
        assert!(card_view(&s).is_none());
        s.setup_install_runtimes();
        assert!(!s.setup.runtimes.busy);
        s.setup.runtimes.missing = vec![MissingRuntime {
            id: "vc2022".into(),
            name: "Visual C++".into(),
        }];
        assert!(card_view(&s).is_some());
        s.setup_install_runtimes();
        assert!(s.setup.runtimes.busy);
        s.setup.runtimes.note = Some(("stale".into(), true));
        s.setup_install_runtimes();
        assert!(s.setup.runtimes.note.is_some());
        s.setup_runtimes_settle(&InstallOutcome::default());
        assert!(!s.setup.runtimes.busy);
        s.setup.runtimes.missing.clear();
        s.setup_runtimes_settle(&InstallOutcome::default());
        assert_eq!(
            s.setup.runtimes.note,
            Some(("Runtimes installed.".into(), false))
        );
        assert!(card_view(&s).is_some());
    }

    #[test]
    fn accounts_stage_shows_the_install_button_for_missing_runtimes() {
        let mut s = AppState::default();
        s.setup = crate::setup::SetupFlow::first_run();
        s.setup.stage = 1;
        s.setup.runtimes.missing = vec![MissingRuntime {
            id: "vc2022".into(),
            name: "Visual C++".into(),
        }];
        let mut rt = rune::Runtime::new(
            s,
            crate::shell::view,
            (1920.0, 1080.0),
            rune::Env::new(true, 1.0),
        );
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("setup-runtimes-install").is_some());
        assert!(rt.find("setup-runtimes-note").is_none());
    }

    #[test]
    #[ignore = "writes a real GPU proof PNG to EXO_PROOF_DIR when requested"]
    fn render_setup_runtimes_proof() {
        use rune::render::{Gpu, OFFSCREEN_FORMAT, Offscreen, Renderer};
        let out_dir = std::path::PathBuf::from(
            std::env::var("EXO_PROOF_DIR")
                .unwrap_or_else(|_| std::env::temp_dir().to_string_lossy().into_owned()),
        );
        std::fs::create_dir_all(&out_dir).expect("create EXO_PROOF_DIR");
        let gpu = Gpu::headless();
        let mut renderer = Renderer::new(&gpu.device, OFFSCREEN_FORMAT);
        let (w, h) = (1600u32, 960u32);
        let mut capture = |name: &str, runtimes: SetupRuntimes| {
            let mut s = AppState::default();
            s.setup = crate::setup::SetupFlow::first_run();
            s.setup.stage = 1;
            s.setup.runtimes = runtimes;
            let mut rt = rune::Runtime::new(
                s,
                crate::shell::view,
                (w as f32, h as f32),
                rune::Env::new(true, 1.0),
            );
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.set_glass_policy(rune::GlassPolicy::Glass);
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            rt.settle_all();
            rt.frame(2.0 / 60.0);
            renderer.set_srgb_blend(rt.srgb_blend());
            renderer.set_fade_stops(rt.fade_stops());
            renderer.set_max_backdrop_blurs(rt.max_backdrop_blurs());
            renderer.set_liquid_glass(rt.liquid_glass_frame());
            renderer.prepare(
                &gpu.device,
                &gpu.queue,
                &mut rt.text.atlas,
                &rt.instances,
                (w, h),
            );
            let target = Offscreen::with_format(&gpu.device, w, h, renderer.format());
            let mut enc = gpu.device.create_command_encoder(&Default::default());
            renderer.draw(&mut enc, &target.view, rt.clear_color());
            target.copy(&mut enc);
            gpu.queue.submit([enc.finish()]);
            let pixels = target.read(&gpu.device);
            image::save_buffer(out_dir.join(name), &pixels, w, h, image::ColorType::Rgba8)
                .expect("save proof png");
        };
        let missing = vec![
            MissingRuntime {
                id: "vcredist-x64".into(),
                name: "Visual C++ 2015-2022 (x64)".into(),
            },
            MissingRuntime {
                id: "directx-june2010".into(),
                name: "DirectX (June 2010)".into(),
            },
        ];
        capture(
            "proof-setup-runtimes-missing.png",
            SetupRuntimes {
                missing: missing.clone(),
                busy: false,
                note: None,
            },
        );
        capture(
            "proof-setup-runtimes-installing.png",
            SetupRuntimes {
                missing: missing.clone(),
                busy: true,
                note: None,
            },
        );
        capture(
            "proof-setup-runtimes-failed.png",
            SetupRuntimes {
                missing,
                busy: false,
                note: Some(("DirectX (June 2010): Download failed.".into(), true)),
            },
        );
        capture(
            "proof-setup-runtimes-done.png",
            SetupRuntimes {
                missing: Vec::new(),
                busy: false,
                note: Some(("Runtimes installed.".into(), false)),
            },
        );
    }
}
