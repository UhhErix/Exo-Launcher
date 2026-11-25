use crate::notice::{NoticeActionKind, NoticeActionSpec, RichNoticeSpec};
use crate::state::{AppState, HostStatus, NoticeTone};
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_MOVE,
    SPRING_QUICK, amoled_card, bad, fg_faint, fg_muted, glass_control_fill, glass_rim, good,
    hairline, hover_fill, icon_chevron_down, icon_chevron_left, icon_heart, icon_search,
    primary_button, scaled, slider_track, status_badge, track_fill, type_body, type_control,
    type_section, type_small, type_title,
};
use exo_rpc::{
    ActionResult, AudioFormatOption, AudioFormatsReply, AudioReadReply, DevicesImageReply,
    EqHeadphone, EqHeadphonesReply, EqStateReply, METHOD_AUDIO_FORMATS, METHOD_AUDIO_READ,
    METHOD_AUDIO_SET_FORMAT, METHOD_AUDIO_SET_MUTE, METHOD_AUDIO_SET_VOLUME, METHOD_DEVICES_IMAGE,
    METHOD_EQ, METHOD_EQ_HEADPHONES, PeripheralCard, PeripheralPort,
};
use rune::{
    Color, Cursor, El, Key, Phase, ScrollSpec, SurfaceFit, TextEdit, TextStyle, col, edit_access,
    el, row, text, text_input,
};
use serde_json::{Map, Value, json};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Wearing {
    pub name: String,
    pub look: String,
    pub id: Option<String>,
}

impl Wearing {
    pub fn from_eq_headphone(hp: &EqHeadphone) -> Self {
        Self {
            name: hp.name.clone(),
            look: if hp.form == "over-ear" {
                "headphones".into()
            } else {
                "iem".into()
            },
            id: Some(hp.id.clone()),
        }
    }
}

pub const SYSTEM_EQ_DRM_DISCLOSURE: &str = "Some protected audio playback (DRM streams) may not play while system-wide EQ is on. You can turn it off any time.";
pub const SYSTEM_EQ_REENABLE_NOTICE_KEY: &str = "exo-eq-reenable";

#[derive(Debug, Clone, Default)]
pub struct AudioEqUiState {
    pub eq: Option<EqStateReply>,
    pub eq_load_error: Option<String>,
    pub eq_busy_row: Option<String>,
    pub eq_row_error: Option<(String, String)>,
    pub eq_seq: u64,
    pub system_eq_disclosure_open: bool,
    pub launch_eq_health_checked: bool,
    pub headphone_picker_open: bool,
    pub headphone_query: TextEdit,
    pub headphones_list: Option<Vec<EqHeadphone>>,
    pub headphones_loading: bool,
    pub headphones_failed: bool,
    pub wearing: Option<Wearing>,
    pub output_formats: Option<AudioFormatsReply>,
    pub mic_formats: Option<AudioFormatsReply>,
    pub open_picker: Option<String>,
    pub output_volume_error: Option<String>,
    pub output_mute_error: Option<String>,
    pub output_format_error: Option<String>,
    pub mic_volume_error: Option<String>,
    pub mic_mute_error: Option<String>,
    pub mic_format_error: Option<String>,
    pub dragging_output_vol: Option<i32>,
    pub dragging_mic_vol: Option<i32>,
    pub audio_change_gen: u64,
    pub last_audio_poll: f64,
    pub last_eq_poll: f64,
}

impl AudioEqUiState {
    pub fn demo() -> Self {
        let hp = EqHeadphone {
            id: "sennheiser-hd-600".into(),
            name: "Sennheiser HD 600".into(),
            source: "oratory1990".into(),
            form: "over-ear".into(),
        };
        Self {
            wearing: Some(Wearing::from_eq_headphone(&hp)),
            eq: Some(EqStateReply {
                ok: true,
                message: None,
                apo: exo_rpc::EqApoState {
                    installed: true,
                    installing: false,
                    version: Some("1.4.2".into()),
                    on_output: true,
                    restart: false,
                    effects_off: false,
                    outputs: 1,
                    installer_version: "1.4.2".into(),
                    native_available: true,
                    system_eq_enabled: false,
                    drm_disclosure_accepted: false,
                    needs_reenable: false,
                    reenable_reason: None,
                    protected_audio_dg_disabled: false,
                },
                on: true,
                comparing: false,
                output: Some(exo_rpc::EqOutputInfo {
                    name: Some("Headphones".into()),
                }),
                headphone: Some(hp),
                sound: "flat".into(),
                custom: Vec::new(),
                preamp: -4.2,
                playing: None,
                games: Vec::new(),
                library: Vec::new(),
                night: exo_rpc::EqNightState {
                    on: false,
                    start: "22:00".into(),
                    end: "07:00".into(),
                    sync: true,
                    now: false,
                },
                others: Vec::new(),
                guess: None,
                lucid_plays: false,
            }),
            output_formats: Some(AudioFormatsReply {
                ok: true,
                current: Some("2-48000-24".into()),
                formats: vec![
                    AudioFormatOption {
                        key: "2-48000-24".into(),
                        rate: 48_000,
                        bits: 24,
                        container: 32,
                        channels: 2,
                    },
                    AudioFormatOption {
                        key: "2-96000-24".into(),
                        rate: 96_000,
                        bits: 24,
                        container: 32,
                        channels: 2,
                    },
                ],
                message: None,
            }),
            ..Self::default()
        }
    }
}

pub fn port_words(port: &PeripheralPort) -> String {
    let hubs = if port.hubs == 1 {
        "a hub".to_string()
    } else {
        format!("{} hubs", port.hubs)
    };
    match port.r#where.as_deref() {
        Some("cpu") => {
            if port.hubs > 0 {
                format!("Processor's own USB, through {hubs}")
            } else {
                "Processor's own USB".into()
            }
        }
        Some("chipset") => {
            if port.hubs > 0 {
                format!("Through {hubs} and the chipset")
            } else {
                "Through the chipset".into()
            }
        }
        Some("addin") => {
            if port.hubs > 0 {
                format!("Through {hubs} and an extra USB chip")
            } else {
                "On an extra USB chip".into()
            }
        }
        Some("dock") => "Through a dock".into(),
        Some("bluetooth") => "Bluetooth".into(),
        _ => "Unknown".into(),
    }
}

pub fn port_short(port: &PeripheralPort) -> Option<String> {
    let via = if port.hubs > 0 { " via hub" } else { "" };
    match port.r#where.as_deref() {
        Some("cpu") => Some(format!("CPU USB{via}")),
        Some("chipset") => Some(format!("Chipset USB{via}")),
        Some("addin") => Some(format!("Add-in USB{via}")),
        Some("dock") => Some("Dock".into()),
        _ => None,
    }
}

pub fn sound_label(sound: &str) -> &'static str {
    match sound {
        "bass" => "Bass",
        "vocal" => "Vocal",
        "footsteps" => "Footsteps",
        "cinematic" => "Immersion",
        "custom" => "Custom",
        _ => "Reference",
    }
}

pub fn noise_label(noise: &str) -> String {
    match noise {
        "exo" => "Exo Voice".into(),
        "standard" => "Standard".into(),
        "off" => "Off".into(),
        other => other.to_string(),
    }
}

pub fn half_hour_slots() -> Vec<(String, String)> {
    (0..48)
        .map(|i| {
            let hour = i / 2;
            let minute = if i % 2 == 1 { 30 } else { 0 };
            let value = format!("{hour:02}:{minute:02}");
            let h12 = if hour % 12 == 0 { 12 } else { hour % 12 };
            let ampm = if hour < 12 { "AM" } else { "PM" };
            let label = format!("{h12}:{minute:02} {ampm}");
            (value, label)
        })
        .collect()
}

pub fn format_time_slot(val: &str) -> String {
    let mut parts = val.split(':');
    if let (Some(h), Some(m)) = (parts.next(), parts.next())
        && let (Ok(hour), Ok(minute)) = (h.parse::<u32>(), m.parse::<u32>())
    {
        let h12 = if hour % 12 == 0 { 12 } else { hour % 12 };
        let ampm = if hour < 12 { "AM" } else { "PM" };
        return format!("{h12}:{minute:02} {ampm}");
    }
    val.to_string()
}

pub fn filter_headphones<'a>(list: &'a [EqHeadphone], query: &str) -> Vec<&'a EqHeadphone> {
    let words: Vec<String> = query
        .split_whitespace()
        .map(|w| w.to_lowercase())
        .filter(|w| !w.is_empty())
        .collect();
    if words.is_empty() {
        return Vec::new();
    }
    list.iter()
        .filter(|hp| {
            let lower = hp.name.to_lowercase();
            words.iter().all(|w| lower.contains(w))
        })
        .take(6)
        .collect()
}

pub fn device_switch(
    key: impl Into<String>,
    label: impl Into<String>,
    on: bool,
    disabled: bool,
) -> El {
    let knob = el()
        .size(20.0, 20.0)
        .radius(10.0)
        .fill(FG)
        .shadow(1.0, 3.0, BG.alpha(0.35))
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_MOVE);

    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .disabled(disabled)
        .focusable(!disabled)
        .hit()
        .cursor(if disabled {
            Cursor::Default
        } else {
            Cursor::Pointer
        })
        .press_on_down()
        .size(40.0, 24.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
        .motion(SPRING_QUICK)
        .child(knob)
}

pub fn device_icon_btn(
    key: impl Into<String>,
    label: impl Into<String>,
    icon: Option<El>,
    disabled: bool,
) -> El {
    let text_el = text(label)
        .font(type_control())
        .color(if disabled { fg_faint() } else { FG });
    row()
        .key(key)
        .role("button")
        .disabled(disabled)
        .focusable(!disabled)
        .hit()
        .press_on_down()
        .press_scale(if disabled { 1.0 } else { PRESS_SCALE })
        .cursor(if disabled {
            Cursor::Default
        } else {
            Cursor::Pointer
        })
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .hover(if disabled {
            glass_control_fill()
        } else {
            hover_fill()
        })
        .motion(SPRING_QUICK)
        .children(icon)
        .child(text_el)
}

pub fn device_btn(key: impl Into<String>, label: impl Into<String>, disabled: bool) -> El {
    device_icon_btn(key, label, None, disabled)
}

pub fn device_panel(title: impl Into<String>, rows: Vec<El>) -> El {
    if rows.is_empty() {
        return el();
    }
    let mut body = col().w_full();
    for (idx, r) in rows.into_iter().enumerate() {
        if idx > 0 {
            body = body.child(el().w_full().h(1.0).fill(hairline()));
        }
        body = body.child(r);
    }
    amoled_card(RADIUS_PANEL)
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(8.0)
        .gap(10.0)
        .child(text(title).font(type_section()).color(FG))
        .child(body)
}

pub fn device_row(
    label: impl Into<String>,
    detail: Option<String>,
    error: Option<String>,
    control: Option<El>,
) -> El {
    device_row_sized(label, detail, error, control, 15.0, Some(68.0))
}

pub fn device_row_compact(
    label: impl Into<String>,
    detail: Option<String>,
    error: Option<String>,
    control: Option<El>,
) -> El {
    device_row_sized(label, detail, error, control, 10.0, None)
}

fn device_row_sized(
    label: impl Into<String>,
    detail: Option<String>,
    error: Option<String>,
    control: Option<El>,
    pad_y: f32,
    min_h: Option<f32>,
) -> El {
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(2.0)
        .child(text(label).font(type_body()).color(FG));
    if let Some(d) = detail
        && !d.is_empty()
    {
        left = left.child(text(d).font(type_small()).color(fg_muted()));
    }
    if let Some(err) = error
        && !err.is_empty()
    {
        left = left.child(text(err).font(type_small()).color(bad()));
    }
    let mut r = row()
        .w_full()
        .py(pad_y)
        .gap(12.0)
        .justify_between()
        .center_y()
        .child(left);
    if let Some(mh) = min_h {
        r = r.min_h(mh);
    }
    if let Some(c) = control {
        r = r.child(c);
    }
    r
}

pub struct DevicePageHeader {
    pub key: &'static str,
    pub name: String,
    pub status: String,
    pub media: El,
    pub stats: Vec<(&'static str, String)>,
    pub credit: Option<&'static str>,
}

pub fn device_page_shell(
    header: DevicePageHeader,
    panels: Vec<El>,
    on_back: impl Fn(&mut AppState) + 'static,
) -> El {
    let cut = panels.len().div_ceil(2);
    let mut iter = panels.into_iter();
    let col_a: Vec<El> = iter.by_ref().take(cut).collect();
    let col_b: Vec<El> = iter.collect();
    device_page_shell_inner(header, col_a, col_b, false, on_back)
}

pub fn device_page_shell_columns_no_rescan(
    header: DevicePageHeader,
    col_a: Vec<El>,
    col_b: Vec<El>,
    on_back: impl Fn(&mut AppState) + 'static,
) -> El {
    device_page_shell_inner(header, col_a, col_b, false, on_back)
}

pub fn device_page_shell_band(
    header: DevicePageHeader,
    col_a: Vec<El>,
    col_b: Vec<El>,
    on_back: impl Fn(&mut AppState) + 'static,
) -> El {
    device_page_shell_inner(header, col_a, col_b, true, on_back)
}

fn device_page_shell_inner(
    header: DevicePageHeader,
    col_a: Vec<El>,
    col_b: Vec<El>,
    stretch_band: bool,
    on_back: impl Fn(&mut AppState) + 'static,
) -> El {
    let DevicePageHeader {
        key,
        name,
        status,
        media,
        stats,
        credit,
    } = header;
    let mut hero = amoled_card(RADIUS_PANEL)
        .w(409.0)
        .h_full()
        .child(
            el().w_full()
                .grow(1.0)
                .min_h(120.0)
                .px(16.0)
                .py(16.0)
                .center()
                .child(media),
        )
        .child(
            col()
                .w_full()
                .pt(16.0)
                .px(24.0)
                .gap(4.0)
                .child(text(name).font(type_title()).color(FG))
                .child(text(status).font(type_small()).color(fg_muted())),
        );

    if !stats.is_empty() {
        let mut dl = col().w_full().mt(16.0);
        for pair in stats.chunks(2) {
            dl = dl.child(el().w_full().h(1.0).fill(hairline()));
            let mut r = row().w_full();
            for (col_idx, (k, v)) in pair.iter().enumerate() {
                if col_idx > 0 {
                    r = r.child(el().w(1.0).h_full().fill(hairline()));
                }
                r = r.child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .py(16.0)
                        .px(24.0)
                        .gap(4.0)
                        .child(text(*k).font(type_small()).color(fg_muted()))
                        .child(
                            text(v.clone())
                                .font(TextStyle::new(scaled(20.0), 400.0))
                                .color(FG)
                                .w_full()
                                .wrap(),
                        ),
                );
            }
            if pair.len() == 1 {
                r = r
                    .child(el().w(1.0).h_full().fill(hairline()))
                    .child(el().grow(1.0).min_w(0.0));
            }
            dl = dl.child(r);
        }
        hero = hero.child(dl);
    }

    if let Some(cred) = credit {
        hero = hero.child(el().w_full().h(1.0).fill(hairline())).child(
            row()
                .w_full()
                .py(10.0)
                .px(24.0)
                .gap(6.0)
                .center_y()
                .child(text(cred).font(type_small()).color(fg_faint()))
                .child(icon_heart(12.0, bad())),
        );
    }

    let stretch_last = |mut list: Vec<El>| -> Vec<El> {
        if stretch_band && let Some(last) = list.pop() {
            list.push(last.grow(1.0));
        }
        list
    };
    let col_a = stretch_last(col_a);
    let col_b = stretch_last(col_b);

    let right = if col_b.is_empty() {
        let c = col().w_full().gap(12.0).children(col_a);
        if stretch_band { c.h_full() } else { c }
    } else if stretch_band {
        row()
            .w_full()
            .h_full()
            .gap(12.0)
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .h_full()
                    .gap(12.0)
                    .children(col_a),
            )
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .h_full()
                    .gap(12.0)
                    .children(col_b),
            )
    } else {
        row()
            .w_full()
            .gap(12.0)
            .child(col().grow(1.0).min_w(0.0).gap(12.0).children(col_a))
            .child(col().grow(1.0).min_w(0.0).gap(12.0).children(col_b))
    };

    let top_strip = row().w_full().justify_between().center_y().child(
        device_icon_btn(
            "dev-back",
            "Devices",
            Some(icon_chevron_left(14.0, FG)),
            false,
        )
        .on_press(on_back),
    );

    col()
        .key(key)
        .w_full()
        .h_full()
        .fill(BG)
        .pt(66.0)
        .px(10.0)
        .pb(16.0)
        .gap(12.0)
        .child(top_strip)
        .child(
            row()
                .grow(1.0)
                .min_h(0.0)
                .w_full()
                .gap(12.0)
                .child(hero)
                .child(
                    col()
                        .key(format!("{key}-scroll"))
                        .grow(1.0)
                        .min_w(0.0)
                        .h_full()
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(right),
                ),
        )
}

impl AppState {
    pub fn open_audio_device_page(&mut self) {
        self.pc.audio_ui.open_picker = None;
        self.pc.audio_ui.headphone_picker_open = false;
        self.pc.audio_ui.output_volume_error = None;
        self.pc.audio_ui.output_mute_error = None;
        self.pc.audio_ui.output_format_error = None;
        self.pc.audio_ui.mic_volume_error = None;
        self.pc.audio_ui.mic_mute_error = None;
        self.pc.audio_ui.mic_format_error = None;
        self.refresh_audio_details();
        self.refresh_audio_formats(true);
        self.refresh_audio_formats(false);
        self.refresh_eq_state();
    }

    pub fn refresh_audio_details(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let out_id = self.pc.audio_output_id.clone();
        let mic_id = self.pc.audio_mic_id.clone();
        let began = self.pc.audio_ui.audio_change_gen;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, AudioReadReply>(
                METHOD_AUDIO_READ,
                &json!({ "output": out_id, "mic": mic_id }),
                move |res| {
                    if let Ok(aud) = res
                        && aud.ok
                    {
                        handle.update(move |s, _| {
                            if s.pc.audio_ui.audio_change_gen == began {
                                s.pc.audio = Some(aud);
                            }
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (out_id, mic_id, began);
    }

    pub fn refresh_audio_formats(&mut self, is_output: bool) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        let Some(id) = target_id else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, AudioFormatsReply>(
                METHOD_AUDIO_FORMATS,
                &json!({ "id": id }),
                move |res| {
                    if let Ok(reply) = res {
                        handle.update(move |s, _| {
                            if is_output {
                                s.pc.audio_ui.output_formats = Some(reply);
                            } else {
                                s.pc.audio_ui.mic_formats = Some(reply);
                            }
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn set_audio_format(&mut self, is_output: bool, key: String) {
        self.pc.audio_ui.open_picker = None;
        self.pc.audio_ui.audio_change_gen = self.pc.audio_ui.audio_change_gen.wrapping_add(1);
        if is_output {
            self.pc.audio_ui.output_format_error = None;
        } else {
            self.pc.audio_ui.mic_format_error = None;
        }
        let picked_info = {
            let list = if is_output {
                self.pc.audio_ui.output_formats.as_ref()
            } else {
                self.pc.audio_ui.mic_formats.as_ref()
            };
            list.and_then(|l| l.formats.iter().find(|f| f.key == key).map(|f| f.as_info()))
        };
        if self.status == HostStatus::Standalone {
            if is_output {
                if let Some(ref mut f) = self.pc.audio_ui.output_formats {
                    f.current = Some(key);
                }
                if let (Some(info), Some(aud)) = (picked_info, &mut self.pc.audio)
                    && let Some(ref mut ep) = aud.output
                {
                    ep.format = Some(info);
                }
            } else {
                if let Some(ref mut f) = self.pc.audio_ui.mic_formats {
                    f.current = Some(key);
                }
                if let (Some(info), Some(aud)) = (picked_info, &mut self.pc.audio)
                    && let Some(ref mut ep) = aud.mic
                {
                    ep.format = Some(info);
                }
            }
            return;
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        let Some(id) = target_id else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_send = key.clone();
            rpc.call::<_, ActionResult>(
                METHOD_AUDIO_SET_FORMAT,
                &json!({ "id": id, "format": key_send }),
                move |res| {
                    handle.update(move |s, _| {
                        match res {
                            Ok(r) if r.ok => {
                                if let Some(info) = picked_info
                                    && let Some(ref mut aud) = s.pc.audio
                                {
                                    let ep = if is_output {
                                        &mut aud.output
                                    } else {
                                        &mut aud.mic
                                    };
                                    if let Some(ep) = ep {
                                        ep.format = Some(info);
                                    }
                                }
                            }
                            Ok(r) => {
                                let msg = r
                                    .message
                                    .unwrap_or_else(|| "Windows didn’t take that format.".into());
                                if is_output {
                                    s.pc.audio_ui.output_format_error = Some(msg);
                                } else {
                                    s.pc.audio_ui.mic_format_error = Some(msg);
                                }
                            }
                            Err(_) => {
                                let msg = "Windows didn’t take that format.".to_string();
                                if is_output {
                                    s.pc.audio_ui.output_format_error = Some(msg);
                                } else {
                                    s.pc.audio_ui.mic_format_error = Some(msg);
                                }
                            }
                        }
                        s.refresh_audio_formats(is_output);
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, picked_info);
    }

    pub fn commit_audio_volume(&mut self, is_output: bool, volume: i32) {
        let vol = volume.clamp(0, 100);
        self.pc.audio_ui.audio_change_gen = self.pc.audio_ui.audio_change_gen.wrapping_add(1);
        let prev = self.pc.audio.as_ref().and_then(|a| {
            if is_output {
                a.output.as_ref().and_then(|o| o.volume)
            } else {
                a.mic.as_ref().and_then(|m| m.volume)
            }
        });
        if is_output {
            self.pc.audio_ui.output_volume_error = None;
            self.pc.audio_ui.dragging_output_vol = None;
        } else {
            self.pc.audio_ui.mic_volume_error = None;
            self.pc.audio_ui.dragging_mic_vol = None;
        }
        if let Some(ref mut aud) = self.pc.audio {
            if is_output {
                if let Some(ref mut o) = aud.output {
                    o.volume = Some(vol);
                }
            } else if let Some(ref mut m) = aud.mic {
                m.volume = Some(vol);
            }
        }
        if self.status == HostStatus::Standalone {
            return;
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        let Some(id) = target_id else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_AUDIO_SET_VOLUME,
                &json!({ "id": id, "volume": vol }),
                move |res| {
                    let fail = match res {
                        Ok(r) if r.ok => None,
                        Ok(r) => Some(
                            r.message
                                .unwrap_or_else(|| "Windows didn’t take it. Try again.".into()),
                        ),
                        Err(_) => Some("Windows didn’t take it. Try again.".into()),
                    };
                    if let Some(msg) = fail {
                        handle.update(move |s, _| {
                            if let Some(ref mut aud) = s.pc.audio {
                                if is_output {
                                    if let Some(ref mut o) = aud.output {
                                        o.volume = prev;
                                    }
                                    s.pc.audio_ui.output_volume_error = Some(msg);
                                } else {
                                    if let Some(ref mut m) = aud.mic {
                                        m.volume = prev;
                                    }
                                    s.pc.audio_ui.mic_volume_error = Some(msg);
                                }
                            }
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, prev);
    }

    pub fn commit_audio_mute(&mut self, is_output: bool, muted: bool) {
        self.pc.audio_ui.audio_change_gen = self.pc.audio_ui.audio_change_gen.wrapping_add(1);
        let prev = self.pc.audio.as_ref().and_then(|a| {
            if is_output {
                a.output.as_ref().and_then(|o| o.muted)
            } else {
                a.mic.as_ref().and_then(|m| m.muted)
            }
        });
        if is_output {
            self.pc.audio_ui.output_mute_error = None;
        } else {
            self.pc.audio_ui.mic_mute_error = None;
        }
        if let Some(ref mut aud) = self.pc.audio {
            if is_output {
                if let Some(ref mut o) = aud.output {
                    o.muted = Some(muted);
                }
            } else if let Some(ref mut m) = aud.mic {
                m.muted = Some(muted);
            }
        }
        if self.status == HostStatus::Standalone {
            return;
        }
        let target_id = if is_output {
            self.pc.audio_output_id.clone()
        } else {
            self.pc.audio_mic_id.clone()
        };
        let Some(id) = target_id else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_AUDIO_SET_MUTE,
                &json!({ "id": id, "muted": muted }),
                move |res| {
                    let fail = match res {
                        Ok(r) if r.ok => None,
                        Ok(r) => Some(
                            r.message
                                .unwrap_or_else(|| "Windows didn’t take it. Try again.".into()),
                        ),
                        Err(_) => Some("Windows didn’t take it. Try again.".into()),
                    };
                    if let Some(msg) = fail {
                        handle.update(move |s, _| {
                            if let Some(ref mut aud) = s.pc.audio {
                                if is_output {
                                    if let Some(ref mut o) = aud.output {
                                        o.muted = prev;
                                    }
                                    s.pc.audio_ui.output_mute_error = Some(msg);
                                } else {
                                    if let Some(ref mut m) = aud.mic {
                                        m.muted = prev;
                                    }
                                    s.pc.audio_ui.mic_mute_error = Some(msg);
                                }
                            }
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, prev);
    }

    pub fn refresh_eq_state(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let out_id = self.pc.audio_output_id.clone();
        self.pc.audio_ui.eq_load_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mut body = Map::new();
            body.insert("action".into(), Value::String("state".into()));
            if let Some(o) = out_id {
                body.insert("output".into(), Value::String(o));
            }
            let rpc_img = rpc.clone();
            rpc.call::<_, EqStateReply>(METHOD_EQ, &Value::Object(body), move |res| match res {
                Ok(eq) => {
                    let hp = eq.headphone.clone();
                    let h_img = handle.clone();
                    handle.update(move |s, _| {
                        s.apply_eq_state(eq);
                    });
                    if let Some(hp) = hp {
                        let look = if hp.form == "over-ear" {
                            "headphones"
                        } else {
                            "iem"
                        };
                        rpc_img.call::<_, DevicesImageReply>(
                            METHOD_DEVICES_IMAGE,
                            &json!({ "kind": "audio", "model": look, "name": hp.name }),
                            move |img_res| {
                                if let Ok(reply) = img_res
                                    && let Some(url) = reply.url
                                {
                                    h_img.update(move |s, _| {
                                        if let Some(oid) = s.pc.audio_output_id.clone() {
                                            s.pc.device_images.insert(oid, url.clone());
                                        }
                                        s.pc.device_images.insert("audio:worn".into(), url);
                                    });
                                }
                            },
                        );
                    }
                }
                Err(_) => {
                    handle.update(|s, _| {
                        s.pc.audio_ui.eq_load_error = Some("Exo EQ didn't load. Try again.".into());
                    });
                }
            });
        }
        #[cfg(not(windows))]
        let _ = out_id;
    }

    pub fn check_system_eq_health_on_launch(&mut self) {
        if self.pc.audio_ui.launch_eq_health_checked {
            return;
        }
        self.pc.audio_ui.launch_eq_health_checked = true;
        self.refresh_eq_state();
    }

    pub fn open_system_eq_disclosure(&mut self) {
        self.pc.audio_ui.system_eq_disclosure_open = true;
    }

    pub fn cancel_system_eq_disclosure(&mut self) {
        self.pc.audio_ui.system_eq_disclosure_open = false;
    }

    pub fn confirm_enable_system_eq(&mut self) {
        self.pc.audio_ui.system_eq_disclosure_open = false;
        self.eq_act(
            "system-eq",
            "enable-system-eq",
            json!({ "drmDisclosureAccepted": true }),
        );
    }

    pub fn apply_eq_state(&mut self, eq: EqStateReply) {
        if let Some(ref hp) = eq.headphone {
            self.pc.eq_headphone = Some(hp.clone());
            self.pc.audio_ui.wearing = Some(Wearing::from_eq_headphone(hp));
        } else {
            self.pc.eq_headphone = None;
        }
        let needs_reenable = eq.apo.system_eq_enabled && eq.apo.needs_reenable;
        let reason_text = reenable_reason_detail(eq.apo.reenable_reason.as_deref());
        self.pc.audio_ui.eq_load_error = None;
        self.pc.audio_ui.eq = Some(eq);
        if needs_reenable {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some(SYSTEM_EQ_REENABLE_NOTICE_KEY.into()),
                title: Some("System-wide EQ paused".into()),
                where_label: Some("Audio".into()),
                text: reason_text.into(),
                tone: NoticeTone::Neutral,
                sticky: Some(true),
                lasting: true,
                action: Some(NoticeActionSpec {
                    label: "Re-enable system EQ".into(),
                    primary: true,
                    danger: false,
                    busy: false,
                    confirm: None,
                    kind: NoticeActionKind::ReenableSystemEq {
                        notice_key: SYSTEM_EQ_REENABLE_NOTICE_KEY.into(),
                    },
                }),
                ..RichNoticeSpec::default()
            });
        } else {
            self.dismiss_notice_by_key(SYSTEM_EQ_REENABLE_NOTICE_KEY);
        }
    }

    pub fn eq_act(&mut self, row: &'static str, action: &'static str, extra: Value) {
        self.pc.audio_ui.eq_seq = self.pc.audio_ui.eq_seq.wrapping_add(1);
        let mine = self.pc.audio_ui.eq_seq;
        self.pc.audio_ui.eq_busy_row = Some(row.into());
        self.pc.audio_ui.eq_row_error = None;

        if self.status == HostStatus::Standalone {
            if let Some(ref mut eq) = self.pc.audio_ui.eq {
                match action {
                    "compare" => {
                        eq.comparing = extra
                            .get("on")
                            .and_then(|v| v.as_bool())
                            .unwrap_or(!eq.comparing);
                    }
                    "night" => {
                        if let Some(on) = extra.get("on").and_then(|v| v.as_bool()) {
                            eq.night.on = on;
                        }
                        if let Some(sync) = extra.get("sync").and_then(|v| v.as_bool()) {
                            eq.night.sync = sync;
                        }
                        if let Some(start) = extra.get("start").and_then(|v| v.as_str()) {
                            eq.night.start = start.to_string();
                        }
                        if let Some(end) = extra.get("end").and_then(|v| v.as_str()) {
                            eq.night.end = end.to_string();
                        }
                    }
                    "headphone" => {
                        if extra.get("id").is_none_or(|v| v.is_null()) {
                            eq.headphone = None;
                            self.pc.eq_headphone = None;
                        }
                    }
                    "lucid-off" => eq.lucid_plays = false,
                    "enable-system-eq" => {
                        if extra
                            .get("drmDisclosureAccepted")
                            .and_then(|v| v.as_bool())
                            .unwrap_or(false)
                        {
                            eq.apo.system_eq_enabled = true;
                            eq.apo.drm_disclosure_accepted = true;
                            eq.apo.needs_reenable = false;
                            eq.apo.reenable_reason = None;
                            eq.apo.protected_audio_dg_disabled = true;
                            eq.apo.installed = true;
                            eq.apo.on_output = true;
                        }
                    }
                    "reenable-system-eq" => {
                        eq.apo.system_eq_enabled = true;
                        eq.apo.needs_reenable = false;
                        eq.apo.reenable_reason = None;
                        eq.apo.protected_audio_dg_disabled = true;
                        eq.apo.on_output = true;
                    }
                    "disable-system-eq" => {
                        eq.apo.system_eq_enabled = false;
                        eq.apo.needs_reenable = false;
                        eq.apo.reenable_reason = None;
                        eq.apo.protected_audio_dg_disabled = false;
                    }
                    _ => {}
                }
            }
            self.pc.audio_ui.eq_busy_row = None;
            return;
        }

        let mut payload = match extra {
            Value::Object(m) => m,
            _ => Map::new(),
        };
        payload.insert("action".into(), Value::String(action.into()));
        if let Some(ref out_id) = self.pc.audio_output_id {
            payload.insert("output".into(), Value::String(out_id.clone()));
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc_img = rpc.clone();
            rpc.call::<_, EqStateReply>(METHOD_EQ, &Value::Object(payload), move |res| {
                let hp_for_img = res.as_ref().ok().and_then(|e| e.headphone.clone());
                let h_img = handle.clone();
                handle.update(move |s, _| {
                    match res {
                        Ok(next) => {
                            let err = if !next.ok {
                                next.message
                                    .clone()
                                    .or_else(|| Some("Exo EQ didn't answer. Try again.".into()))
                            } else {
                                None
                            };
                            if mine == s.pc.audio_ui.eq_seq {
                                s.apply_eq_state(next);
                            }
                            if let Some(msg) = err {
                                s.pc.audio_ui.eq_row_error = Some((row.into(), msg));
                            }
                        }
                        Err(_) => {
                            s.pc.audio_ui.eq_row_error =
                                Some((row.into(), "Exo EQ didn't answer. Try again.".into()));
                        }
                    }
                    if mine == s.pc.audio_ui.eq_seq {
                        s.pc.audio_ui.eq_busy_row = None;
                    }
                });
                if action == "headphone"
                    && let Some(hp) = hp_for_img
                {
                    let look = if hp.form == "over-ear" {
                        "headphones"
                    } else {
                        "iem"
                    };
                    rpc_img.call::<_, DevicesImageReply>(
                        METHOD_DEVICES_IMAGE,
                        &json!({ "kind": "audio", "model": look, "name": hp.name }),
                        move |img_res| {
                            if let Ok(reply) = img_res
                                && let Some(url) = reply.url
                            {
                                h_img.update(move |s, _| {
                                    if let Some(oid) = s.pc.audio_output_id.clone() {
                                        s.pc.device_images.insert(oid, url.clone());
                                    }
                                    s.pc.device_images.insert("audio:worn".into(), url);
                                });
                            }
                        },
                    );
                }
            });
        }
        #[cfg(not(windows))]
        let _ = (mine, payload);
    }

    pub fn open_headphone_picker(&mut self) {
        self.pc.audio_ui.headphone_picker_open = true;
        self.pc.audio_ui.headphone_query = TextEdit::default();
        if self.pc.audio_ui.headphones_failed {
            self.pc.audio_ui.headphones_list = None;
            self.pc.audio_ui.headphones_failed = false;
        }
        if self.pc.audio_ui.headphones_list.is_some() || self.pc.audio_ui.headphones_loading {
            return;
        }
        if self.status == HostStatus::Standalone {
            self.pc.audio_ui.headphones_list = Some(vec![
                EqHeadphone {
                    id: "sennheiser-hd-600".into(),
                    name: "Sennheiser HD 600".into(),
                    source: "oratory1990".into(),
                    form: "over-ear".into(),
                },
                EqHeadphone {
                    id: "moondrop-blessing-3".into(),
                    name: "Moondrop Blessing 3".into(),
                    source: "crinacle".into(),
                    form: "in-ear".into(),
                },
            ]);
            return;
        }
        self.pc.audio_ui.headphones_loading = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, EqHeadphonesReply>(METHOD_EQ_HEADPHONES, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.pc.audio_ui.headphones_loading = false;
                    match res {
                        Ok(reply) if reply.ok => {
                            s.pc.audio_ui.headphones_list = Some(reply.headphones);
                            s.pc.audio_ui.headphones_failed = false;
                        }
                        _ => {
                            s.pc.audio_ui.headphones_list = Some(Vec::new());
                            s.pc.audio_ui.headphones_failed = true;
                        }
                    }
                });
            });
        }
    }

    pub fn pick_eq_headphone(&mut self, hp: Option<EqHeadphone>) {
        self.pc.audio_ui.headphone_picker_open = false;
        self.pc.audio_ui.headphone_query = TextEdit::default();
        if let Some(ref h) = hp {
            self.pc.audio_ui.wearing = Some(Wearing::from_eq_headphone(h));
            self.pc.eq_headphone = Some(h.clone());
        }
        let id_val = hp
            .as_ref()
            .map(|h| Value::String(h.id.clone()))
            .unwrap_or(Value::Null);
        self.eq_act("headphone", "headphone", json!({ "id": id_val }));
    }

    pub fn tick_audio_eq_polling(&mut self, now: f64) {
        if self.room != crate::state::Room::Devices || self.status == HostStatus::Standalone {
            return;
        }
        let is_audio_open = self
            .pc
            .selected_device_id
            .as_deref()
            .and_then(|id| {
                self.pc
                    .devices
                    .as_ref()
                    .and_then(|d| d.devices.iter().find(|x| x.id == id))
            })
            .is_some_and(|d| d.kind == "audio");
        if !is_audio_open {
            return;
        }
        if self.pc.audio_ui.dragging_output_vol.is_none()
            && self.pc.audio_ui.dragging_mic_vol.is_none()
            && now - self.pc.audio_ui.last_audio_poll >= 3.0
        {
            self.pc.audio_ui.last_audio_poll = now;
            self.refresh_audio_details();
        }
        let eq_waiting = self
            .pc
            .audio_ui
            .eq
            .as_ref()
            .is_some_and(|e| e.apo.installing || (e.apo.installed && !e.apo.on_output));
        if eq_waiting && now - self.pc.audio_ui.last_eq_poll >= 4.0 {
            self.pc.audio_ui.last_eq_poll = now;
            self.refresh_eq_state();
        }
    }
}

fn eq_error_for(s: &AppState, row_name: &str) -> Option<String> {
    s.pc.audio_ui
        .eq_row_error
        .as_ref()
        .filter(|(r, _)| r == row_name)
        .map(|(_, msg)| msg.clone())
}

fn reenable_reason_detail(reason: Option<&str>) -> &'static str {
    match reason {
        Some("protected-audiodg-reset") => {
            "Windows reset the unsigned APO setting after an update. Re-enable to restore system-wide EQ."
        }
        Some("audiodg-rejected") => {
            "Windows Audio rejected Exo's EQ engine. Re-enable to reapply settings and restart Audio."
        }
        Some("endpoint-reset") => {
            "Windows reset the audio endpoint settings. Re-enable to reconnect system-wide EQ."
        }
        Some("effects-off") => {
            "Windows turned audio enhancements off for this output. Re-enable system-wide EQ when ready."
        }
        _ => "System-wide EQ needs to be re-enabled after a Windows audio change.",
    }
}

fn apo_panel(s: &AppState) -> Option<El> {
    let Some(eq) = s.pc.audio_ui.eq.as_ref() else {
        let err = s.pc.audio_ui.eq_load_error.clone()?;
        return Some(device_panel(
            "Exo EQ",
            vec![device_row(
                "Exo EQ couldn't load",
                None,
                Some(err),
                Some(
                    device_btn("eq-retry", "Try again", false)
                        .on_press(|s: &mut AppState| s.refresh_eq_state()),
                ),
            )],
        ));
    };

    let busy = s.pc.audio_ui.eq_busy_row.is_some();
    let apo = &eq.apo;
    let out_name = eq
        .output
        .as_ref()
        .and_then(|o| o.name.as_deref())
        .unwrap_or("this output");

    let mut rows = Vec::new();
    if apo.native_available {
        if apo.system_eq_enabled && apo.needs_reenable {
            rows.push(device_row(
                "System-wide EQ needs re-enabling",
                Some(reenable_reason_detail(apo.reenable_reason.as_deref()).into()),
                eq_error_for(s, "system-eq"),
                Some(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            primary_button("eq-reenable", "Re-enable system EQ", None).on_press(
                                |s: &mut AppState| {
                                    s.eq_act("system-eq", "reenable-system-eq", json!({}))
                                },
                            ),
                        )
                        .child(
                            device_btn("eq-disable", "Turn off system EQ", busy).on_press(
                                |s: &mut AppState| {
                                    s.eq_act("system-eq", "disable-system-eq", json!({}))
                                },
                            ),
                        ),
                ),
            ));
        } else if apo.system_eq_enabled {
            rows.push(device_row(
                "System-wide EQ",
                Some(format!(
                    "Exo's audio engine is active on {out_name}. Turning it off restores Windows' prior audio policy and restarts Audio."
                )),
                eq_error_for(s, "system-eq"),
                Some(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(status_badge("On".into(), BadgeTone::Good))
                        .child(
                            device_btn("eq-disable", "Turn off system EQ", busy).on_press(
                                |s: &mut AppState| {
                                    s.eq_act("system-eq", "disable-system-eq", json!({}))
                                },
                            ),
                        ),
                ),
            ));
        } else if s.pc.audio_ui.system_eq_disclosure_open {
            rows.push(device_row(
                "Before turning on system-wide EQ",
                Some(SYSTEM_EQ_DRM_DISCLOSURE.into()),
                eq_error_for(s, "system-eq"),
                Some(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            device_btn("eq-disclosure-cancel", "Cancel", busy)
                                .on_press(|s: &mut AppState| s.cancel_system_eq_disclosure()),
                        )
                        .child(
                            primary_button("eq-disclosure-confirm", "Confirm & enable", None)
                                .on_press(|s: &mut AppState| s.confirm_enable_system_eq()),
                        ),
                ),
            ));
        } else {
            rows.push(device_row(
                "System-wide EQ",
                Some(format!(
                    "Runs Exo's built-in audio engine on {out_name} with one-click rollback."
                )),
                eq_error_for(s, "system-eq"),
                Some(
                    primary_button("eq-enable-native", "Enable system EQ", None)
                        .on_press(|s: &mut AppState| s.open_system_eq_disclosure()),
                ),
            ));
        }
    }

    if !apo.installed && !apo.native_available {
        let label = if apo.installing {
            "Installing Equalizer APO"
        } else {
            "Install Equalizer APO"
        };
        let detail = if apo.installing {
            "Say yes when Windows asks, then tick your headphones and speakers in its Device Selector."
        } else {
            "Exo EQ plays through Equalizer APO, a free audio engine. Exo downloads it from SourceForge (12 MB), Windows asks once for permission, then you pick your outputs and restart the PC."
        };
        let disabled = busy || apo.installing;
        let btn = if disabled {
            device_btn("eq-install", "Installing…", true)
        } else {
            primary_button("eq-install", "Install", None)
                .on_press(|s: &mut AppState| s.eq_act("install", "install", json!({})))
        };
        return Some(device_panel(
            "Exo EQ",
            vec![device_row(
                label,
                Some(detail.into()),
                eq_error_for(s, "install"),
                Some(btn),
            )],
        ));
    }

    if apo.installed && !apo.system_eq_enabled {
        if !apo.on_output {
            rows.push(device_row(
                format!("Equalizer APO isn't on {out_name}"),
                Some(
                    "Tick it in Equalizer APO's Device Selector, then restart the PC once.".into(),
                ),
                eq_error_for(s, "devices"),
                Some(
                    device_btn("eq-devices", "Choose outputs", busy)
                        .on_press(|s: &mut AppState| s.eq_act("devices", "devices", json!({}))),
                ),
            ));
        } else if apo.restart {
            rows.push(device_row(
                "Restart to hear Exo EQ",
                Some(format!(
                    "Equalizer APO was just turned on for {out_name}. It plays after the PC restarts."
                )),
                None,
                Some(status_badge("Restart".into(), BadgeTone::Warn)),
            ));
        }
    }
    if apo.effects_off {
        rows.push(device_row(
            "Audio enhancements are off",
            Some(format!(
                "Windows keeps Equalizer APO out of {out_name} while they're off. Turn them on in its Properties, under Advanced."
            )),
            eq_error_for(s, "sound-settings"),
            Some(
                device_btn("eq-sound-settings", "Sound settings", busy).on_press(
                    |s: &mut AppState| s.eq_act("sound-settings", "sound-settings", json!({})),
                ),
            ),
        ));
    }
    if eq.lucid_plays {
        rows.push(device_row(
            "Lucid's EQ plays too",
            Some("Its block in Equalizer APO's config corrects the sound a second time.".into()),
            eq_error_for(s, "lucid-off"),
            Some(
                device_btn("eq-lucid-off", "Turn Lucid's off", busy)
                    .on_press(|s: &mut AppState| s.eq_act("lucid-off", "lucid-off", json!({}))),
            ),
        ));
    }
    if rows.is_empty() {
        None
    } else {
        Some(device_panel("Exo EQ", rows))
    }
}

fn headphone_panel(s: &AppState) -> Option<El> {
    let eq = s.pc.audio_ui.eq.as_ref()?;
    let busy_all = s.pc.audio_ui.eq_busy_row.is_some();
    let busy_hp = s.pc.audio_ui.eq_busy_row.as_deref() == Some("headphone");
    let picked = eq.headphone.as_ref();

    let mut rows = Vec::new();
    if let Some(guess) = eq.guess.clone() {
        let g = guess.clone();
        rows.push(device_row(
            format!("Looks like {}", guess.name),
            None,
            None,
            Some(
                device_btn("eq-use-guess", "Use it", busy_all)
                    .on_press(move |s: &mut AppState| s.pick_eq_headphone(Some(g.clone()))),
            ),
        ));
    }

    let detail = if busy_hp {
        "Downloading its correction".to_string()
    } else if let Some(hp) = picked {
        let target = if hp.form == "over-ear" {
            "Harman OE"
        } else {
            "JM-1 / IEF 2025"
        };
        format!("{} · {} · {target}", hp.name, hp.source)
    } else {
        "None: pick yours for its correction".to_string()
    };

    let err = if s.pc.audio_ui.headphones_failed && s.pc.audio_ui.headphone_picker_open {
        Some("AutoEq's list didn't load. Check your connection, then try again.".into())
    } else {
        eq_error_for(s, "headphone")
    };

    let control = if s.pc.audio_ui.headphone_picker_open {
        row()
            .h(32.0)
            .w(210.0)
            .px(10.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .border(1.0, glass_rim())
            .child(icon_search(13.0, fg_muted()))
            .child(
                text_input(&s.pc.audio_ui.headphone_query, type_body())
                    .key("eq-hp-search")
                    .label("Search headphones")
                    .single_line()
                    .grow(1.0)
                    .min_w(0.0)
                    .color(FG)
                    .placeholder("HD 600", fg_faint())
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.pc.audio_ui.headphone_query
                    }))
                    .on_input_key(|s: &mut AppState, ev, _| {
                        if ev.key == Key::Escape {
                            s.pc.audio_ui.headphone_picker_open = false;
                            return true;
                        }
                        false
                    }),
            )
    } else {
        let mut btns = row().gap(8.0).center_y();
        if picked.is_some() {
            btns = btns.child(
                device_btn("eq-hp-none", "None", busy_hp)
                    .on_press(|s: &mut AppState| s.pick_eq_headphone(None)),
            );
        }
        btns = btns.child(
            device_btn(
                "eq-hp-pick",
                if picked.is_some() { "Change" } else { "Pick" },
                busy_hp,
            )
            .on_press(|s: &mut AppState| s.open_headphone_picker()),
        );
        btns
    };

    rows.push(device_row("Correction", Some(detail), err, Some(control)));

    if s.pc.audio_ui.headphone_picker_open {
        let query = s.pc.audio_ui.headphone_query.text.as_str();
        let mut results = col().w_full().gap(4.0).pt(4.0);
        if s.pc.audio_ui.headphones_loading || s.pc.audio_ui.headphones_list.is_none() {
            results = results.child(
                text("Reading AutoEq's list…")
                    .font(type_small())
                    .color(fg_muted()),
            );
        } else if let Some(list) = s.pc.audio_ui.headphones_list.as_ref() {
            if query.trim().is_empty() {
                let msg = if list.is_empty() {
                    "No list".to_string()
                } else {
                    format!("Type a model: {} to search", list.len())
                };
                results = results.child(text(msg).font(type_small()).color(fg_muted()));
            } else {
                let hits = filter_headphones(list, query);
                if hits.is_empty() {
                    results = results.child(text("No match").font(type_small()).color(fg_muted()));
                } else {
                    for hp in hits {
                        let hp_clone = hp.clone();
                        results = results.child(
                            row()
                                .key(format!("eq-hp-opt-{}", hp.id))
                                .role("button")
                                .focusable(true)
                                .hit()
                                .press_on_down()
                                .cursor(Cursor::Pointer)
                                .w_full()
                                .px(10.0)
                                .py(8.0)
                                .radius(RADIUS_SMALL)
                                .justify_between()
                                .center_y()
                                .hover(hover_fill())
                                .on_press(move |s: &mut AppState| {
                                    s.pick_eq_headphone(Some(hp_clone.clone()));
                                })
                                .child(text(hp.name.clone()).font(type_body()).color(FG))
                                .child(
                                    text(hp.source.clone()).font(type_small()).color(fg_muted()),
                                ),
                        );
                    }
                }
            }
        }
        rows.push(results);
    }

    Some(device_panel("Headphones", rows))
}

fn sound_panel(s: &AppState) -> Option<El> {
    let eq = s.pc.audio_ui.eq.as_ref()?;
    if !eq.apo.installed && !eq.apo.system_eq_enabled {
        return None;
    }
    let active_sound = eq
        .playing
        .as_ref()
        .and_then(|p| p.sound.as_deref())
        .unwrap_or(eq.sound.as_str());
    let active_label = eq
        .playing
        .as_ref()
        .and_then(|p| p.label.as_deref())
        .unwrap_or_else(|| sound_label(active_sound));
    let detail = if eq.comparing {
        "Bypassed while you compare without EQ".to_string()
    } else if let Some(ref playing) = eq.playing
        && !playing.title.is_empty()
    {
        if let Some(ref reason) = playing.reason
            && !reason.is_empty()
        {
            format!("{active_label} for {} — {reason}", playing.title)
        } else {
            format!("{active_label} for {}", playing.title)
        }
    } else {
        "Automatic per-game profiles · Studio reference on desktop, switches live for every game."
            .to_string()
    };

    let comparing = eq.comparing;
    let busy_compare = s.pc.audio_ui.eq_busy_row.as_deref() == Some("compare");
    let mut rows = vec![
        device_row(
            "Exo EQ",
            Some(detail),
            eq_error_for(s, "compare"),
            Some(
                row()
                    .gap(8.0)
                    .center_y()
                    .child(status_badge(
                        if comparing {
                            "Bypassed".into()
                        } else {
                            active_label.into()
                        },
                        if comparing {
                            BadgeTone::Neutral
                        } else {
                            BadgeTone::Good
                        },
                    ))
                    .child(
                        device_btn(
                            "eq-compare",
                            if comparing { "Back to EQ" } else { "Compare" },
                            busy_compare,
                        )
                        .on_press(move |s: &mut AppState| {
                            s.eq_act("compare", "compare", json!({ "on": !comparing }));
                        }),
                    ),
            ),
        ),
        device_row(
            "Game profiles",
            Some(
                "Shooters → Footsteps · Story & RPG → Vocal · Action & Racing → Immersion · Rhythm & desktop → Reference."
                    .into(),
            ),
            None,
            Some(status_badge("Auto".into(), BadgeTone::Good)),
        ),
    ];

    if eq.preamp < 0.0 {
        rows.push(device_row(
            "Headroom",
            Some("Exo lowers the level this much, so the boosts never clip.".into()),
            None,
            Some(status_badge(
                format!("{:.1} dB", eq.preamp),
                BadgeTone::Neutral,
            )),
        ));
    }

    if !eq.others.is_empty() {
        let summary = eq
            .others
            .iter()
            .map(|o| {
                let mut parts = Vec::new();
                if let Some(ref hp) = o.headphone
                    && !hp.is_empty()
                {
                    parts.push(hp.as_str());
                }
                if o.sound != "flat" {
                    parts.push(sound_label(&o.sound));
                }
                format!("{}: {}", o.name, parts.join(", "))
            })
            .collect::<Vec<_>>()
            .join("; ");
        rows.push(device_row(
            "Other outputs",
            Some(summary),
            None,
            Some(status_badge("Saved".into(), BadgeTone::Neutral)),
        ));
    }

    Some(device_panel("Sound", rows))
}

fn night_panel(s: &AppState) -> Option<El> {
    let eq = s.pc.audio_ui.eq.as_ref()?;
    if !eq.apo.installed && !eq.apo.system_eq_enabled {
        return None;
    }
    let night = &eq.night;
    let on = night.on;
    let sync = night.sync;
    let mut rows = vec![device_row(
        "Night Shift",
        (night.on && night.now).then(|| "On now".into()),
        eq_error_for(s, "night"),
        Some(
            device_switch("eq-night-toggle", "Night Shift", on, false)
                .on_press(move |s: &mut AppState| s.eq_act("night", "night", json!({ "on": !on }))),
        ),
    )];

    if on {
        rows.push(device_row(
            "Follow Windows Night light",
            None,
            None,
            Some(
                device_switch("eq-night-sync", "Follow Windows Night light", sync, false).on_press(
                    move |s: &mut AppState| s.eq_act("night", "night", json!({ "sync": !sync })),
                ),
            ),
        ));
        if !sync {
            let start_open = s.pc.audio_ui.open_picker.as_deref() == Some("night-start");
            let end_open = s.pc.audio_ui.open_picker.as_deref() == Some("night-end");
            rows.push(device_row(
                "Hours",
                None,
                eq_error_for(s, "hours"),
                Some(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            device_btn("eq-night-start", format_time_slot(&night.start), false)
                                .on_press(move |s: &mut AppState| {
                                    s.pc.audio_ui.open_picker = if start_open {
                                        None
                                    } else {
                                        Some("night-start".into())
                                    };
                                }),
                        )
                        .child(text("to").font(type_small()).color(fg_muted()))
                        .child(
                            device_btn("eq-night-end", format_time_slot(&night.end), false)
                                .on_press(move |s: &mut AppState| {
                                    s.pc.audio_ui.open_picker = if end_open {
                                        None
                                    } else {
                                        Some("night-end".into())
                                    };
                                }),
                        ),
                ),
            ));

            if start_open || end_open {
                let is_start = start_open;
                let current_val = if is_start {
                    night.start.as_str()
                } else {
                    night.end.as_str()
                };
                let mut list = col()
                    .w_full()
                    .max_h(180.0)
                    .scroll(ScrollSpec {
                        stick_bottom: false,
                    })
                    .gap(2.0)
                    .py(4.0);
                for (val, lbl) in half_hour_slots() {
                    let selected = val == current_val;
                    let v_clone = val.clone();
                    list = list.child(
                        row()
                            .key(format!(
                                "eq-time-{}-{val}",
                                if is_start { "s" } else { "e" }
                            ))
                            .role("button")
                            .focusable(true)
                            .hit()
                            .press_on_down()
                            .cursor(Cursor::Pointer)
                            .w_full()
                            .px(10.0)
                            .py(6.0)
                            .radius(RADIUS_SMALL)
                            .fill(if selected {
                                hover_fill()
                            } else {
                                Color::TRANSPARENT
                            })
                            .hover(hover_fill())
                            .on_press(move |s: &mut AppState| {
                                s.pc.audio_ui.open_picker = None;
                                if is_start {
                                    s.eq_act("hours", "night", json!({ "start": v_clone }));
                                } else {
                                    s.eq_act("hours", "night", json!({ "end": v_clone }));
                                }
                            })
                            .child(text(lbl).font(type_small()).color(FG)),
                    );
                }
                rows.push(list);
            }
        }
    }
    Some(device_panel("Night Shift", rows))
}

fn endpoint_panel(
    s: &AppState,
    title: &'static str,
    is_output: bool,
    ep: &exo_rpc::AudioEndpoint,
) -> El {
    let prefix = if is_output { "out" } else { "mic" };
    let formats = if is_output {
        s.pc.audio_ui.output_formats.as_ref()
    } else {
        s.pc.audio_ui.mic_formats.as_ref()
    };
    let vol_err = if is_output {
        s.pc.audio_ui.output_volume_error.clone()
    } else {
        s.pc.audio_ui.mic_volume_error.clone()
    };
    let mute_err = if is_output {
        s.pc.audio_ui.output_mute_error.clone()
    } else {
        s.pc.audio_ui.mic_mute_error.clone()
    };
    let fmt_err = if is_output {
        s.pc.audio_ui.output_format_error.clone()
    } else {
        s.pc.audio_ui.mic_format_error.clone()
    };

    let mut rows = Vec::new();
    let picker_key = format!("audio-fmt-{prefix}");
    let picker_open = s.pc.audio_ui.open_picker.as_deref() == Some(picker_key.as_str());

    if let Some(offered) = formats
        && offered.current.is_some()
        && offered.formats.len() > 1
    {
        let current_key = offered.current.as_deref().unwrap_or("");
        let current_label = offered
            .formats
            .iter()
            .find(|f| f.key == current_key)
            .map(|f| f.display_words())
            .or_else(|| ep.format.as_ref().map(|f| f.display_words()))
            .unwrap_or_else(|| current_key.to_string());
        let pk = picker_key.clone();
        rows.push(device_row(
            "Format",
            None,
            fmt_err,
            Some(
                device_btn(format!("aud-fmt-btn-{prefix}"), current_label, false)
                    .child(icon_chevron_down(12.0, fg_muted()))
                    .on_press(move |s: &mut AppState| {
                        s.pc.audio_ui.open_picker =
                            if picker_open { None } else { Some(pk.clone()) };
                    }),
            ),
        ));
        if picker_open {
            let mut list = col()
                .w_full()
                .max_h(180.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .gap(2.0)
                .py(4.0);
            for opt in &offered.formats {
                let selected = opt.key == current_key;
                let k = opt.key.clone();
                list = list.child(
                    row()
                        .key(format!("aud-fmt-opt-{prefix}-{}", opt.key))
                        .role("button")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(6.0)
                        .radius(RADIUS_SMALL)
                        .fill(if selected {
                            hover_fill()
                        } else {
                            Color::TRANSPARENT
                        })
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| {
                            s.set_audio_format(is_output, k.clone());
                        })
                        .child(text(opt.display_words()).font(type_small()).color(FG)),
                );
            }
            rows.push(list);
        }
    } else if let Some(ref fmt) = ep.format {
        rows.push(device_row(
            "Format",
            None,
            fmt_err,
            Some(status_badge(fmt.display_words(), BadgeTone::Neutral)),
        ));
    }

    if let Some(ep_vol) = ep.volume {
        let dragging = if is_output {
            s.pc.audio_ui.dragging_output_vol
        } else {
            s.pc.audio_ui.dragging_mic_vol
        };
        let shown = dragging.unwrap_or(ep_vol).clamp(0, 100);
        let slider = slider_track(
            format!("aud-vol-slider-{prefix}"),
            format!("{title} volume"),
            150.0,
            shown as f32 / 100.0,
            dragging.is_some(),
            false,
        )
        .on_drag(move |s: &mut AppState, d, _| {
            let ratio = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
            let next = (ratio * 100.0).round() as i32;
            match d.phase {
                Phase::Down | Phase::Move => {
                    if is_output {
                        s.pc.audio_ui.dragging_output_vol = Some(next);
                    } else {
                        s.pc.audio_ui.dragging_mic_vol = Some(next);
                    }
                }
                Phase::Up => {
                    if d.cancelled() {
                        if is_output {
                            s.pc.audio_ui.dragging_output_vol = None;
                        } else {
                            s.pc.audio_ui.dragging_mic_vol = None;
                        }
                    } else {
                        s.commit_audio_volume(is_output, next);
                    }
                }
            }
        });
        rows.push(device_row(
            "Volume",
            None,
            vol_err,
            Some(
                row().gap(10.0).center_y().child(slider).child(
                    text(format!("{shown}%"))
                        .font(type_small())
                        .color(fg_muted()),
                ),
            ),
        ));
    }

    if let Some(muted) = ep.muted {
        rows.push(device_row(
            "Mute",
            None,
            mute_err,
            Some(
                device_switch(
                    format!("aud-mute-{prefix}"),
                    format!("Mute {title}"),
                    muted,
                    false,
                )
                .on_press(move |s: &mut AppState| s.commit_audio_mute(is_output, !muted)),
            ),
        ));
    }

    device_panel(title, rows)
}

fn voice_panel(s: &AppState) -> Option<El> {
    let voice = &s.pc.audio.as_ref()?.voice;
    let noise_text = if voice.custom {
        noise_label(&voice.noise)
    } else {
        "Exo Voice".into()
    };
    let noise_tone = if voice.noise != "off" {
        BadgeTone::Good
    } else {
        BadgeTone::Neutral
    };
    let mut rows = vec![
        device_row(
            "Noise suppression",
            None,
            None,
            Some(status_badge(noise_text, noise_tone)),
        ),
        device_row(
            "Talks by",
            None,
            None,
            Some(status_badge(
                if voice.push_to_talk {
                    "Push to talk".into()
                } else {
                    "Voice activity".into()
                },
                BadgeTone::Neutral,
            )),
        ),
    ];
    if !voice.push_to_talk {
        let sens = match voice.sensitivity {
            Some(db) => format!("{db:.0} dB"),
            None => "Automatic".into(),
        };
        rows.push(device_row(
            "Input sensitivity",
            None,
            None,
            Some(status_badge(sens, BadgeTone::Neutral)),
        ));
    }
    if voice.custom {
        rows.push(device_row(
            "Voice Focus",
            None,
            None,
            Some(status_badge(
                if voice.focus {
                    "On".into()
                } else {
                    "Off".into()
                },
                if voice.focus {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            )),
        ));
    }
    Some(device_panel("Exo Voice", rows))
}

pub fn audio_page(
    s: &AppState,
    output: Option<&PeripheralCard>,
    mic: Option<&PeripheralCard>,
    display_name: String,
    status: String,
    scale: f32,
) -> El {
    let img_url =
        s.pc.device_images
            .get("audio:worn")
            .or_else(|| output.and_then(|d| s.pc.device_images.get(&d.id)));
    let media = if let Some(photo) = img_url.and_then(|u| s.device_image(u, scale)) {
        el().w_full()
            .h_full()
            .pixels_fit(photo.pixels(), SurfaceFit::Fit)
    } else {
        exo_kit::icon_volume(64.0, fg_muted())
    };

    let out_ep = s.pc.audio.as_ref().and_then(|a| a.output.as_ref());
    let mut stats = Vec::new();
    if let Some(o) = output {
        stats.push(("Playing on", o.name.clone()));
    }
    if let Some(m) = mic {
        stats.push(("Microphone", m.name.clone()));
    }
    if let Some(fmt) = out_ep.and_then(|e| e.format.as_ref()) {
        stats.push(("Format", fmt.rate_khz()));
    }
    if let Some(vol) = out_ep.and_then(|e| e.volume) {
        let val = if out_ep.and_then(|e| e.muted).unwrap_or(false) {
            "Muted".to_string()
        } else {
            format!("{vol}%")
        };
        stats.push(("Volume", val));
    }

    let worn = output.is_some_and(|o| {
        o.model.as_deref() != Some("speakers") && o.model.as_deref() != Some("monitor")
    });

    let mut col_a = Vec::new();
    let mut col_b = Vec::new();
    if let Some(p) = apo_panel(s) {
        col_a.push(p);
    }
    if worn && let Some(p) = headphone_panel(s) {
        col_a.push(p);
    }
    if let Some(p) = sound_panel(s) {
        col_a.push(p);
    }
    if let Some(p) = night_panel(s) {
        col_a.push(p);
    }
    if let (Some(_), Some(ep)) = (output, s.pc.audio.as_ref().and_then(|a| a.output.as_ref())) {
        col_a.push(endpoint_panel(s, "Output", true, ep));
    }
    if let (Some(_), Some(ep)) = (mic, s.pc.audio.as_ref().and_then(|a| a.mic.as_ref())) {
        col_b.push(endpoint_panel(s, "Microphone", false, ep));
    }
    if let Some(p) = voice_panel(s) {
        col_b.push(p);
    }

    device_page_shell_band(
        DevicePageHeader {
            key: "room-devices-audio",
            name: display_name,
            status,
            media,
            stats,
            credit: Some("Corrections by AutoEq, sound by Equalizer APO"),
        },
        col_a,
        col_b,
        |s| {
            s.pc.selected_device_id = None;
            s.pc.audio_ui.open_picker = None;
            s.pc.audio_ui.headphone_picker_open = false;
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn port_words_and_short_match_react() {
        let cpu = PeripheralPort {
            r#where: Some("cpu".into()),
            hubs: 0,
            ..Default::default()
        };
        assert_eq!(port_words(&cpu), "Processor's own USB");
        assert_eq!(port_short(&cpu).as_deref(), Some("CPU USB"));

        let hub = PeripheralPort {
            r#where: Some("chipset".into()),
            hubs: 2,
            ..Default::default()
        };
        assert_eq!(port_words(&hub), "Through 2 hubs and the chipset");
        assert_eq!(port_short(&hub).as_deref(), Some("Chipset USB via hub"));
    }

    #[test]
    fn filter_headphones_matches_multi_word_and_caps_at_six() {
        let list: Vec<EqHeadphone> = (0..10)
            .map(|i| EqHeadphone {
                id: format!("id-{i}"),
                name: format!("Sennheiser HD 6{i}0"),
                source: "oratory1990".into(),
                form: "over-ear".into(),
            })
            .collect();
        assert!(filter_headphones(&list, "   ").is_empty());
        let hits = filter_headphones(&list, "hd senn");
        assert_eq!(hits.len(), 6);
        assert_eq!(filter_headphones(&list, "hd 600").len(), 1);
    }

    #[test]
    fn half_hour_slots_cover_24_hours() {
        let slots = half_hour_slots();
        assert_eq!(slots.len(), 48);
        assert_eq!(slots[0], ("00:00".into(), "12:00 AM".into()));
        assert_eq!(slots[24], ("12:00".into(), "12:00 PM".into()));
        assert_eq!(slots[47], ("23:30".into(), "11:30 PM".into()));
    }

    #[test]
    fn system_eq_disclosure_requires_explicit_confirm_and_matches_required_copy() {
        assert_eq!(
            SYSTEM_EQ_DRM_DISCLOSURE,
            "Some protected audio playback (DRM streams) may not play while system-wide EQ is on. You can turn it off any time."
        );
        let mut s = AppState::default();
        s.pc.audio_ui = AudioEqUiState::demo();
        assert!(!s.pc.audio_ui.eq.as_ref().unwrap().apo.system_eq_enabled);
        assert!(!s.pc.audio_ui.system_eq_disclosure_open);

        s.open_system_eq_disclosure();
        assert!(s.pc.audio_ui.system_eq_disclosure_open);
        assert!(!s.pc.audio_ui.eq.as_ref().unwrap().apo.system_eq_enabled);
        assert!(apo_panel(&s).is_some());

        s.cancel_system_eq_disclosure();
        assert!(!s.pc.audio_ui.system_eq_disclosure_open);
        assert!(!s.pc.audio_ui.eq.as_ref().unwrap().apo.system_eq_enabled);

        s.open_system_eq_disclosure();
        s.confirm_enable_system_eq();
        assert!(!s.pc.audio_ui.system_eq_disclosure_open);
        let apo = &s.pc.audio_ui.eq.as_ref().unwrap().apo;
        assert!(apo.system_eq_enabled);
        assert!(apo.drm_disclosure_accepted);
    }

    #[test]
    fn launch_health_check_prompts_reenable_without_auto_elevating() {
        let mut s = AppState::default();
        let mut eq = AudioEqUiState::demo().eq.unwrap();
        eq.apo.system_eq_enabled = true;
        eq.apo.needs_reenable = true;
        eq.apo.reenable_reason = Some("protected-audiodg-reset".into());

        s.apply_eq_state(eq);
        assert!(s.pc.audio_ui.eq_busy_row.is_none());
        let notice = s
            .notices
            .iter()
            .find(|n| n.key_id.as_deref() == Some(SYSTEM_EQ_REENABLE_NOTICE_KEY))
            .expect("reenable prompt notice pushed");
        let action = notice.action.as_ref().expect("reenable action present");
        assert_eq!(action.label, "Re-enable system EQ");
        assert!(matches!(
            action.kind,
            NoticeActionKind::ReenableSystemEq { .. }
        ));
        assert!(apo_panel(&s).is_some());

        let mut healthy = s.pc.audio_ui.eq.clone().unwrap();
        healthy.apo.needs_reenable = false;
        healthy.apo.reenable_reason = None;
        s.apply_eq_state(healthy);
        assert!(
            !s.notices
                .iter()
                .any(|n| n.key_id.as_deref() == Some(SYSTEM_EQ_REENABLE_NOTICE_KEY))
        );
    }
}
