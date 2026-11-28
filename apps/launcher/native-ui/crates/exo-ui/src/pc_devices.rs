use crate::devices_audio::{
    DevicePageHeader, device_btn, device_icon_btn, device_page_shell_columns_no_rescan,
    device_panel, device_row, device_switch, port_short, port_words,
};
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PANEL, SPRING_MOVE, SPRING_QUICK,
    amoled_card, bad, card_border, fg_faint, fg_muted, filter_pill, glass_rim_bottom,
    glass_rim_top, good, hairline, hover_fill, icon_chevron_left, icon_external, icon_gamepad,
    icon_headphones, icon_keyboard, icon_mic, icon_mouse, icon_refresh, pressed_fill,
    primary_button, scaled, status_badge, type_body, type_control, type_label, type_section,
    type_small, type_title,
};
use exo_rpc::keyboard_layouts::{KeyCap, KeyKind, build_keyboard_layout, find_key};
use exo_rpc::{KeyboardReadReply, PeripheralCard};
use rune::{Cursor, El, Phase, ScrollSpec, SurfaceFit, TextStyle, col, el, row, text};

fn connection_label(conn: Option<&str>) -> &'static str {
    match conn.unwrap_or("") {
        "usb" => "Wired",
        "wireless" => "Wireless",
        "bluetooth" => "Bluetooth",
        "jack" => "3.5 mm jack",
        "hdmi" => "HDMI",
        other if !other.is_empty() => "Connected",
        _ => "Wired",
    }
}

fn device_fallback_icon_name(kind: &str, role: Option<&str>) -> &'static str {
    match (kind, role) {
        ("mouse", _) => "mouse",
        ("keyboard", _) => "keyboard",
        ("controller", _) => "gamepad",
        (_, Some("input")) => "mic",
        _ => "headphones",
    }
}

fn device_fallback_icon(kind: &str, role: Option<&str>) -> El {
    let icon_el = match device_fallback_icon_name(kind, role) {
        "mouse" => icon_mouse(44.0, FG.alpha(0.88)),
        "keyboard" => icon_keyboard(44.0, FG.alpha(0.88)),
        "gamepad" => icon_gamepad(44.0, FG.alpha(0.88)),
        "mic" => icon_mic(44.0, FG.alpha(0.88)),
        _ => icon_headphones(44.0, FG.alpha(0.88)),
    };
    let tint = match device_fallback_icon_name(kind, role) {
        "mouse" => rune::oklch(0.32, 0.08, 250.0),
        "keyboard" => rune::oklch(0.32, 0.08, 290.0),
        "gamepad" => rune::oklch(0.32, 0.08, 165.0),
        "mic" => rune::oklch(0.32, 0.08, 25.0),
        _ => rune::oklch(0.32, 0.08, 210.0),
    };
    el().size(104.0, 104.0)
        .radius(28.0)
        .continuous_corners()
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, tint.alpha(0.45)),
                (1.0, exo_kit::surface_elevated_hi()),
            ],
        )
        .border_gradient(
            1.0,
            rune::Color::WHITE.alpha(0.18),
            rune::Color::WHITE.alpha(0.04),
        )
        .shadow(0.0, 14.0, BG.alpha(0.55))
        .center()
        .child(icon_el)
}

fn clean_device_name(dev: &PeripheralCard, s: &AppState) -> String {
    if dev.kind == "audio" && dev.role.as_deref() != Some("input") {
        let speakers = matches!(dev.model.as_deref(), Some("speakers" | "monitor"));
        if !speakers
            && let Some(ref hp) = s.pc.eq_headphone
            && !hp.name.trim().is_empty()
        {
            return hp.name.clone();
        }
    }
    if dev.kind == "controller" {
        for suffix in [
            " Wireless Controller",
            " wireless controller",
            " Controller",
            " controller",
        ] {
            if let Some(stripped) = dev.name.strip_suffix(suffix)
                && !stripped.trim().is_empty()
            {
                return stripped.trim().to_string();
            }
        }
    }
    if dev.kind == "mouse"
        && let Some(mouse) = receiver_mouse_name(&dev.name)
    {
        return mouse;
    }
    dev.name.clone()
}

fn is_receiver_word(word: &str) -> bool {
    word.eq_ignore_ascii_case("dongle") || word.eq_ignore_ascii_case("receiver")
}

fn name_key(name: &str) -> String {
    name.chars()
        .filter(char::is_ascii_alphanumeric)
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

fn known_mice() -> &'static [(String, String)] {
    use exo_hid_devices::registry::{DeviceCategory, catalog};
    static MICE: std::sync::OnceLock<Vec<(String, String)>> = std::sync::OnceLock::new();
    MICE.get_or_init(|| {
        let mut mice: Vec<(String, String)> = Vec::new();
        for entry in catalog() {
            if entry.category != DeviceCategory::Mouse {
                continue;
            }
            let vendor = entry.vendor.display_name();
            let base = entry
                .model_name
                .split(" (")
                .next()
                .unwrap_or(&entry.model_name);
            let model = base.strip_prefix(vendor).unwrap_or(base).trim();
            if model.is_empty()
                || model.split_whitespace().any(is_receiver_word)
                || mice.iter().any(|(_, m)| m == model)
            {
                continue;
            }
            mice.push((name_key(vendor), model.to_string()));
        }
        mice
    })
}

fn receiver_mouse_name(name: &str) -> Option<String> {
    let words: Vec<&str> = name.split_whitespace().collect();
    if !words.iter().any(|w| is_receiver_word(w)) {
        return None;
    }
    let wanted = name_key(
        &words
            .iter()
            .filter(|w| !is_receiver_word(w))
            .copied()
            .collect::<Vec<_>>()
            .join(" "),
    );
    if wanted.is_empty() {
        return None;
    }
    known_mice()
        .iter()
        .find(|(vendor, model)| {
            let key = name_key(model);
            wanted == key || wanted == format!("{vendor}{key}")
        })
        .map(|(_, model)| model.clone())
}

fn format_battery_pct(b: f64) -> i32 {
    if (0.0..=1.0).contains(&b) {
        (b * 100.0).round() as i32
    } else {
        b.round() as i32
    }
}

fn device_showcase_card(dev: &PeripheralCard, s: &AppState, scale: f32) -> El {
    device_showcase_card_sized(dev, s, scale, 468.0, 312.0)
}

fn device_showcase_card_sized(
    dev: &PeripheralCard,
    s: &AppState,
    scale: f32,
    max_w: f32,
    max_h: f32,
) -> El {
    let img_url = if dev.kind == "audio" && dev.role.as_deref() != Some("input") {
        s.pc.device_images
            .get("audio:worn")
            .or_else(|| s.pc.device_images.get(&dev.id))
    } else {
        s.pc.device_images.get(&dev.id)
    };
    let img = img_url.and_then(|u| s.device_image(u, scale));

    let media = if let Some(photo) = img {
        el().w_full().grow(1.0).min_h(0.0).center().child(
            el().w_full()
                .max_w(max_w)
                .h_full()
                .max_h(max_h)
                .pixels_fit(photo.pixels(), SurfaceFit::Fit),
        )
    } else {
        el().w_full()
            .grow(1.0)
            .min_h(0.0)
            .center()
            .child(device_fallback_icon(&dev.kind, dev.role.as_deref()))
    };

    let display_name = clean_device_name(dev, s);
    let sub = match dev.battery {
        Some(b) => format!(
            "{}% · {}",
            format_battery_pct(b),
            connection_label(dev.connection.as_deref())
        ),
        None => connection_label(dev.connection.as_deref()).to_string(),
    };

    let port_warn = dev.port.as_ref().and_then(|p| match p.better.as_deref() {
        Some("free") => Some("Move to CPU USB"),
        Some("hub") => Some("Plug in without the hub"),
        _ => None,
    });

    let kind_label = match (dev.kind.as_str(), dev.role.as_deref()) {
        ("mouse", _) => "Mouse",
        ("keyboard", _) => "Keyboard",
        ("controller", _) => "Controller",
        (_, Some("input")) => "Microphone",
        _ => "Audio",
    };

    let status_pill = row()
        .h(26.0)
        .px(10.0)
        .gap(6.0)
        .center_y()
        .radius(exo_kit::RADIUS_PILL)
        .fill(exo_kit::surface_elevated_hi())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(el().size(6.0, 6.0).radius(3.0).fill(good()))
        .child(text(sub.clone()).font(type_small()).color(FG.alpha(0.88)));

    let top_bar = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            text(kind_label)
                .font(TextStyle::new(scaled(11.0), 600.0).letter_spacing(0.04))
                .color(fg_faint()),
        )
        .child(status_pill);

    let dev_id = dev.id.clone();
    let is_mouse = dev.kind == "mouse";
    let is_kb = dev.kind == "keyboard" && dev.lab;
    let is_audio = dev.kind == "audio";
    let is_controller = dev.kind == "controller";
    col()
        .key(format!("dev-card-{}", dev.id))
        .role("button")
        .label(display_name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(0.985)
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .pt(18.0)
        .px(22.0)
        .pb(20.0)
        .gap(14.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .hover(exo_kit::surface_elevated_hi())
        .hover_motion(SPRING_QUICK, SPRING_MOVE)
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| {
            if is_mouse {
                s.open_mouse_page(dev_id.clone());
            } else if is_controller {
                s.pc.selected_device_id = Some(dev_id.clone());
                s.open_controller_page(dev_id.clone());
            } else if is_audio {
                s.pc.selected_device_id = Some(dev_id.clone());
                s.open_audio_device_page();
            } else {
                s.pc.selected_device_id = Some(dev_id.clone());
                if is_kb {
                    s.pc.keyboard_id = Some(dev_id.clone());
                    let needs_reload = s.pc.keyboard.as_ref().is_none_or(|k| !k.ok);
                    if needs_reload {
                        s.reload_keyboard();
                    }
                }
            }
        })
        .child(top_bar)
        .child(media)
        .child(
            col()
                .w_full()
                .gap(4.0)
                .child(text(display_name).font(type_title()).color(FG).truncate())
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .gap(12.0)
                        .child(
                            text(if port_warn.is_some() {
                                "Optimization available"
                            } else {
                                "Ready to configure"
                            })
                            .font(type_small())
                            .color(fg_muted())
                            .truncate(),
                        )
                        .children(port_warn.map(|w| {
                            text(w)
                                .font(TextStyle::new(scaled(12.0), 600.0))
                                .color(exo_kit::warn())
                        })),
                ),
        )
}

fn render_key_section(
    rows: &[Vec<KeyCap>],
    grow: f32,
    rt_on: bool,
    selected_key: Option<&str>,
) -> El {
    let key_h = 26.0_f32;
    let gap = 4.0_f32;
    let lbl_font = TextStyle::new(scaled(10.0), 500.0);
    let mut section = col().grow(grow).min_w(0.0).gap(gap);
    for r in rows {
        let mut krow = row().w_full().gap(gap);
        for cap in r {
            if cap.kind == KeyKind::Gap {
                krow = krow.child(el().grow(cap.u).min_w(0.0).h(key_h));
                continue;
            }
            let is_selected = selected_key == Some(cap.id.as_str());
            let key_id = cap.id.clone();
            if cap.kind == KeyKind::LedBar {
                let mut bar = row()
                    .key(format!("kb-key-{}", cap.id))
                    .role("button")
                    .label(cap.label.clone())
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .grow(cap.u)
                    .min_w(0.0)
                    .h(key_h)
                    .px(6.0)
                    .radius(6.0)
                    .center_y()
                    .justify_between()
                    .fill(if is_selected {
                        FG.alpha(0.18)
                    } else {
                        FG.alpha(0.04)
                    })
                    .border(
                        1.0,
                        if is_selected {
                            FG.alpha(0.75)
                        } else {
                            FG.alpha(0.16)
                        },
                    )
                    .on_press(move |s: &mut AppState| s.toggle_keyboard_key(key_id.clone()));
                for idx in 0..10 {
                    let seg_c = if rt_on || idx < 6 {
                        good().alpha(0.85)
                    } else {
                        FG.alpha(0.30)
                    };
                    bar = bar.child(el().w(5.0).h(4.0).radius(2.0).fill(seg_c));
                }
                krow = krow.child(bar);
                continue;
            }

            let is_wasd = cap.kind == KeyKind::Wasd;
            let is_mod = matches!(cap.kind, KeyKind::Mod | KeyKind::Space);
            let fill = if is_selected {
                FG.alpha(0.18)
            } else if is_wasd && rt_on {
                good().alpha(0.14)
            } else if is_mod {
                FG.alpha(0.03)
            } else {
                FG.alpha(0.06)
            };
            let border_c = if is_selected {
                FG
            } else if is_wasd && rt_on {
                good().alpha(0.52)
            } else {
                FG.alpha(0.16)
            };
            let txt_c = if is_selected || !is_mod {
                FG
            } else {
                fg_muted()
            };
            krow = krow.child(
                col()
                    .key(format!("kb-key-{}", cap.id))
                    .role("button")
                    .label(cap.label.clone())
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .grow(cap.u)
                    .min_w(0.0)
                    .h(key_h)
                    .px(2.0)
                    .radius(6.0)
                    .center()
                    .fill(fill)
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .border(1.0, border_c)
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| s.toggle_keyboard_key(key_id.clone()))
                    .child(
                        text(cap.label.clone())
                            .font(lbl_font)
                            .color(txt_c)
                            .truncate(),
                    ),
            );
        }
        section = section.child(krow);
    }
    section
}

fn format_thousands(n: i32) -> String {
    let s = n.to_string();
    let mut out = String::new();
    for (idx, ch) in s.chars().rev().enumerate() {
        if idx > 0 && idx % 3 == 0 {
            out.push(',');
        }
        out.push(ch);
    }
    out.chars().rev().collect()
}

fn format_backup_time(iso: &str) -> String {
    let bytes = iso.as_bytes();
    if bytes.len() >= 16
        && bytes[4] == b'-'
        && bytes[7] == b'-'
        && let (Ok(y), Ok(m), Ok(d), Ok(hr), Ok(min)) = (
            iso[0..4].parse::<u32>(),
            iso[5..7].parse::<usize>(),
            iso[8..10].parse::<u32>(),
            iso[11..13].parse::<u32>(),
            iso[14..16].parse::<u32>(),
        )
        && (1..=12).contains(&m)
    {
        const MONTHS: [&str; 12] = [
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
        ];
        let h12 = if hr % 12 == 0 { 12 } else { hr % 12 };
        let ampm = if hr < 12 { "AM" } else { "PM" };
        return format!("{} {d}, {y}, {h12}:{min:02} {ampm}", MONTHS[m - 1]);
    }
    iso.get(..16).unwrap_or(iso).replace('T', " ")
}

fn kb_slider(key: &str, label: &str, width: f32, ratio: f32, disabled: bool) -> El {
    let clamped = ratio.clamp(0.0, 1.0);
    let knob = 12.0_f32;
    let left_w = ((width - knob).max(0.0) * clamped).max(0.0);
    row()
        .key(key)
        .role("slider")
        .label(label)
        .focusable(!disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .opacity(if disabled { 0.45 } else { 1.0 })
        .size(width, 20.0)
        .center_y()
        .child(el().size(left_w, 4.0).radius(2.0).fill(FG))
        .child(el().size(knob, knob).radius(knob / 2.0).fill(FG))
        .child(
            el().grow(1.0)
                .min_w(0.0)
                .h(4.0)
                .radius(2.0)
                .fill(hairline()),
        )
}

fn kb_tag(label: String, tone: BadgeTone) -> El {
    status_badge(label, tone)
}

fn wooting_key_map_panel(s: &AppState, dev: &PeripheralCard, kb: &KeyboardReadReply) -> El {
    let spec = build_keyboard_layout(
        kb.form_factor.as_deref(),
        kb.layout_code.as_deref(),
        kb.model.as_deref().or(dev.model.as_deref()),
        kb.layout.as_deref().or(dev.layout.as_deref()),
    );
    let rt_on = kb.has_rapid_trigger && kb.rapid_trigger.unwrap_or(false);
    let selected_id = s.pc.keyboard_selected_key.as_deref();

    let mut sections = row()
        .w_full()
        .gap(10.0)
        .items_end()
        .child(render_key_section(
            &spec.main_rows,
            15.0,
            rt_on,
            selected_id,
        ));
    if !spec.nav_rows.is_empty() {
        sections = sections.child(render_key_section(&spec.nav_rows, 3.0, rt_on, selected_id));
    }
    if !spec.num_rows.is_empty() {
        sections = sections.child(render_key_section(&spec.num_rows, 4.0, rt_on, selected_id));
    }

    let active_prof = kb
        .profiles
        .iter()
        .find(|p| p.index == kb.active_profile)
        .map(|p| p.name.clone());
    let min_act = kb.min_actuation_mm.unwrap_or(0.1);
    let max_act = kb.max_actuation_mm.unwrap_or(4.0);
    let mut tags = row().w_full().flex_wrap().gap(6.0);
    if let Some(ref pname) = active_prof {
        tags = tags.child(kb_tag(pname.clone(), BadgeTone::Neutral));
    }
    if let Some(a) = kb.actuation_mm {
        tags = tags.child(kb_tag(format!("Actuation {a:.1} mm"), BadgeTone::Neutral));
    }
    if kb.has_rapid_trigger
        && let Some(rt) = kb.rapid_trigger
    {
        let lbl = if rt {
            match kb.rapid_trigger_mm {
                Some(mm) => format!("Rapid Trigger {mm:.2} mm"),
                None => "Rapid Trigger On".into(),
            }
        } else {
            "Rapid Trigger Off".into()
        };
        tags = tags.child(kb_tag(
            lbl,
            if rt {
                BadgeTone::Good
            } else {
                BadgeTone::Neutral
            },
        ));
    }
    if let Some(tach) = kb.tachyon {
        let lbl = if tach {
            format!(
                "Tachyon · {} Hz",
                format_thousands(kb.max_scan_hz.unwrap_or(1000))
            )
        } else {
            "Tachyon Off".into()
        };
        tags = tags.child(kb_tag(lbl, BadgeTone::Neutral));
    }
    if kb.has_rgb
        && let Some(rgb) = kb.rgb_brightness
    {
        tags = tags.child(kb_tag(format!("Brightness {rgb}%"), BadgeTone::Neutral));
    }

    let travel_bar = kb.actuation_mm.map(|a| {
        let ratio = if max_act > min_act {
            ((a - min_act) / (max_act - min_act)).clamp(0.0, 1.0) as f32
        } else {
            0.0
        };
        row()
            .w_full()
            .gap(10.0)
            .center_y()
            .child(
                row()
                    .grow(1.0)
                    .min_w(0.0)
                    .h(6.0)
                    .radius(3.0)
                    .clip_rounded(3.0)
                    .fill(FG.alpha(0.10))
                    .child(el().grow(ratio.max(0.005)).h(6.0).fill(FG))
                    .child(el().grow((1.0 - ratio).max(0.005)).h(6.0)),
            )
            .child(
                text(format!("{min_act:.1}–{max_act:.1} mm"))
                    .font(type_label())
                    .color(fg_faint()),
            )
    });

    let selected_cap = selected_id.and_then(|id| find_key(&spec, id));
    let note_copy = match selected_cap {
        None => "Click any key to inspect active profile settings. Global actuation applies to all keys; open Wootility for per-key DKS, remaps, and RGB layers.".to_string(),
        Some(cap) if cap.kind == KeyKind::LedBar => format!(
            "{}. Brightness follows the active profile lighting setting; configure reactive effects in Wootility.",
            cap.note.as_deref().unwrap_or(&cap.label)
        ),
        Some(cap) => {
            let mm_text = kb
                .actuation_mm
                .map(|a| format!("{a:.1} mm"))
                .unwrap_or_else(|| "default".into());
            let rt_text = if rt_on {
                match kb.rapid_trigger_mm {
                    Some(rtm) => format!(" · Rapid Trigger {rtm:.2} mm"),
                    None => " · Rapid Trigger on".into(),
                }
            } else {
                String::new()
            };
            let prefix = cap.note.as_deref().unwrap_or(&cap.label);
            let prof = active_prof.as_deref().unwrap_or("the active profile");
            format!(
                "{prefix} uses {prof} global actuation ({mm_text}{rt_text}). Open Wootility for per-key DKS and remaps."
            )
        }
    };

    let note_el = if selected_cap.is_some() {
        col().key("kb-key-inspect").w_full().min_w(0.0).child(
            text(note_copy)
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        )
    } else {
        col().w_full().min_w(0.0).child(
            text(note_copy)
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        )
    };

    amoled_card(RADIUS_PANEL)
        .key("kb-matrix")
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(12.0)
        .gap(10.0)
        .child(text("Keyboard map").font(type_section()).color(FG))
        .child(
            col()
                .w_full()
                .pt(17.0)
                .pb(8.0)
                .gap(12.0)
                .child(
                    el().w_full()
                        .padding(12.0)
                        .radius(RADIUS_CARD)
                        .fill(FG.alpha(0.03))
                        .border(1.0, FG.alpha(0.12))
                        .child(sections),
                )
                .child(tags)
                .children(travel_bar)
                .child(note_el),
        )
}

fn wooting_keyboard_page(
    s: &AppState,
    dev: &PeripheralCard,
    display_name: String,
    status_line: String,
    scale: f32,
) -> El {
    let kb_opt = s.pc.keyboard.as_ref().filter(|k| k.ok);
    let hero_img =
        s.pc.device_images
            .get(&dev.id)
            .and_then(|u| s.device_image(u, scale));
    let media = if let Some(photo) = hero_img {
        el().w_full()
            .h_full()
            .pixels_fit(photo.pixels(), SurfaceFit::Fit)
    } else {
        icon_keyboard(56.0, fg_faint())
    };

    let mut stats = Vec::new();
    if let Some(kb) = kb_opt {
        if let Some(p) = kb.profiles.iter().find(|p| p.index == kb.active_profile) {
            stats.push(("Profile", p.name.clone()));
        }
        if let Some(a) = kb.actuation_mm {
            stats.push(("Actuation", format!("{a:.1} mm")));
        }
        if kb.has_rapid_trigger
            && let Some(rt) = kb.rapid_trigger
        {
            let v = if !rt {
                "Off".into()
            } else if let Some(mm) = kb.rapid_trigger_mm {
                format!("{mm:.2} mm")
            } else {
                "On".into()
            };
            stats.push(("Rapid Trigger", v));
        }
        if let Some(hz) = kb.max_scan_hz
            && hz > 0
        {
            stats.push(("Scan rate", format!("{} Hz", format_thousands(hz))));
        }
        if let Some(ref fw) = kb.firmware
            && !fw.is_empty()
        {
            stats.push(("Firmware", fw.clone()));
        }
        if let Some(ref lay) = kb.layout
            && !lay.is_empty()
        {
            stats.push(("Layout", lay.clone()));
        }
    }

    let mut col_a = Vec::new();
    let mut col_b = Vec::new();

    if let Some(kb) = kb_opt {
        let min_act = kb.min_actuation_mm.unwrap_or(0.1);
        let max_act = kb.max_actuation_mm.unwrap_or(4.0);
        let act = kb.actuation_mm.unwrap_or(min_act).clamp(min_act, max_act);
        let rt_on = kb.has_rapid_trigger && kb.rapid_trigger.unwrap_or(false);
        let rt_mm = kb.rapid_trigger_mm.unwrap_or(0.15).clamp(0.10, 2.35);
        let rgb = kb.rgb_brightness.unwrap_or(100).clamp(0, 100);
        let can_write = kb.can_write;

        if let Some(ref err) = s.pc.keyboard_error {
            col_a.push(
                amoled_card(RADIUS_PANEL)
                    .key("kb-write-error")
                    .w_full()
                    .px(20.0)
                    .py(14.0)
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .gap(12.0)
                            .child(
                                col()
                                    .grow(1.0)
                                    .min_w(0.0)
                                    .gap(2.0)
                                    .child(
                                        text("Keyboard change didn't apply")
                                            .font(type_body())
                                            .color(bad()),
                                    )
                                    .child(
                                        text(err.clone())
                                            .font(type_small())
                                            .color(fg_muted())
                                            .wrap(),
                                    ),
                            )
                            .child(
                                device_btn("kb-reread", "Re-read", false)
                                    .on_press(|s: &mut AppState| s.reload_keyboard()),
                            ),
                    ),
            );
        }

        if !can_write {
            col_a.push(
                amoled_card(RADIUS_PANEL)
                    .key("kb-readonly-banner")
                    .w_full()
                    .px(20.0)
                    .py(12.0)
                    .child(
                        text(
                            kb.message.clone().unwrap_or_else(|| {
                                "Read-only mode — close Wootility to edit.".into()
                            }),
                        )
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                    ),
            );
        }

        if !kb.profiles.is_empty() {
            let tabs = row()
                .w_full()
                .flex_wrap()
                .gap(6.0)
                .children(kb.profiles.iter().map(|p| {
                    let idx = p.index;
                    let active = kb.active_profile == idx;
                    let key = match idx {
                        0 => "kb-prof-0",
                        1 => "kb-prof-1",
                        2 => "kb-prof-2",
                        _ => "kb-prof-3",
                    };
                    let mut tab = row()
                        .key(key)
                        .role("tab")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .cursor(Cursor::Pointer)
                        .h(34.0)
                        .px(12.0)
                        .gap(6.0)
                        .center_y()
                        .radius(8.0)
                        .fill(if active {
                            FG.alpha(0.08)
                        } else {
                            FG.alpha(0.02)
                        })
                        .border(
                            1.0,
                            if active {
                                FG.alpha(0.45)
                            } else {
                                card_border()
                            },
                        )
                        .hover(hover_fill())
                        .motion(SPRING_QUICK)
                        .child(
                            text(format!("P{}", idx + 1))
                                .font(type_small())
                                .color(fg_muted()),
                        )
                        .child(text(p.name.clone()).font(type_control()).color(FG));
                    if !active {
                        tab = tab.on_press(move |s: &mut AppState| s.switch_keyboard_profile(idx));
                    }
                    tab
                }));
            col_a.push(device_panel(
                "Profiles",
                vec![
                    col()
                        .w_full()
                        .py(if rt_on { 10.0 } else { 18.0 })
                        .gap(if rt_on { 10.0 } else { 12.0 })
                        .child(
                            col()
                                .gap(2.0)
                                .child(text("Onboard profiles").font(type_body()).color(FG))
                                .child(
                                    text("Switch active slot stored on keyboard memory")
                                        .font(type_small())
                                        .color(fg_muted()),
                                ),
                        )
                        .child(tabs),
                ],
            ));
        }

        if kb.actuation_mm.is_some() {
            let act_ratio = if max_act > min_act {
                ((act - min_act) / (max_act - min_act)).clamp(0.0, 1.0) as f32
            } else {
                0.0
            };
            let act_slider = kb_slider(
                "kb-act-slider",
                "Actuation point",
                180.0,
                act_ratio,
                !can_write,
            )
            .on_drag(move |s: &mut AppState, d, _| {
                if !can_write || d.phase != Phase::Up || d.cancelled() {
                    return;
                }
                let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0) as f64;
                let stepped = ((min_act + r * (max_act - min_act)) * 10.0).round() / 10.0;
                s.update_keyboard_settings(
                    Some(stepped.clamp(min_act, max_act)),
                    None,
                    None,
                    None,
                    None,
                );
            });
            let act_slider = exo_kit::slider_range(
                act_slider,
                act,
                min_act,
                max_act,
                0.1,
                can_write,
                move |s: &mut AppState, v| {
                    let stepped = ((v * 10.0).round() / 10.0).clamp(min_act, max_act);
                    s.update_keyboard_settings(Some(stepped), None, None, None, None);
                },
            );
            let mut act_readout = el()
                .key("kb-act-up")
                .hit()
                .cursor(if can_write {
                    Cursor::Pointer
                } else {
                    Cursor::Default
                })
                .child(
                    text(format!("{act:.1} mm"))
                        .font(type_small())
                        .color(fg_muted()),
                );
            if can_write {
                act_readout = act_readout.on_press(move |s: &mut AppState| {
                    s.update_keyboard_settings(
                        Some((act + 0.1).min(max_act)),
                        None,
                        None,
                        None,
                        None,
                    );
                });
            }

            let presets: &[(f64, &str)] = if min_act >= 1.0 {
                &[
                    (1.5, "1.5 mm"),
                    (2.0, "2.0 mm"),
                    (2.8, "2.8 mm"),
                    (3.6, "3.6 mm"),
                ]
            } else {
                &[
                    (0.1, "0.1 mm"),
                    (0.4, "0.4 mm"),
                    (1.2, "1.2 mm"),
                    (2.0, "2.0 mm"),
                ]
            };
            let preset_row =
                row()
                    .gap(6.0)
                    .flex_wrap()
                    .children(presets.iter().map(|&(val, lbl)| {
                        let on = (act - val).abs() < 0.05;
                        let mut pill = filter_pill(format!("kb-act-p-{lbl}"), lbl, on);
                        if can_write {
                            pill = pill.on_press(move |s: &mut AppState| {
                                s.update_keyboard_settings(Some(val), None, None, None, None);
                            });
                        } else {
                            pill = pill.cursor(Cursor::NotAllowed).opacity(0.45);
                        }
                        pill
                    }));

            col_a.push(device_panel(
                "Actuation",
                vec![
                    device_row(
                        "Actuation point",
                        None,
                        None,
                        Some(
                            row()
                                .gap(10.0)
                                .center_y()
                                .child(act_slider)
                                .child(act_readout),
                        ),
                    ),
                    device_row(
                        "Presets",
                        Some("Instant global travel presets".into()),
                        None,
                        Some(preset_row),
                    ),
                ],
            ));
        }

        if kb.has_rapid_trigger && kb.rapid_trigger.is_some() {
            let mut rt_rows = vec![device_row(
                "Reset a key the instant it lifts",
                None,
                None,
                Some(
                    device_switch("kb-rt-toggle", "Rapid Trigger", rt_on, !can_write).on_press(
                        move |s: &mut AppState| {
                            if can_write {
                                s.update_keyboard_settings(None, Some(!rt_on), None, None, None);
                            }
                        },
                    ),
                ),
            )];
            if rt_on && kb.rapid_trigger_mm.is_some() {
                let rt_ratio = ((rt_mm - 0.10) / (2.35 - 0.10)).clamp(0.0, 1.0) as f32;
                let rt_slider =
                    kb_slider("kb-rt-slider", "Sensitivity", 180.0, rt_ratio, !can_write).on_drag(
                        move |s: &mut AppState, d, _| {
                            if !can_write || d.phase != Phase::Up || d.cancelled() {
                                return;
                            }
                            let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0) as f64;
                            let stepped = ((0.10 + r * (2.35 - 0.10)) * 20.0).round() / 20.0;
                            s.update_keyboard_settings(
                                None,
                                None,
                                Some(stepped.clamp(0.10, 2.35)),
                                None,
                                None,
                            );
                        },
                    );
                let rt_slider = exo_kit::slider_range(
                    rt_slider,
                    rt_mm,
                    0.10,
                    2.35,
                    0.05,
                    can_write,
                    |s: &mut AppState, v| {
                        let stepped = ((v * 20.0).round() / 20.0).clamp(0.10, 2.35);
                        s.update_keyboard_settings(None, None, Some(stepped), None, None);
                    },
                );
                let mut rt_readout = el()
                    .key("kb-rt-up")
                    .hit()
                    .cursor(if can_write {
                        Cursor::Pointer
                    } else {
                        Cursor::Default
                    })
                    .child(
                        text(format!("{rt_mm:.2} mm"))
                            .font(type_small())
                            .color(fg_muted()),
                    );
                if can_write {
                    rt_readout = rt_readout.on_press(move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            None,
                            None,
                            Some((rt_mm + 0.05).min(2.35)),
                            None,
                            None,
                        );
                    });
                }
                rt_rows.push(device_row(
                    "Sensitivity",
                    None,
                    None,
                    Some(
                        row()
                            .gap(10.0)
                            .center_y()
                            .child(rt_slider)
                            .child(rt_readout),
                    ),
                ));
            }
            col_a.push(device_panel("Rapid Trigger", rt_rows));
        }

        if kb.has_rgb && kb.rgb_brightness.is_some() {
            let rgb_slider = kb_slider(
                "kb-rgb-slider",
                "RGB brightness",
                180.0,
                rgb as f32 / 100.0,
                !can_write,
            )
            .on_drag(move |s: &mut AppState, d, _| {
                if !can_write || d.phase != Phase::Up || d.cancelled() {
                    return;
                }
                let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
                let stepped = ((r * 20.0).round() as i32 * 5).clamp(0, 100);
                s.update_keyboard_settings(None, None, None, None, Some(stepped));
            });
            let rgb_slider = exo_kit::slider_range(
                rgb_slider,
                f64::from(rgb),
                0.0,
                100.0,
                5.0,
                can_write,
                |s: &mut AppState, v| {
                    let stepped = ((v / 5.0).round() as i32 * 5).clamp(0, 100);
                    s.update_keyboard_settings(None, None, None, None, Some(stepped));
                },
            );
            let mut rgb_label = el()
                .key("kb-rgb-down")
                .hit()
                .child(text("RGB brightness").font(type_body()).color(FG));
            if can_write {
                rgb_label = rgb_label.on_press(move |s: &mut AppState| {
                    s.update_keyboard_settings(None, None, None, None, Some((rgb - 5).max(0)));
                });
            }
            let mut rgb_readout = el()
                .key("kb-rgb-up")
                .hit()
                .child(text(format!("{rgb}%")).font(type_small()).color(fg_muted()));
            if can_write {
                rgb_readout = rgb_readout.on_press(move |s: &mut AppState| {
                    s.update_keyboard_settings(None, None, None, None, Some((rgb + 5).min(100)));
                });
            }
            col_a.push(device_panel(
                "Lighting",
                vec![
                    row()
                        .w_full()
                        .min_h(if rt_on { 44.0 } else { 68.0 })
                        .py(if rt_on { 12.0 } else { 15.0 })
                        .justify_between()
                        .center_y()
                        .gap(12.0)
                        .child(rgb_label)
                        .child(
                            row()
                                .gap(10.0)
                                .center_y()
                                .child(rgb_slider)
                                .child(rgb_readout),
                        ),
                ],
            ));
        }

        col_b.push(wooting_key_map_panel(s, dev, kb));

        let mut perf_rows = Vec::new();
        if let Some(tach) = kb.tachyon {
            let tach_detail = if kb.max_scan_hz.unwrap_or(1000) >= 8000 {
                "Enables 8,000 Hz ultra-low latency scanning"
            } else {
                "Prioritizes minimum key scan latency over RGB animations"
            };
            perf_rows.push(device_row(
                "Tachyon mode",
                Some(tach_detail.into()),
                None,
                Some(
                    device_switch("kb-tach-toggle", "Tachyon mode", tach, !can_write).on_press(
                        move |s: &mut AppState| {
                            if can_write {
                                s.update_keyboard_settings(None, None, None, Some(!tach), None);
                            }
                        },
                    ),
                ),
            ));
        }
        if let Some(ref port) = dev.port {
            let badge = port_short(port).unwrap_or_else(|| "USB".into());
            perf_rows.push(device_row(
                "USB port",
                Some(port_words(port)),
                None,
                Some(text(badge).font(type_small()).color(fg_muted())),
            ));
        }
        let woot_detail = if kb.background_service_running {
            "Wooting Background Service running · Open for per-key DKS, remap & RGB"
        } else {
            "Open for per-key DKS, remapping & RGB layers"
        };
        perf_rows.push(device_row(
            "Wootility",
            Some(woot_detail.into()),
            None,
            Some(
                device_icon_btn(
                    "kb-open-wootility",
                    if kb.wootility_installed {
                        "Open Wootility"
                    } else {
                        "Wootility Web"
                    },
                    Some(icon_external(14.0, FG)),
                    false,
                )
                .on_press(|s: &mut AppState| s.open_keyboard_app()),
            ),
        ));
        col_b.push(device_panel("Performance", perf_rows));

        let mut backup_rows = vec![device_row(
            "Save snapshot",
            Some("Stores all onboard profiles locally for this keyboard".into()),
            None,
            Some(if can_write {
                primary_button("kb-backup", "Save backup", None)
                    .on_press(|s: &mut AppState| s.backup_keyboard_profiles())
            } else {
                device_btn("kb-backup", "Save backup", true)
            }),
        )];
        if s.pc.keyboard_backups.is_empty() {
            backup_rows.push(
                el().w_full().py(10.0).child(
                    text("No backups saved yet. A safety backup is created automatically before Exo writes settings.")
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                ),
            );
        } else {
            for (idx, bk) in s.pc.keyboard_backups.iter().take(6).enumerate() {
                let fname = bk.file_name.clone();
                let prof_word = if bk.profile_count == 1 {
                    "profile"
                } else {
                    "profiles"
                };
                let key = if idx == 0 {
                    "kb-restore-latest".to_string()
                } else {
                    format!("kb-restore-{idx}")
                };
                backup_rows.push(device_row(
                    format_backup_time(&bk.created_utc),
                    Some(format!("{} · {} {prof_word}", bk.model, bk.profile_count)),
                    None,
                    Some(device_btn(key, "Restore", !can_write).on_press(
                        move |s: &mut AppState| {
                            if can_write {
                                s.restore_keyboard_backup(fname.clone());
                            }
                        },
                    )),
                ));
            }
        }
        col_b.push(device_panel("Backups", backup_rows));
    } else if s.pc.keyboard.is_some() || s.pc.keyboard_error.is_some() {
        let err_msg =
            s.pc.keyboard_error
                .clone()
                .or_else(|| s.pc.keyboard.as_ref().and_then(|k| k.message.clone()))
                .unwrap_or_else(|| "Could not read Wooting keyboard.".into());
        col_a.push(
            amoled_card(RADIUS_PANEL)
                .key("kb-error-card")
                .w_full()
                .px(20.0)
                .py(18.0)
                .gap(12.0)
                .child(text("Keyboard").font(type_section()).color(FG))
                .child(device_row(
                    "Couldn't read keyboard",
                    Some(err_msg),
                    None,
                    Some(
                        device_btn("kb-retry", "Retry", false)
                            .on_press(|s: &mut AppState| s.reload_keyboard()),
                    ),
                )),
        );
    } else {
        col_a.push(
            amoled_card(RADIUS_PANEL).w_full().px(20.0).py(18.0).child(
                text("Reading keyboard…")
                    .font(type_body())
                    .color(fg_muted()),
            ),
        );
    }

    device_page_shell_columns_no_rescan(
        DevicePageHeader {
            key: "room-devices-detail",
            name: kb_opt.and_then(|k| k.model.clone()).unwrap_or(display_name),
            status: status_line,
            media,
            stats,
            credit: Some("Direct Wooting USB HID protocol"),
        },
        col_a,
        col_b,
        |s| s.pc.selected_device_id = None,
    )
}

pub(crate) fn devices_room(s: &AppState, scale: f32) -> El {
    if s.pc.mouse_panel_open {
        let mut slot = el()
            .key("mouse-panel-slot")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .fill(BG)
            .center()
            .on_layout(|s: &mut AppState, rect| s.update_mouse_panel_layout(rect));

        if let Some(ref err) = s.pc.mouse_panel_error {
            slot = slot.child(
                col()
                    .center()
                    .gap(12.0)
                    .px(24.0)
                    .child(
                        text("Exo could not reach your mouse")
                            .font(type_title())
                            .color(FG),
                    )
                    .child(text(err.clone()).font(type_body()).color(fg_muted()))
                    .child(
                        device_btn("mouse-panel-retry", "Try again", false)
                            .on_press(|s: &mut AppState| s.retry_mouse_panel()),
                    ),
            );
        } else if s.pc.mouse_panel_loading {
            slot = slot.child(
                text("Looking for your mouse")
                    .font(type_body())
                    .color(fg_muted()),
            );
        }

        return col()
            .key("room-devices-mouse")
            .w_full()
            .h_full()
            .fill(BG)
            .child(
                row().w_full().pt(66.0).px(10.0).pb(0.0).center_y().child(
                    device_icon_btn(
                        "dev-back",
                        "Devices",
                        Some(icon_chevron_left(14.0, FG)),
                        false,
                    )
                    .on_press(|s: &mut AppState| s.close_mouse_panel()),
                ),
            )
            .child(slot);
    }

    let devices =
        s.pc.devices
            .as_ref()
            .map(|d| d.devices.as_slice())
            .unwrap_or(&[]);

    if let Some(sel_id) = s.pc.selected_device_id.as_deref()
        && let Some(dev) = devices.iter().find(|d| d.id == sel_id)
    {
        let display_name = clean_device_name(dev, s);
        let status_line = match dev.battery {
            Some(b) => format!(
                "{} · {}%",
                connection_label(dev.connection.as_deref()),
                format_battery_pct(b)
            ),
            None => connection_label(dev.connection.as_deref()).to_string(),
        };

        if dev.kind == "mouse" {
            return crate::devices_mouse::mouse_page(s, dev, display_name, status_line, scale);
        }

        if dev.kind == "audio" {
            let output = devices
                .iter()
                .find(|d| d.kind == "audio" && d.role.as_deref() == Some("output"))
                .or_else(|| {
                    devices
                        .iter()
                        .find(|d| d.kind == "audio" && d.role.as_deref() != Some("input"))
                });
            let mic = devices
                .iter()
                .find(|d| d.kind == "audio" && d.role.as_deref() == Some("input"));
            return crate::devices_audio::audio_page(
                s,
                output,
                mic,
                display_name,
                status_line,
                scale,
            );
        }

        if dev.kind == "controller" && dev.lab {
            return crate::devices_controller::controller_page(
                s,
                dev,
                display_name,
                status_line,
                scale,
            );
        }

        if dev.kind == "keyboard" && dev.lab {
            return wooting_keyboard_page(s, dev, display_name, status_line, scale);
        }

        let mut page = col()
            .key("room-devices-detail")
            .w_full()
            .h_full()
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .chain_scroll_offset(crate::shell::bar_scroll_watch)
            .pt(60.0)
            .px(16.0)
            .pb(36.0)
            .gap(16.0)
            .child(
                row().w_full().center_y().child(
                    device_icon_btn(
                        "dev-back",
                        "Devices",
                        Some(icon_chevron_left(14.0, FG)),
                        false,
                    )
                    .on_press(|s: &mut AppState| s.pc.selected_device_id = None),
                ),
            )
            .child(
                text(dev.name.clone())
                    .font(TextStyle::new(22.0, 650.0))
                    .color(FG),
            );

        page = page.child(
            amoled_card(RADIUS_PANEL)
                .w_full()
                .px(20.0)
                .py(18.0)
                .gap(8.0)
                .child(
                    text(connection_label(dev.connection.as_deref()))
                        .font(type_section())
                        .color(FG),
                )
                .children(dev.battery.map(|b| {
                    text(format!("Battery: {}%", format_battery_pct(b)))
                        .font(type_body())
                        .color(fg_muted())
                }))
                .children(dev.polling_hz.map(|hz| {
                    text(format!("Polling rate: {hz} Hz"))
                        .font(type_body())
                        .color(fg_muted())
                })),
        );
        return page;
    }

    let header = row()
        .w_full()
        .h(32.0)
        .justify_between()
        .center_y()
        .child(text("Devices").font(type_title()).color(FG))
        .child(
            row().gap(8.0).center_y().child(
                device_icon_btn("dev-rescan", "Rescan", Some(icon_refresh(14.0, FG)), false)
                    .on_press(|s: &mut AppState| s.load_pc_devices()),
            ),
        );

    let mut ordered: Vec<&PeripheralCard> = Vec::new();
    for kind in ["mouse", "keyboard", "audio", "controller"] {
        let mut added_audio = false;
        for dev in devices {
            if dev.kind != kind {
                continue;
            }
            if (kind == "keyboard" || kind == "controller") && !dev.lab {
                continue;
            }
            if kind == "audio" {
                if added_audio || dev.role.as_deref() == Some("input") {
                    continue;
                }
                added_audio = true;
            }
            ordered.push(dev);
        }
        if kind == "audio"
            && !added_audio
            && let Some(mic) = devices
                .iter()
                .find(|d| d.kind == "audio" && d.role.as_deref() == Some("input"))
        {
            ordered.push(mic);
        }
    }

    let grid = match ordered.len() {
        1 => col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .child(device_showcase_card(ordered[0], s, scale)),
        2 => row()
            .w_full()
            .grow(1.0)
            .min_h(160.0)
            .gap(12.0)
            .child(device_showcase_card(ordered[0], s, scale))
            .child(device_showcase_card(ordered[1], s, scale)),
        3 => row()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(device_showcase_card_sized(
                ordered[0], s, scale, 750.0, 500.0,
            ))
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .h_full()
                    .gap(12.0)
                    .child(
                        el().w_full()
                            .grow(1.0)
                            .min_h(160.0)
                            .child(device_showcase_card(ordered[1], s, scale)),
                    )
                    .child(
                        el().w_full()
                            .grow(1.0)
                            .min_h(160.0)
                            .child(device_showcase_card(ordered[2], s, scale)),
                    ),
            ),
        5 => col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(
                row()
                    .w_full()
                    .grow(1.0)
                    .min_h(160.0)
                    .gap(12.0)
                    .child(device_showcase_card(ordered[0], s, scale))
                    .child(device_showcase_card(ordered[1], s, scale))
                    .child(device_showcase_card(ordered[2], s, scale)),
            )
            .child(
                row()
                    .w_full()
                    .grow(1.0)
                    .min_h(160.0)
                    .gap(12.0)
                    .child(device_showcase_card(ordered[3], s, scale))
                    .child(device_showcase_card(ordered[4], s, scale)),
            ),
        6 => col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(
                row()
                    .w_full()
                    .grow(1.0)
                    .min_h(160.0)
                    .gap(12.0)
                    .child(device_showcase_card(ordered[0], s, scale))
                    .child(device_showcase_card(ordered[1], s, scale))
                    .child(device_showcase_card(ordered[2], s, scale)),
            )
            .child(
                row()
                    .w_full()
                    .grow(1.0)
                    .min_h(160.0)
                    .gap(12.0)
                    .child(device_showcase_card(ordered[3], s, scale))
                    .child(device_showcase_card(ordered[4], s, scale))
                    .child(device_showcase_card(ordered[5], s, scale)),
            ),
        _ => {
            let mut g = col().w_full().grow(1.0).min_h(0.0).gap(12.0);
            for pair in ordered.chunks(2) {
                let mut r = row().w_full().grow(1.0).min_h(160.0).gap(12.0);
                for dev in pair {
                    r = r.child(device_showcase_card(dev, s, scale));
                }
                if pair.len() == 1 {
                    r = r.child(el().grow(1.0).min_w(0.0));
                }
                g = g.child(r);
            }
            g
        }
    };

    let mut page = col()
        .key("room-devices")
        .w_full()
        .h_full()
        .pt(68.0)
        .px(24.0)
        .pb(24.0)
        .gap(16.0)
        .fill(BG)
        .child(header);

    if let Some(ref err) = s.pc.devices_error
        && !s.pc.devices_loading
        && !devices.is_empty()
    {
        page = page.child(
            amoled_card(RADIUS_PANEL)
                .key("devices-error-banner")
                .w_full()
                .px(16.0)
                .py(12.0)
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .gap(12.0)
                        .child(
                            text(format!("Device scan failed: {err}"))
                                .font(type_body())
                                .color(fg_muted())
                                .truncate(),
                        )
                        .child(
                            device_btn("devices-retry", "Try again", false)
                                .on_press(|s: &mut AppState| s.load_pc_devices()),
                        ),
                ),
        );
    }

    if devices.is_empty() {
        let empty_body = if s.pc.devices_loading {
            text("Scanning connected devices…")
                .font(type_section())
                .color(fg_muted())
        } else if let Some(ref err) = s.pc.devices_error {
            col()
                .center()
                .gap(12.0)
                .px(24.0)
                .child(text("Could not load devices").font(type_title()).color(FG))
                .child(text(err.clone()).font(type_body()).color(fg_muted()))
                .child(
                    device_btn("devices-retry", "Try again", false)
                        .on_press(|s: &mut AppState| s.load_pc_devices()),
                )
        } else {
            text("No peripherals detected yet.")
                .font(type_section())
                .color(fg_muted())
        };
        page = page.child(
            amoled_card(RADIUS_PANEL)
                .w_full()
                .grow(1.0)
                .center()
                .child(empty_body),
        );
    } else {
        page = page.child(grid);
    }

    page
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wireless_receiver_shows_the_mouse_it_belongs_to() {
        let s = AppState::default();
        let card = |name: &str| PeripheralCard {
            id: "hid:mouse".into(),
            kind: "mouse".into(),
            name: name.into(),
            maker: Some("Finalmouse".into()),
            connection: Some("wireless".into()),
            ..Default::default()
        };
        assert_eq!(
            clean_device_name(&card("StarlightX Dongle"), &s),
            "Starlight X"
        );
        assert_eq!(
            clean_device_name(&card("Finalmouse ULX Receiver"), &s),
            "ULX"
        );
        assert_eq!(
            clean_device_name(&card("Mystery Dongle"), &s),
            "Mystery Dongle"
        );
        assert_eq!(clean_device_name(&card("Starlight X"), &s), "Starlight X");
    }

    #[test]
    fn microphone_uses_mic_fallback_icon() {
        assert_eq!(device_fallback_icon_name("audio", Some("input")), "mic");
        assert_eq!(
            device_fallback_icon_name("audio", Some("output")),
            "headphones"
        );
        assert_eq!(device_fallback_icon_name("audio", None), "headphones");
        assert_eq!(device_fallback_icon_name("mouse", None), "mouse");
        assert_eq!(device_fallback_icon_name("keyboard", None), "keyboard");
        assert_eq!(device_fallback_icon_name("controller", None), "gamepad");
    }
}
