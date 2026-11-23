use crate::pc_state::{AppsSubTab, PcPartMenu, PcTab, format_bytes};
use crate::state::{AppState, Room, title_hue};
use exo_kit::{
    BG, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK, TRANSPARENT, amoled_card, bad,
    card_border, fg_faint, fg_muted, filter_pill, float_line, glass_button, glass_control_fill,
    glass_icon_circle, glass_panel_opt, glass_rim_bottom, glass_rim_top, good, hover_fill,
    icon_app_window, icon_check, icon_chevron_down, icon_chevron_left, icon_close, icon_cpu,
    icon_gamepad, icon_hard_drive, icon_headphones, icon_keyboard, icon_monitor, icon_mouse,
    icon_refresh, icon_search, icon_trash, icon_volume, icon_volume_off, popover_transition_from,
    pressed_fill, primary_button, progress_bar, scaled, surface_card, type_body, type_control,
    type_label, type_page, type_section, type_small, type_title, warn,
};
use exo_rpc::{DriveInfo, InstalledAppItem, KeyboardReadReply, PeripheralCard};
use rune::{
    Align, Cursor, El, Key, ScrollSpec, Side, SurfaceFit, TextStyle, Transition, col, edit_access,
    el, oklch, row, text, text_input,
};

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

fn device_fallback_icon(kind: &str) -> El {
    match kind {
        "mouse" => icon_mouse(48.0, fg_faint()),
        "keyboard" => icon_keyboard(48.0, fg_faint()),
        "controller" => icon_gamepad(48.0, fg_faint()),
        _ => icon_headphones(48.0, fg_faint()),
    }
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
    dev.name.clone()
}

fn format_battery_pct(b: f64) -> i32 {
    if (0.0..=1.0).contains(&b) {
        (b * 100.0).round() as i32
    } else {
        b.round() as i32
    }
}

fn device_showcase_card(dev: &PeripheralCard, s: &AppState, scale: f32) -> El {
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
            el().size(471.0, 314.0)
                .pixels_fit(photo.pixels(), SurfaceFit::Fit),
        )
    } else {
        el().w_full()
            .grow(1.0)
            .min_h(0.0)
            .center()
            .child(device_fallback_icon(&dev.kind))
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

    let dev_id = dev.id.clone();
    let is_mouse = dev.kind == "mouse";
    col()
        .key(format!("dev-card-{}", dev.id))
        .role("button")
        .label(display_name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(0.99)
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .pt(16.0)
        .px(20.0)
        .pb(18.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .hover(BG)
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| {
            if is_mouse {
                s.open_mouse_panel(dev_id.clone());
            } else {
                s.pc.selected_device_id = Some(dev_id.clone());
            }
        })
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
                        .child(text(sub).font(type_body()).color(fg_muted()).truncate())
                        .children(port_warn.map(|w| {
                            text(w)
                                .font(TextStyle::new(scaled(13.0), 600.0))
                                .color(bad())
                        })),
                ),
        )
}

fn wooting_key_matrix(kb: &KeyboardReadReply) -> El {
    let rows_spec: [&[(&str, f32)]; 5] = [
        &[
            ("Esc", 1.0),
            ("1", 1.0),
            ("2", 1.0),
            ("3", 1.0),
            ("4", 1.0),
            ("5", 1.0),
            ("6", 1.0),
            ("7", 1.0),
            ("8", 1.0),
            ("9", 1.0),
            ("0", 1.0),
            ("-", 1.0),
            ("=", 1.0),
            ("Bksp", 2.0),
        ],
        &[
            ("Tab", 1.5),
            ("Q", 1.0),
            ("W", 1.0),
            ("E", 1.0),
            ("R", 1.0),
            ("T", 1.0),
            ("Y", 1.0),
            ("U", 1.0),
            ("I", 1.0),
            ("O", 1.0),
            ("P", 1.0),
            ("[", 1.0),
            ("]", 1.0),
            ("\\", 1.5),
        ],
        &[
            ("Caps", 1.75),
            ("A", 1.0),
            ("S", 1.0),
            ("D", 1.0),
            ("F", 1.0),
            ("G", 1.0),
            ("H", 1.0),
            ("J", 1.0),
            ("K", 1.0),
            ("L", 1.0),
            (";", 1.0),
            ("'", 1.0),
            ("Enter", 2.25),
        ],
        &[
            ("Shift", 2.25),
            ("Z", 1.0),
            ("X", 1.0),
            ("C", 1.0),
            ("V", 1.0),
            ("B", 1.0),
            ("N", 1.0),
            ("M", 1.0),
            (",", 1.0),
            (".", 1.0),
            ("/", 1.0),
            ("Shift", 2.75),
        ],
        &[
            ("Ctrl", 1.25),
            ("Win", 1.25),
            ("Alt", 1.25),
            ("Space", 6.25),
            ("Alt", 1.25),
            ("Fn", 1.25),
            ("Menu", 1.25),
            ("Ctrl", 1.25),
        ],
    ];
    let wasd = ["W", "A", "S", "D"];
    let act = kb.actuation_mm.unwrap_or(0.4);
    let unit = 44.0;
    let gap = 4.0;

    let mut matrix = col().gap(gap);
    for r in rows_spec {
        let mut krow = row().gap(gap);
        for (lbl, u) in r {
            let is_hot = wasd.contains(lbl);
            let w = u * unit + (u - 1.0).max(0.0) * gap;
            let fill = if is_hot && kb.rapid_trigger {
                good().alpha(0.18)
            } else if is_hot {
                FG.alpha(0.14)
            } else {
                FG.alpha(0.04)
            };
            let border_c = if is_hot && kb.rapid_trigger {
                good().alpha(0.70)
            } else {
                card_border()
            };
            krow = krow.child(
                col()
                    .w(w)
                    .h(40.0)
                    .radius(7.0)
                    .center()
                    .gap(2.0)
                    .fill(fill)
                    .border(1.0, border_c)
                    .child(text(*lbl).font(type_small()).color(FG))
                    .children(is_hot.then(|| {
                        text(format!("{act:.1}"))
                            .font(type_label())
                            .color(if kb.rapid_trigger { good() } else { fg_muted() })
                    })),
            );
        }
        matrix = matrix.child(krow);
    }

    el().w_full()
        .px(16.0)
        .py(14.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, card_border())
        .child(matrix)
}

fn wooting_detail(_s: &AppState, kb: &KeyboardReadReply) -> El {
    let act = kb.actuation_mm.unwrap_or(0.4);
    let rt = kb.rapid_trigger;
    let tach = kb.tachyon;

    let profiles = row()
        .gap(6.0)
        .flex_wrap()
        .children(kb.profiles.iter().map(|p| {
            let idx = p.index;
            let active = kb.active_profile == idx;
            filter_pill(
                match idx {
                    0 => "kb-prof-0",
                    1 => "kb-prof-1",
                    2 => "kb-prof-2",
                    _ => "kb-prof-3",
                },
                p.name.clone(),
                active,
            )
            .on_press(move |s: &mut AppState| s.switch_keyboard_profile(idx))
        }));

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(16.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text(
                        kb.model
                            .clone()
                            .unwrap_or_else(|| "Wooting Hall-Effect".into()),
                    )
                    .font(type_title())
                    .color(FG),
                )
                .child(
                    glass_button("kb-backup", "Backup profiles", None)
                        .on_press(|s: &mut AppState| s.backup_keyboard_profiles()),
                ),
        )
        .child(profiles)
        .child(wooting_key_matrix(kb))
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            text(format!("Actuation {act:.1} mm"))
                                .font(type_control())
                                .color(FG),
                        )
                        .child(glass_button("kb-act-down", "−0.1", None).on_press(
                            move |s: &mut AppState| {
                                s.update_keyboard_settings(
                                    Some((act - 0.1).max(0.1)),
                                    None,
                                    None,
                                    None,
                                );
                            },
                        ))
                        .child(glass_button("kb-act-up", "+0.1", None).on_press(
                            move |s: &mut AppState| {
                                s.update_keyboard_settings(
                                    Some((act + 0.1).min(4.0)),
                                    None,
                                    None,
                                    None,
                                );
                            },
                        )),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            filter_pill("kb-rt-toggle", "Rapid Trigger".into(), rt).on_press(
                                move |s: &mut AppState| {
                                    s.update_keyboard_settings(None, Some(!rt), None, None);
                                },
                            ),
                        )
                        .child(
                            filter_pill("kb-tach-toggle", "Tachyon".into(), tach).on_press(
                                move |s: &mut AppState| {
                                    s.update_keyboard_settings(None, None, None, Some(!tach));
                                },
                            ),
                        ),
                ),
        )
}

fn audio_detail(s: &AppState) -> El {
    let Some(aud) = s.pc.audio.as_ref() else {
        return el();
    };
    let mut card = amoled_card(RADIUS_PANEL)
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(12.0)
        .child(text("Audio mixer").font(type_title()).color(FG));

    for (label, ep, is_out, key_mute, key_dn, key_up) in [
        (
            "Output",
            aud.output.as_ref(),
            true,
            "aud-out-mute",
            "aud-out-dn",
            "aud-out-up",
        ),
        (
            "Microphone",
            aud.mic.as_ref(),
            false,
            "aud-mic-mute",
            "aud-mic-dn",
            "aud-mic-up",
        ),
    ] {
        if let Some(ep) = ep {
            let vol = ep.volume.unwrap_or(80);
            let muted = ep.muted;
            card = card.child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(
                        col()
                            .gap(2.0)
                            .child(text(label).font(type_section()).color(FG))
                            .child(text(format!("{vol}%")).font(type_small()).color(fg_muted())),
                    )
                    .child(
                        row()
                            .gap(8.0)
                            .center_y()
                            .child(
                                glass_button(
                                    key_mute,
                                    if muted { "Muted" } else { "Mute" },
                                    Some(if muted {
                                        icon_volume_off(13.0, bad())
                                    } else {
                                        icon_volume(13.0, FG)
                                    }),
                                )
                                .on_press(move |s: &mut AppState| s.set_audio_mute(is_out, !muted)),
                            )
                            .child(glass_button(key_dn, "−10%", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_audio_volume(is_out, (vol - 10).max(0));
                                },
                            ))
                            .child(glass_button(key_up, "+10%", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_audio_volume(is_out, (vol + 10).min(100));
                                },
                            )),
                    ),
            );
        }
    }
    card
}

fn devices_room(s: &AppState, scale: f32) -> El {
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
                    .child(text("Exo Mouse unavailable").font(type_section()).color(FG))
                    .child(text(err.clone()).font(type_body()).color(fg_muted()))
                    .child(
                        glass_button("mouse-panel-retry", "Retry", Some(icon_refresh(14.0, FG)))
                            .on_press(|s: &mut AppState| s.retry_mouse_panel()),
                    ),
            );
        } else if s.pc.mouse_panel_loading {
            slot = slot.child(
                text("Opening Exo Mouse…")
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
                row().w_full().pt(60.0).px(GAP).pb(0.0).center_y().child(
                    glass_button("dev-back", "Devices", Some(icon_chevron_left(14.0, FG)))
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
        let mut page = col()
            .key("room-devices-detail")
            .w_full()
            .h_full()
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .pt(60.0)
            .px(16.0)
            .pb(36.0)
            .gap(16.0)
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(
                        glass_button("dev-back", "Devices", Some(icon_chevron_left(14.0, FG)))
                            .on_press(|s: &mut AppState| s.pc.selected_device_id = None),
                    )
                    .child(
                        glass_button("dev-rescan", "Rescan", Some(icon_refresh(14.0, FG)))
                            .on_press(|s: &mut AppState| s.load_pc_devices()),
                    ),
            )
            .child(
                text(dev.name.clone())
                    .font(TextStyle::new(22.0, 650.0))
                    .color(FG),
            );

        if dev.wooting || dev.kind == "keyboard" {
            if let Some(ref kb) = s.pc.keyboard {
                page = page.child(wooting_detail(s, kb));
            }
        } else if dev.kind == "audio" {
            page = page.child(audio_detail(s));
        } else {
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
        }
        return page;
    }

    let header = row()
        .w_full()
        .h(32.0)
        .justify_between()
        .center_y()
        .child(text("Devices").font(type_title()).color(FG))
        .child(
            glass_button("dev-rescan", "Rescan", Some(icon_refresh(14.0, FG)))
                .on_press(|s: &mut AppState| s.load_pc_devices()),
        );

    let mut ordered: Vec<&PeripheralCard> = Vec::new();
    for kind in ["mouse", "keyboard", "audio", "controller"] {
        for dev in devices {
            if dev.kind == kind {
                if kind == "audio" && dev.role.as_deref() == Some("input") {
                    continue;
                }
                ordered.push(dev);
            }
        }
    }
    for dev in devices {
        if !matches!(
            dev.kind.as_str(),
            "mouse" | "keyboard" | "audio" | "controller"
        ) {
            ordered.push(dev);
        }
    }

    let mut grid = col().w_full().grow(1.0).min_h(0.0).gap(12.0);
    for pair in ordered.chunks(2) {
        let mut r = row().w_full().grow(1.0).min_h(160.0).gap(12.0);
        for dev in pair {
            r = r.child(device_showcase_card(dev, s, scale));
        }
        if pair.len() == 1 {
            r = r.child(el().grow(1.0).min_w(0.0));
        }
        grid = grid.child(r);
    }

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

    if devices.is_empty() {
        page = page.child(
            amoled_card(RADIUS_PANEL).w_full().grow(1.0).center().child(
                text(if s.pc.devices_loading {
                    "Scanning connected devices…"
                } else {
                    "No peripherals detected yet."
                })
                .font(type_section())
                .color(fg_muted()),
            ),
        );
    } else {
        page = page.child(grid);
    }

    page
}

fn format_gb_short(gb: f64) -> String {
    if gb >= 1000.0 {
        format!("{:.1} TB", gb / 1024.0)
    } else {
        format!("{:.0} GB", gb.round().max(1.0))
    }
}

fn strip_trademarks(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    let chars: Vec<char> = raw.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '®' || chars[i] == '™' {
            i += 1;
            continue;
        }
        if chars[i] == '(' {
            if i + 2 < chars.len()
                && chars[i + 2] == ')'
                && matches!(chars[i + 1].to_ascii_lowercase(), 'r' | 'c')
            {
                i += 3;
                continue;
            }
            if i + 3 < chars.len()
                && chars[i + 3] == ')'
                && chars[i + 1].eq_ignore_ascii_case(&'t')
                && chars[i + 2].eq_ignore_ascii_case(&'m')
            {
                i += 4;
                continue;
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

fn is_numeric_token(s: &str) -> bool {
    !s.is_empty()
        && s.chars().all(|c| c.is_ascii_digit() || c == '.')
        && s.chars().any(|c| c.is_ascii_digit())
}

fn short_drive_model(raw: &str) -> String {
    let cleaned = strip_trademarks(raw);
    let mut parts = Vec::new();
    let tokens: Vec<&str> = cleaned.split_whitespace().collect();
    let mut i = 0;
    while i < tokens.len() {
        let t = tokens[i];
        let lower = t.to_ascii_lowercase();
        if lower == "with" && i + 1 < tokens.len() && tokens[i + 1].eq_ignore_ascii_case("heatsink")
        {
            i += 2;
            continue;
        }
        if lower == "solid"
            && i + 2 < tokens.len()
            && tokens[i + 1].eq_ignore_ascii_case("state")
            && tokens[i + 2].eq_ignore_ascii_case("drive")
        {
            i += 3;
            continue;
        }
        if matches!(lower.as_str(), "ssd" | "nvme" | "m.2" | "series") {
            i += 1;
            continue;
        }
        if (lower.ends_with("tb") || lower.ends_with("gb"))
            && lower.len() > 2
            && is_numeric_token(&lower[..lower.len() - 2])
        {
            i += 1;
            continue;
        }
        if is_numeric_token(&lower)
            && i + 1 < tokens.len()
            && matches!(tokens[i + 1].to_ascii_lowercase().as_str(), "tb" | "gb")
        {
            i += 2;
            continue;
        }
        parts.push(t);
        i += 1;
    }
    if parts.is_empty() {
        cleaned.trim().to_string()
    } else {
        parts.join(" ")
    }
}

fn short_windows_name(raw: &str) -> String {
    let trimmed = raw.trim();
    let lower = trimmed.to_ascii_lowercase();
    let base = if lower.starts_with("windows 11") {
        Some("Windows 11")
    } else if lower.starts_with("windows 10") {
        Some("Windows 10")
    } else {
        None
    };
    match base {
        Some(name) if lower.contains("insider") => format!("{name} Insider"),
        Some(name) => name.to_string(),
        None => trimmed.to_string(),
    }
}

fn format_windows_label(about: Option<&exo_rpc::SystemAbout>) -> String {
    let name = about
        .and_then(|a| a.windows.as_deref())
        .map(short_windows_name)
        .filter(|s| !s.is_empty());
    let version = about
        .and_then(|a| a.windows_version.as_deref())
        .map(str::trim)
        .filter(|s| !s.is_empty());
    match (name, version) {
        (Some(w), Some(v)) => format!("{w} {v}"),
        (Some(w), None) => w,
        _ => about
            .and_then(|a| a.os.as_deref())
            .map(short_windows_name)
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| "Windows 11".into()),
    }
}

fn drive_row(d: &DriveInfo) -> El {
    let (letter, free_gb, total_gb) = d
        .volumes
        .first()
        .map(|v| (v.letter.as_str(), v.free_gb, v.total_gb.max(1.0)))
        .unwrap_or(("C:", 0.0, 1.0));
    let used_pct = ((total_gb - free_gb) / total_gb).clamp(0.0, 1.0) as f32;

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(20.0)
        .py(16.0)
        .gap(10.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(icon_hard_drive(16.0, FG))
                        .child(
                            text(format!("{letter} {}", d.model))
                                .font(type_section())
                                .color(FG),
                        ),
                )
                .child(
                    text(format!(
                        "{} free of {}",
                        format_gb_short(free_gb),
                        format_gb_short(total_gb)
                    ))
                    .font(type_small())
                    .color(fg_muted()),
                ),
        )
        .child(progress_bar(420.0, used_pct))
}

const SHADER_ROUGH: f64 = 1.5;

#[derive(Debug, Clone, Copy)]
struct ShaderSpan {
    low: f64,
    high: f64,
    learned: bool,
}

fn shader_js_round(value: f64) -> i64 {
    (value + 0.5).floor() as i64
}

fn shader_span(est: &exo_rpc::ShaderEstimate, share: f64) -> ShaderSpan {
    let sure = est.learned && est.low_seconds.is_none();
    let low = est.low_seconds.unwrap_or(if sure {
        est.seconds
    } else {
        est.seconds / SHADER_ROUGH
    }) * share;
    let high = if est.low_seconds.is_some() || sure {
        est.seconds
    } else {
        est.seconds * SHADER_ROUGH
    } * share;
    ShaderSpan {
        low,
        high,
        learned: sure,
    }
}

fn shader_minute_text(minutes: i64) -> String {
    let h = minutes / 60;
    let m = minutes % 60;
    if h > 0 {
        if m > 0 {
            format!("{h} h {m} min")
        } else {
            format!("{h} h")
        }
    } else {
        format!("{m} min")
    }
}

fn shader_round_span(seconds: f64) -> String {
    if seconds < 60.0 {
        return "under a minute".into();
    }
    let minutes = seconds / 60.0;
    let step: i64 = if minutes < 10.0 {
        1
    } else if minutes < 60.0 {
        5
    } else if minutes < 600.0 {
        10
    } else {
        60
    };
    let rounded = (shader_js_round(minutes / step as f64) * step).max(step);
    shader_minute_text(rounded)
}

fn shader_range_text(low: f64, high: f64) -> String {
    if high < 60.0 {
        return "under a minute".into();
    }
    let minutes = high / 60.0;
    let step: i64 = if minutes < 10.0 {
        1
    } else if minutes < 60.0 {
        5
    } else if minutes < 600.0 {
        30
    } else {
        60
    };
    let top = (shader_js_round(minutes / step as f64) * step).max(step);
    let bottom = shader_js_round(low / 60.0 / step as f64) * step;
    if bottom <= 0 {
        return format!("up to {}", shader_minute_text(top));
    }
    if bottom >= top {
        return format!("about {}", shader_minute_text(top));
    }
    if top < 60 {
        return format!("{bottom}–{}", shader_minute_text(top));
    }
    if bottom % 60 == 0 && top % 60 == 0 {
        format!("{}–{} h", bottom / 60, top / 60)
    } else {
        format!("{}–{}", shader_minute_text(bottom), shader_minute_text(top))
    }
}

fn format_shader_estimate(est: &exo_rpc::ShaderEstimate) -> String {
    let span = shader_span(est, 1.0);
    if !span.learned {
        shader_range_text(span.low, span.high)
    } else if span.high < 60.0 {
        "under a minute".into()
    } else {
        format!("about {}", shader_round_span(span.high))
    }
}

fn resolve_shader_estimate(
    sg: &exo_rpc::ShaderSnapGame,
    shader_estimates: &std::collections::HashMap<String, f64>,
) -> Option<exo_rpc::ShaderEstimate> {
    sg.estimate.clone().or_else(|| {
        shader_estimates
            .get(&sg.id)
            .copied()
            .map(|seconds| exo_rpc::ShaderEstimate {
                seconds,
                low_seconds: None,
                learned: false,
            })
    })
}

fn shader_whole_percent(percent: f64) -> Option<u32> {
    if !percent.is_finite() {
        return None;
    }
    let share = (percent / 100.0).clamp(0.0, 1.0);
    let cap = if share < 1.0 { 99 } else { 100 };
    Some(((share * 100.0 + 1e-9).floor() as u32).min(cap))
}

fn shader_poster_subtitle(
    sg: &exo_rpc::ShaderSnapGame,
    place: Option<usize>,
    estimate: Option<&exo_rpc::ShaderEstimate>,
) -> String {
    if sg.compiling {
        if let Some(pct) = sg.percent.and_then(shader_whole_percent) {
            return format!("Compiling · {pct}%");
        }
        return sg
            .status_text
            .as_deref()
            .filter(|s| !s.is_empty())
            .unwrap_or("Compiling")
            .to_string();
    }
    let time = if place.is_some() || sg.in_add_all_ready {
        estimate.map(format_shader_estimate)
    } else {
        None
    };
    if let Some(pos) = place {
        let head = if pos == 1 {
            "Next".to_string()
        } else {
            format!("#{pos}")
        };
        return match time {
            Some(t) => format!("{head} · {t}"),
            None => head,
        };
    }
    time.or_else(|| sg.status_text.clone()).unwrap_or_default()
}

fn shader_prepare_subtitle(sg: &exo_rpc::ShaderPrepareGame) -> String {
    match sg.state.as_str() {
        "running" => "Compiling its first launch".into(),
        "stale" => "Driver changed".into(),
        _ => sg
            .message
            .clone()
            .filter(|m| !m.is_empty())
            .unwrap_or_else(|| "First launch".into()),
    }
}

fn shader_eta_text(span: ShaderSpan, floor: bool, running: bool) -> Option<String> {
    if span.high <= 0.0 {
        return None;
    }
    let left = if running { " left" } else { "" };
    if floor {
        return (span.low >= 60.0).then(|| format!("over {}{left}", shader_round_span(span.low)));
    }
    if !span.learned {
        return Some(format!("{}{left}", shader_range_text(span.low, span.high)));
    }
    if span.high < 60.0 {
        return Some(format!("under a minute{left}"));
    }
    Some(format!("about {}{left}", shader_round_span(span.high)))
}

fn exo_ai_storage_view(s: &AppState) -> El {
    let drives =
        s.pc.drives
            .as_ref()
            .map(|d| d.drives.as_slice())
            .unwrap_or(&[]);
    col()
        .w_full()
        .pt(60.0)
        .px(16.0)
        .pb(36.0)
        .gap(16.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    glass_button("storage-back", "Exo AI", Some(icon_chevron_left(14.0, FG)))
                        .on_press(|s: &mut AppState| s.pc.exo_ai_storage_open = false),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            glass_button("storage-clean", "Clean temporary files", None)
                                .on_press(|s: &mut AppState| s.clean_system_junk()),
                        )
                        .child(
                            glass_button(
                                "storage-refresh",
                                "Refresh",
                                Some(icon_refresh(14.0, FG)),
                            )
                            .on_press(|s: &mut AppState| s.load_pc_drives()),
                        ),
                ),
        )
        .child(text("Storage").font(TextStyle::new(22.0, 650.0)).color(FG))
        .children(drives.iter().map(drive_row))
}

fn exo_ai_room(s: &AppState, scale: f32) -> El {
    if s.pc.exo_ai_storage_open || s.pc.pc_tab == PcTab::Storage {
        return exo_ai_storage_view(s);
    }

    let ap_on = s.pc.autopilot.as_ref().is_some_and(|a| a.on);
    let about = s.pc.system_about.as_ref();
    let pc_name = about
        .and_then(|a| a.name.as_deref())
        .filter(|n| !n.is_empty())
        .unwrap_or("This PC");
    let cpu_name = about
        .and_then(|a| a.processor.as_deref().or(a.cpu.as_deref()))
        .unwrap_or("Processor");
    let gpu_name =
        s.pc.gpu_short_name
            .as_deref()
            .or_else(|| about.and_then(|a| a.graphics.as_deref().or(a.gpu.as_deref())))
            .unwrap_or("Graphics");
    let ram_label = match (
        about.and_then(|a| a.memory_gb.or(a.ram_gb)),
        about
            .and_then(|a| a.memory_type.as_deref())
            .filter(|t| !t.is_empty()),
    ) {
        (Some(gb), Some(t)) => format!("{} GB {t}", gb.round() as u32),
        (Some(gb), None) => format!("{} GB", gb.round() as u32),
        _ => "Memory".into(),
    };
    let display_label =
        s.pc.screen_label
            .as_deref()
            .or_else(|| about.and_then(|a| a.display.as_deref()))
            .unwrap_or("Primary display");
    let board_label = about
        .and_then(|a| a.motherboard.as_deref())
        .unwrap_or("Motherboard");
    let net_label = {
        let base = about.and_then(|a| a.network.as_deref());
        let speed = about
            .and_then(|a| a.network_mbps)
            .filter(|&m| m > 0.0)
            .map(|mbps| {
                if mbps >= 1000.0 {
                    let gbps = (mbps / 100.0).round() / 10.0;
                    if (gbps - gbps.round()).abs() < 0.01 {
                        format!("{:.0} Gbps", gbps)
                    } else {
                        format!("{gbps:.1} Gbps")
                    }
                } else {
                    format!("{:.0} Mbps", mbps.round())
                }
            });
        match (base, speed) {
            (Some(b), Some(sp)) if !b.contains("bps") => format!("{b} · {sp}"),
            (Some(b), _) => b.to_string(),
            (None, Some(sp)) => sp,
            (None, None) => "Network".into(),
        }
    };
    let win_label = format_windows_label(about);

    let sys_drive = s.pc.drives.as_ref().and_then(|d| d.drives.first());
    let (drive_title, drive_sub, drive_pct) = if let Some(d) = sys_drive {
        let letters = d
            .volumes
            .iter()
            .map(|v| v.letter.as_str())
            .filter(|l| !l.is_empty())
            .collect::<Vec<_>>()
            .join(" ");
        let total_gb: f64 = d.volumes.iter().map(|v| v.total_gb).sum::<f64>().max(1.0);
        let free_gb: f64 = d.volumes.iter().map(|v| v.free_gb).sum();
        let used = ((total_gb - free_gb) / total_gb).clamp(0.0, 1.0) as f32;
        let model = short_drive_model(&d.model);
        let title = if letters.is_empty() {
            model
        } else if model.is_empty() {
            letters
        } else {
            format!("{letters} {model}")
        };
        (
            title,
            format!(
                "{} free of {}",
                format_gb_short(free_gb),
                format_gb_short(total_gb)
            ),
            used,
        )
    } else {
        ("C: System drive".into(), "NVMe SSD".into(), 0.55)
    };

    let (storage_label, storage_is_good) = if s.pc.compress_potential_bytes > 0 {
        let gb = (s.pc.compress_potential_bytes as f64 / 1_000_000_000.0)
            .round()
            .max(1.0) as u64;
        (format!("Storage · {gb} GB"), true)
    } else if s.pc.compress_saved_bytes > 0 {
        let gb = (s.pc.compress_saved_bytes as f64 / 1_000_000_000.0)
            .round()
            .max(1.0) as u64;
        (format!("Storage · {gb} GB"), true)
    } else {
        ("Storage".into(), true)
    };
    let storage_fg = if storage_is_good { good() } else { FG };

    let header = row()
        .w_full()
        .h(36.0)
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(text("Exo AI").font(type_page()).color(FG))
                .child(
                    row()
                        .h(26.0)
                        .px(11.0)
                        .gap(7.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(surface_card())
                        .border(1.0, float_line())
                        .child(el().size(6.0, 6.0).radius(3.0).fill(if ap_on {
                            good()
                        } else {
                            fg_muted()
                        }))
                        .child(
                            text(if ap_on {
                                "Autopilot active"
                            } else {
                                "Autopilot paused"
                            })
                            .font(TextStyle::new(scaled(12.0), 600.0))
                            .color(if ap_on {
                                FG
                            } else {
                                fg_muted()
                            }),
                        ),
                ),
        )
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(
                    row()
                        .key("pc-tab-storage")
                        .role("button")
                        .label("Storage")
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .h(32.0)
                        .pl(10.0)
                        .pr(12.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .backdrop_blur(GLASS_CONTROL_SIGMA)
                        .hover(hover_fill())
                        .press(pressed_fill())
                        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                        .motion(SPRING_QUICK)
                        .on_press(|s: &mut AppState| {
                            s.pc.exo_ai_storage_open = true;
                            s.pc.part_menu = None;
                            s.load_pc_drives();
                        })
                        .child(icon_hard_drive(14.0, storage_fg))
                        .child(
                            text(storage_label)
                                .font(TextStyle::new(scaled(12.0), 600.0))
                                .color(storage_fg),
                        ),
                )
                .child(
                    el().key("ai-run-again")
                        .role("button")
                        .label("Run again")
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .h(32.0)
                        .px(14.0)
                        .center()
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .backdrop_blur(GLASS_CONTROL_SIGMA)
                        .hover(hover_fill())
                        .press(pressed_fill())
                        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                        .motion(SPRING_QUICK)
                        .on_press(|s: &mut AppState| s.run_exo_ai_now())
                        .child(text("Run again").font(type_control()).color(FG)),
                ),
        );

    let tune_rows = [
        (
            "Power & Autopilot",
            "Exo power plan, 0.5 ms timer, standby purge & Autopilot",
        ),
        (
            "Graphics & display",
            "Graphics profile and display at best defaults",
        ),
        (
            "System, interrupts & network",
            "Windows 11 tweaks, MSI interrupts & network optimal",
        ),
        ("Preinstalled apps", "No bloatware found"),
        (
            "Temporary caches",
            "Windows Update, browser, crash dump & launcher caches",
        ),
        ("Drivers & updates", "Graphics & chipset drivers up to date"),
    ];

    let mut auto_tune_card = col()
        .key("exo-ai-auto-tune-card")
        .w_full()
        .pt(18.0)
        .px(24.0)
        .pb(8.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(
            row()
                .w_full()
                .min_h(42.0)
                .pb(10.0)
                .justify_between()
                .center_y()
                .child(
                    text("Auto Tune")
                        .key("exo-ai-auto-tune-title")
                        .font(type_title())
                        .color(FG),
                )
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .child(icon_check(13.0, good()))
                        .child(
                            text("Auto Tune complete")
                                .key("exo-ai-auto-tune-status")
                                .font(TextStyle::new(scaled(12.0), 500.0))
                                .color(good()),
                        ),
                ),
        )
        .child(el().w_full().h(1.0).fill(float_line()));

    let mut stages_col = col().w_full().pt(4.0);
    for (i, (title, desc)) in tune_rows.iter().enumerate() {
        if i > 0 {
            stages_col = stages_col.child(el().w_full().h(1.0).fill(float_line()));
        }
        stages_col = stages_col.child(
            row()
                .w_full()
                .min_h(48.0)
                .py(8.0)
                .gap(10.0)
                .center_y()
                .child(
                    el().size(16.0, 16.0)
                        .center()
                        .child(icon_check(14.0, good())),
                )
                .child(
                    col()
                        .gap(2.0)
                        .child(text(*title).font(type_section()).color(FG))
                        .child(text(*desc).font(type_small()).color(fg_muted())),
                ),
        );
    }

    auto_tune_card = auto_tune_card
        .child(stages_col)
        .child(el().w_full().mt(4.0).h(1.0).fill(float_line()))
        .child(
            col()
                .w_full()
                .pt(2.0)
                .child(
                    row()
                        .w_full()
                        .min_h(58.0)
                        .py(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            row()
                                .gap(10.0)
                                .center_y()
                                .child(icon_monitor(15.0, fg_muted()))
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(
                                            text("GPU overclock & undervolt")
                                                .font(type_section())
                                                .color(FG),
                                        )
                                        .child(
                                            text(gpu_name.to_string())
                                                .font(type_small())
                                                .color(fg_muted()),
                                        ),
                                ),
                        )
                        .child(
                            glass_button("ai-tune-gpu", "Tune GPU", None)
                                .on_press(|s: &mut AppState| s.run_exo_ai_now()),
                        ),
                )
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .min_h(58.0)
                        .py(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            row()
                                .gap(10.0)
                                .center_y()
                                .child(icon_cpu(15.0, fg_muted()))
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(
                                            text("CPU undervolt (Curve Optimizer)")
                                                .font(type_section())
                                                .color(FG),
                                        )
                                        .child(
                                            text(cpu_name.to_string())
                                                .font(type_small())
                                                .color(fg_muted()),
                                        ),
                                ),
                        )
                        .child(
                            glass_button("ai-tune-cpu", "Tune CPU", None)
                                .on_press(|s: &mut AppState| s.run_latency_check()),
                        ),
                ),
        );

    let spec_cell = |icon: El, label: String| {
        col()
            .grow(1.0)
            .min_w(0.0)
            .child(el().w_full().h(1.0).fill(float_line()))
            .child(
                row()
                    .w_full()
                    .pt(14.0)
                    .pr(28.0)
                    .pb(16.0)
                    .gap(10.0)
                    .items_start()
                    .child(el().pt(1.0).child(icon))
                    .child(
                        text(label)
                            .font(type_control())
                            .color(FG)
                            .grow(1.0)
                            .min_w(0.0)
                            .wrap_anywhere(),
                    ),
            )
    };

    let spec_action_cell =
        |key: &'static str, aria: &'static str, icon: El, label: String, menu: PcPartMenu| {
            let active = s.pc.part_menu == Some(menu);
            col()
                .key(key)
                .role("button")
                .label(format!("{aria} {label}"))
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .hover(hover_fill())
                .press(pressed_fill())
                .radius(RADIUS_SMALL)
                .grow(1.0)
                .min_w(0.0)
                .on_press(move |s: &mut AppState| {
                    s.pc.part_menu = if s.pc.part_menu == Some(menu) {
                        None
                    } else {
                        Some(menu)
                    };
                })
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .pt(14.0)
                        .pr(24.0)
                        .pb(16.0)
                        .gap(10.0)
                        .items_start()
                        .child(el().pt(1.0).child(icon))
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(
                                    text(label)
                                        .font(type_control())
                                        .color(FG)
                                        .grow(1.0)
                                        .min_w(0.0)
                                        .wrap_anywhere(),
                                )
                                .child(el().w(44.0).h(1.0).fill(if active {
                                    FG
                                } else {
                                    fg_faint()
                                })),
                        ),
                )
        };

    let drive_cell = col()
        .grow(1.0)
        .min_w(0.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .pt(14.0)
                .pr(28.0)
                .pb(16.0)
                .gap(10.0)
                .items_start()
                .child(el().pt(1.0).child(icon_hard_drive(16.0, fg_muted())))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(3.0)
                        .child(text(drive_title).font(type_control()).color(FG).truncate())
                        .child(
                            text(drive_sub)
                                .font(type_body())
                                .color(fg_muted())
                                .truncate(),
                        )
                        .child(el().pt(5.0).pb(2.0).child(progress_bar(168.0, drive_pct))),
                ),
        );

    let hw_card = col()
        .key("exo-ai-hw-card")
        .w_full()
        .pt(20.0)
        .px(24.0)
        .pb(14.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(text(pc_name.to_string()).font(type_page()).color(FG))
        .child(
            row()
                .w_full()
                .mt(12.0)
                .child(spec_cell(icon_cpu(16.0, fg_muted()), cpu_name.to_string()))
                .child(spec_action_cell(
                    "pc-cell-graphics",
                    "Graphics",
                    icon_monitor(16.0, fg_muted()),
                    gpu_name.to_string(),
                    PcPartMenu::Graphics,
                ))
                .child(spec_cell(icon_hard_drive(16.0, fg_muted()), ram_label))
                .child(spec_action_cell(
                    "pc-cell-display",
                    "Display",
                    icon_monitor(16.0, fg_muted()),
                    display_label.to_string(),
                    PcPartMenu::Display,
                )),
        )
        .child(
            row()
                .w_full()
                .items_start()
                .child(spec_action_cell(
                    "pc-cell-board",
                    "Motherboard",
                    icon_cpu(16.0, fg_muted()),
                    board_label.to_string(),
                    PcPartMenu::Board,
                ))
                .child(spec_cell(icon_app_window(16.0, fg_muted()), net_label))
                .child(spec_cell(icon_app_window(16.0, fg_muted()), win_label))
                .child(drive_cell),
        );

    let updates_card = col()
        .key("exo-ai-updates-card")
        .w_full()
        .py(14.0)
        .px(24.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(
            row()
                .w_full()
                .min_h(32.0)
                .justify_between()
                .center_y()
                .child(text("Updates").font(type_title()).color(FG))
                .child(text("Up to date").font(type_body()).color(fg_muted())),
        );

    let left_col = col()
        .key("exo-ai-left-col")
        .grow(1.12)
        .min_w(0.0)
        .gap(10.0)
        .child(auto_tune_card)
        .child(hw_card)
        .child(updates_card);

    let (shader_list, shader_summary) = if !s.pc.shader_snap_games.is_empty() {
        let current = s.pc.shader_snap_games.iter().find(|g| g.compiling);
        let current_id = current.map(|g| g.id.as_str());
        let waiting: Vec<&exo_rpc::ShaderSnapGame> =
            s.pc.shader_snap_games
                .iter()
                .filter(|g| g.queued && Some(g.id.as_str()) != current_id)
                .collect();
        let mut ready: Vec<&exo_rpc::ShaderSnapGame> =
            s.pc.shader_snap_games
                .iter()
                .filter(|g| g.in_add_all_ready && !g.queued && Some(g.id.as_str()) != current_id)
                .collect();
        ready.sort_by(|a, b| {
            let ta = resolve_shader_estimate(a, &s.pc.shader_estimates)
                .map(|e| e.seconds)
                .unwrap_or(f64::INFINITY);
            let tb = resolve_shader_estimate(b, &s.pc.shader_estimates)
                .map(|e| e.seconds)
                .unwrap_or(f64::INFINITY);
            ta.partial_cmp(&tb).unwrap_or(std::cmp::Ordering::Equal)
        });
        let mut rest: Vec<&exo_rpc::ShaderSnapGame> = s
            .pc
            .shader_snap_games
            .iter()
            .filter(|g| {
                Some(g.id.as_str()) != current_id
                    && !g.queued
                    && !g.in_add_all_ready
                    && (g.error.is_some() || matches!(g.tone.as_deref(), Some("warn" | "primary")))
            })
            .collect();
        rest.sort_by_key(|g| match g.tone.as_deref() {
            Some("warn" | "primary") => 1000_u32,
            Some("good") => 2000,
            _ => 3000,
        });

        let working = current.is_some();
        let mut total = ShaderSpan {
            low: 0.0,
            high: 0.0,
            learned: true,
        };
        let mut unknown = 0usize;
        if let Some(cur) = current {
            if let Some(est) = resolve_shader_estimate(cur, &s.pc.shader_estimates) {
                let share = 1.0 - (cur.percent.unwrap_or(0.0).clamp(0.0, 100.0) / 100.0);
                let span = shader_span(&est, share);
                total.low += span.low;
                total.high += span.high;
                total.learned &= span.learned;
            } else {
                unknown += 1;
            }
        }
        let counted: Vec<&exo_rpc::ShaderSnapGame> = if working {
            waiting.clone()
        } else {
            waiting.iter().chain(ready.iter()).copied().collect()
        };
        for sg in &counted {
            if let Some(est) = resolve_shader_estimate(sg, &s.pc.shader_estimates) {
                let span = shader_span(&est, 1.0);
                total.low += span.low;
                total.high += span.high;
                total.learned &= span.learned;
            } else {
                unknown += 1;
            }
        }
        let left_count = if working {
            waiting.len() + usize::from(current.is_some())
        } else {
            waiting.len() + ready.len()
        };
        let eta = if working || left_count > 0 {
            shader_eta_text(total, unknown > 0, working)
        } else {
            None
        };
        let games_label = (left_count > 0).then(|| {
            format!(
                "{left_count} {}",
                if left_count == 1 { "game" } else { "games" }
            )
        });
        let summary = match (games_label, eta) {
            (Some(g), Some(t)) => format!("{g} · {t}"),
            (Some(g), None) => g,
            (None, Some(t)) => t,
            (None, None) => String::new(),
        };

        let mut ordered: Vec<(&exo_rpc::ShaderSnapGame, Option<usize>)> = Vec::new();
        if let Some(cur) = current {
            ordered.push((cur, None));
        }
        for (i, sg) in waiting.iter().enumerate() {
            ordered.push((sg, Some(i + 1)));
        }
        for sg in &ready {
            ordered.push((sg, None));
        }
        for sg in &rest {
            ordered.push((sg, None));
        }

        let list = ordered
            .into_iter()
            .take(8)
            .map(|(sg, place)| {
                let est = resolve_shader_estimate(sg, &s.pc.shader_estimates);
                let sub = shader_poster_subtitle(sg, place, est.as_ref());
                let g = s
                    .games
                    .iter()
                    .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.name));
                (sg.id.clone(), sg.name.clone(), sub, g)
            })
            .collect();
        (list, summary)
    } else if !s.pc.shader_games.is_empty() {
        let list: Vec<_> =
            s.pc.shader_games
                .iter()
                .take(8)
                .map(|sg| {
                    let g = s
                        .games
                        .iter()
                        .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.title));
                    (
                        sg.id.clone(),
                        sg.title.clone(),
                        shader_prepare_subtitle(sg),
                        g,
                    )
                })
                .collect();
        let n = list.len();
        let summary = format!("{n} {}", if n == 1 { "game" } else { "games" });
        (list, summary)
    } else {
        let list: Vec<_> = s
            .games
            .iter()
            .filter(|g| g.installed)
            .take(8)
            .map(|g| (g.id.clone(), g.title.clone(), "Ready".into(), Some(g)))
            .collect();
        let n = list.len();
        let summary = format!("{n} {}", if n == 1 { "game" } else { "games" });
        (list, summary)
    };

    let mut shader_rows = col().w_full().pt(16.0).gap(20.0);
    for chunk in shader_list.chunks(4) {
        let mut r = row().w_full().gap(16.0).items_start();
        for (id, title, est, game_opt) in chunk {
            let hue = title_hue(title);
            let mut art = el()
                .w_full()
                .h(298.0)
                .radius(12.0)
                .continuous_corners()
                .clip_rounded(12.0);
            if let Some(img) = game_opt.and_then(|g| s.cover_image(g, scale)) {
                art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
            } else {
                art = art.linear_gradient(
                    0.35,
                    [(0.0, oklch(0.38, 0.14, hue)), (1.0, oklch(0.10, 0.03, hue))],
                );
            }
            r = r.child(
                col()
                    .key(format!("shader-game-{id}"))
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(6.0)
                    .child(art)
                    .child(
                        col()
                            .w_full()
                            .gap(1.0)
                            .child(
                                text(title.clone())
                                    .font(type_control())
                                    .color(FG)
                                    .truncate(),
                            )
                            .child(
                                text(est.clone())
                                    .font(type_small())
                                    .color(fg_muted())
                                    .truncate(),
                            ),
                    ),
            );
        }
        for _ in chunk.len()..4 {
            r = r.child(el().grow(1.0).min_w(0.0));
        }
        shader_rows = shader_rows.child(r);
    }

    let shaders_card = col()
        .key("exo-ai-shaders-card")
        .w_full()
        .pt(18.0)
        .px(24.0)
        .pb(18.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(
            row()
                .w_full()
                .min_h(42.0)
                .pb(10.0)
                .justify_between()
                .center_y()
                .child(
                    text("Shaders")
                        .key("exo-ai-shaders-title")
                        .font(type_title())
                        .color(FG),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(text(shader_summary).font(type_body()).color(fg_muted()))
                        .child(
                            glass_button("shaders-refresh", "Refresh", None)
                                .on_press(|s: &mut AppState| s.load_pc_exo_ai()),
                        )
                        .child(
                            primary_button("shaders-compile", "Compile", None)
                                .on_press(|s: &mut AppState| s.compile_shaders_all()),
                        ),
                ),
        )
        .child(shader_rows);

    let right_col = col()
        .key("exo-ai-right-col")
        .grow(1.0)
        .min_w(0.0)
        .gap(10.0)
        .child(shaders_card);

    col()
        .key("room-exo-ai")
        .w_full()
        .h_full()
        .pt(64.0)
        .px(10.0)
        .pb(20.0)
        .gap(14.0)
        .fill(BG)
        .child(header)
        .child(
            row()
                .key("exo-ai-grid")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .items_start()
                .gap(10.0)
                .child(left_col)
                .child(right_col),
        )
}

fn apps_pill(key: &'static str, label: &'static str, active: bool) -> El {
    el().key(key)
        .role("tab")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(if active { FG } else { TRANSPARENT })
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(type_control())
                .color(if active { PRIMARY_FG } else { fg_muted() }),
        )
}

fn app_table_row(app: &InstalledAppItem, s: &AppState, show_last_used: bool) -> El {
    let id = app.id.clone();
    let is_armed =
        s.pc.armed_uninstall
            .as_ref()
            .is_some_and(|(armed_id, _)| armed_id == &app.id);
    let icon_box = if let Some(img) = s.app_icon_image(&app.id) {
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.05))
            .child(
                el().size(20.0, 20.0)
                    .pixels_fit(img.pixels(), SurfaceFit::Fit),
            )
    } else {
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.05))
            .child(icon_app_window(15.0, fg_muted()))
    };

    let sub = app
        .kept
        .as_deref()
        .or(app.publisher.as_deref())
        .unwrap_or("")
        .to_string();
    let size_txt = app.bytes.map(format_bytes).unwrap_or_else(|| "—".into());
    let last_used = app.last_opened.clone().unwrap_or_default();

    let action_slot = if is_armed {
        let confirm_btn = el()
            .key(format!("app-confirm-{}", app.id))
            .role("button")
            .label(format!("Confirm uninstall {}", app.name))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(30.0)
            .px(12.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(bad())
            .border(1.0, bad())
            .hover(bad().alpha(0.90))
            .press(bad().alpha(0.80))
            .on_press(move |s: &mut AppState| s.arm_or_confirm_uninstall(&id))
            .on_key(|s: &mut AppState, ev, _| {
                if ev.key == Key::Escape && s.pc.armed_uninstall.is_some() {
                    s.disarm_uninstall();
                    true
                } else {
                    false
                }
            })
            .child(
                text("Confirm uninstall")
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(PRIMARY_FG),
            );
        row()
            .key(format!("app-confirm-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(confirm_btn)
    } else {
        let uninstall_btn = el()
            .key(format!("app-uninst-{}", app.id))
            .role("button")
            .label(format!("Uninstall {}", app.name))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(30.0)
            .px(12.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(BG)
            .border(1.0, float_line())
            .hover(hover_fill())
            .press(pressed_fill())
            .group_opacity(0.0, 1.0)
            .on_press(move |s: &mut AppState| s.arm_or_confirm_uninstall(&id))
            .child(
                text("Uninstall")
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(bad()),
            );
        row()
            .key(format!("app-uninst-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(uninstall_btn)
    };

    let mut content_row = row()
        .w_full()
        .h(51.0)
        .gap(12.0)
        .center_y()
        .hover_group()
        .hover_motion(SPRING_QUICK, SPRING_QUICK)
        .child(icon_box)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(
                    text(app.name.clone())
                        .font(type_control())
                        .color(FG)
                        .truncate(),
                )
                .children(
                    (!sub.is_empty())
                        .then(|| text(sub).font(type_small()).color(fg_muted()).truncate()),
                ),
        );
    if show_last_used {
        content_row = content_row.child(
            el().w(scaled(112.0)).child(
                text(last_used)
                    .font(type_small())
                    .color(fg_muted())
                    .truncate(),
            ),
        );
    }
    content_row = content_row
        .child(
            row()
                .w(scaled(80.0))
                .justify_end()
                .child(text(size_txt).font(type_small()).color(fg_muted())),
        )
        .child(el().w(scaled(140.0)).h(30.0).child(action_slot));

    col()
        .key(format!("app-row-{}", app.id))
        .w_full()
        .h(52.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(content_row)
}

fn is_system_part(app: &InstalledAppItem) -> bool {
    !app.removable || app.care.as_deref() == Some("runtime")
}

fn apps_room(s: &AppState) -> El {
    let q = s.pc.apps_query.text.trim().to_lowercase();
    let programs =
        s.pc.apps_list
            .as_ref()
            .map(|l| l.programs.as_slice())
            .unwrap_or(&[]);
    let main_count = programs.iter().filter(|p| !is_system_part(p)).count();
    let total_bytes: u64 = programs
        .iter()
        .filter(|p| !is_system_part(p))
        .filter_map(|p| p.bytes)
        .sum();

    let header = row()
        .w_full()
        .h(32.0)
        .justify_between()
        .center_y()
        .gap(16.0)
        .child(
            row()
                .gap(16.0)
                .center_y()
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            text("Apps")
                                .font(TextStyle::new(scaled(20.0), 650.0))
                                .color(FG),
                        )
                        .child(
                            text(format!("{main_count} apps · {}", format_bytes(total_bytes)))
                                .font(type_body())
                                .color(fg_muted()),
                        ),
                )
                .child(
                    row()
                        .gap(4.0)
                        .center_y()
                        .child(
                            apps_pill(
                                "apps-sub-installed",
                                "Apps",
                                s.pc.apps_sub_tab == AppsSubTab::Installed,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Installed),
                        )
                        .child(
                            apps_pill(
                                "apps-sub-startup",
                                "Startup",
                                s.pc.apps_sub_tab == AppsSubTab::Startup,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Startup),
                        ),
                ),
        )
        .child(
            row()
                .cursor(Cursor::Text)
                .w(240.0)
                .h(32.0)
                .pl(10.0)
                .pr(8.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .backdrop_blur(GLASS_CONTROL_SIGMA)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_search(14.0, fg_muted()))
                .child(
                    text_input(&s.pc.apps_query, type_body())
                        .key("apps-search-input")
                        .label("Search apps")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Search", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| &mut s.pc.apps_query)),
                ),
        );

    let body = if s.pc.apps_sub_tab == AppsSubTab::Startup {
        let items =
            s.pc.startup_list
                .as_ref()
                .map(|l| l.items.as_slice())
                .unwrap_or(&[]);
        let mut list_col = col()
            .key("pc-startup-scroll")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });
        for item in items {
            let id = item.id.clone();
            list_col = list_col.child(
                col()
                    .w_full()
                    .h(52.0)
                    .child(el().w_full().h(1.0).fill(float_line()))
                    .child(
                        row()
                            .w_full()
                            .h(51.0)
                            .justify_between()
                            .center_y()
                            .child(
                                col()
                                    .gap(2.0)
                                    .child(text(item.name.clone()).font(type_control()).color(FG))
                                    .child(
                                        text(
                                            item.does
                                                .clone()
                                                .unwrap_or_else(|| item.source.clone()),
                                        )
                                        .font(type_small())
                                        .color(fg_muted()),
                                    ),
                            )
                            .child(
                                glass_button(
                                    "startup-rm",
                                    "Disable",
                                    Some(icon_trash(13.0, warn())),
                                )
                                .on_press(move |s: &mut AppState| s.remove_startup_item(&id)),
                            ),
                    ),
            );
        }
        list_col
    } else {
        let mut main_apps: Vec<&InstalledAppItem> = programs
            .iter()
            .filter(|p| !is_system_part(p))
            .filter(|p| {
                q.is_empty()
                    || p.name.to_lowercase().contains(&q)
                    || p.publisher
                        .as_deref()
                        .unwrap_or("")
                        .to_lowercase()
                        .contains(&q)
            })
            .collect();
        main_apps.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));

        let mut parts: Vec<&InstalledAppItem> = programs
            .iter()
            .filter(|p| is_system_part(p))
            .filter(|p| {
                q.is_empty()
                    || p.name.to_lowercase().contains(&q)
                    || p.publisher
                        .as_deref()
                        .unwrap_or("")
                        .to_lowercase()
                        .contains(&q)
            })
            .collect();
        parts.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));

        let visible_main = &main_apps[..main_apps.len().min(60)];
        let visible_parts = &parts[..parts.len().min(40)];
        let show_last_used = visible_main.iter().chain(visible_parts.iter()).any(|a| {
            a.last_opened
                .as_deref()
                .is_some_and(|t| !t.trim().is_empty())
        });

        let mut table_head = row()
            .key("apps-table-head")
            .w_full()
            .h(32.0)
            .gap(12.0)
            .center_y()
            .child(el().w(28.0))
            .child(
                el().grow(1.0).min_w(0.0).child(
                    text("Name")
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(fg_muted()),
                ),
            );
        if show_last_used {
            table_head = table_head.child(
                el().key("apps-head-last-used").w(scaled(112.0)).child(
                    text("Last used")
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(fg_muted()),
                ),
            );
        }
        table_head = table_head
            .child(
                row()
                    .w(scaled(80.0))
                    .justify_end()
                    .gap(4.0)
                    .center_y()
                    .child(icon_chevron_down(12.0, fg_muted()))
                    .child(
                        text("Size")
                            .font(TextStyle::new(scaled(12.0), 500.0))
                            .color(FG),
                    ),
            )
            .child(
                row().w(scaled(140.0)).justify_end().center_y().child(
                    el().cursor(Cursor::Pointer)
                        .h(28.0)
                        .px(12.0)
                        .center()
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .backdrop_blur(GLASS_CONTROL_SIGMA)
                        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                        .child(
                            text("Select")
                                .font(TextStyle::new(scaled(12.0), 500.0))
                                .color(FG),
                        ),
                ),
            );

        let mut list = col()
            .key("pc-apps-scroll")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });
        for app in visible_main {
            list = list.child(app_table_row(app, s, show_last_used));
        }
        if !parts.is_empty() {
            list = list.child(
                el().w_full().pt(20.0).pb(8.0).child(
                    text(format!(
                        "What Windows, games and Exo need ({})",
                        parts.len()
                    ))
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(fg_muted()),
                ),
            );
            for app in visible_parts {
                list = list.child(app_table_row(app, s, show_last_used));
            }
        }

        col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(table_head)
            .child(list)
    };

    let surface = col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .pt(20.0)
        .px(24.0)
        .pb(8.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .clip_rounded(RADIUS_PANEL)
        .child(header)
        .child(body);

    col()
        .key("room-apps")
        .w_full()
        .h_full()
        .pt(64.0)
        .px(10.0)
        .pb(20.0)
        .fill(BG)
        .on_background_press(|s: &mut AppState| s.disarm_uninstall())
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape && s.pc.armed_uninstall.is_some() {
                s.disarm_uninstall();
                true
            } else {
                false
            }
        })
        .child(surface)
}

pub fn view(s: &AppState, scale: f32) -> El {
    match s.room {
        Room::Devices => devices_room(s, scale),
        Room::Apps => apps_room(s),
        _ => exo_ai_room(s, scale),
    }
}

pub fn part_menu_layers(s: &AppState, blur: bool) -> Vec<El> {
    let Some(which) = s.pc.part_menu else {
        return Vec::new();
    };
    if s.room != Room::ExoAi {
        return Vec::new();
    }
    let (anchor_key, title) = match which {
        PcPartMenu::Graphics => ("pc-cell-graphics", "Graphics"),
        PcPartMenu::Display => ("pc-cell-display", "Display"),
        PcPartMenu::Board => ("pc-cell-board", "Motherboard"),
    };

    let row_item = |label: String, sub: Option<String>, value: String, tone: rune::Color| {
        row()
            .w_full()
            .py(12.0)
            .justify_between()
            .center_y()
            .gap(16.0)
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(3.0)
                    .child(text(label).font(type_control()).color(FG))
                    .children(sub.map(|s| text(s).font(type_small()).color(fg_muted()))),
            )
            .child(text(value).font(type_control()).color(tone))
    };

    let divider = || el().w_full().h(1.0).fill(float_line());

    let mut body = col().w_full().px(20.0).pb(16.0);
    match which {
        PcPartMenu::Graphics => {
            let gpu_name =
                s.pc.gpu_short_name
                    .as_deref()
                    .or_else(|| {
                        s.pc.system_about
                            .as_ref()
                            .and_then(|a| a.gpu.as_deref().or(a.graphics.as_deref()))
                    })
                    .unwrap_or("Graphics adapter")
                    .to_string();
            body = body
                .child(row_item(
                    "Graphics driver".into(),
                    Some(gpu_name),
                    "Game Ready".into(),
                    FG,
                ))
                .child(divider())
                .child(row_item(
                    "Exo's driver profile".into(),
                    None,
                    "Applied".into(),
                    good(),
                ))
                .child(divider())
                .child(row_item("Resizable BAR".into(), None, "On".into(), good()))
                .child(divider())
                .child(row_item(
                    "Hardware-accelerated GPU scheduling".into(),
                    None,
                    "On".into(),
                    good(),
                ));
        }
        PcPartMenu::Display => {
            let screen =
                s.pc.screen_label
                    .as_deref()
                    .or_else(|| {
                        s.pc.system_about
                            .as_ref()
                            .and_then(|a| a.display.as_deref())
                    })
                    .unwrap_or("Primary display")
                    .to_string();
            let hdr_on = s.pc.hdr.as_ref().is_some_and(|h| h.on);
            body = body
                .child(row_item(
                    "Active display".into(),
                    Some(screen),
                    "Native".into(),
                    FG,
                ))
                .child(divider())
                .child(row_item(
                    "HDR".into(),
                    Some("High dynamic range output".into()),
                    if hdr_on { "On".into() } else { "Off".into() },
                    if hdr_on { good() } else { fg_muted() },
                ))
                .child(divider())
                .child(row_item(
                    "Variable refresh rate".into(),
                    None,
                    "Active".into(),
                    good(),
                ));
        }
        PcPartMenu::Board => {
            let board =
                s.pc.system_about
                    .as_ref()
                    .and_then(|a| a.motherboard.as_deref())
                    .unwrap_or("System board")
                    .to_string();
            let autopilot_on = s.pc.autopilot.as_ref().is_some_and(|a| a.on);
            body = body
                .child(row_item(
                    "Motherboard".into(),
                    Some(board),
                    "Ready".into(),
                    FG,
                ))
                .child(divider())
                .child(row_item(
                    "Autopilot".into(),
                    Some("Core affinity & timer pacing".into()),
                    if autopilot_on {
                        "Active".into()
                    } else {
                        "Off".into()
                    },
                    if autopilot_on { good() } else { fg_muted() },
                ))
                .child(divider())
                .child(row_item(
                    "Chipset & power plan".into(),
                    None,
                    "Optimized".into(),
                    good(),
                ));
        }
    }

    let close_label = match which {
        PcPartMenu::Graphics => "Close Graphics",
        PcPartMenu::Display => "Close Display",
        PcPartMenu::Board => "Close Motherboard",
    };
    let head = row()
        .w_full()
        .pt(16.0)
        .px(20.0)
        .pb(8.0)
        .justify_between()
        .center_y()
        .child(text(title).font(type_section()).color(FG))
        .child(
            glass_icon_circle(
                "pc-part-menu-close",
                close_label,
                28.0,
                icon_close(13.0, FG),
            )
            .on_press(|s: &mut AppState| s.pc.part_menu = None),
        );

    let card = glass_panel_opt("pc-part-menu", 540.0, blur)
        .role("dialog")
        .label(title)
        .anchored_fit(anchor_key, Side::Top, Align::Start, 10.0)
        .transition(popover_transition_from(rune::Vec2::new(0.0, 1.0)))
        .modal(true, |s: &mut AppState| s.pc.part_menu = None)
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                s.pc.part_menu = None;
                true
            } else {
                false
            }
        })
        .child(head)
        .child(body);

    vec![card]
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Key, KeyEvent, Mods, Runtime};

    #[test]
    fn apps_uninstall_hidden_at_rest_reveals_on_hover_and_confirms_in_place() {
        let mut state = AppState::default();
        if let Some(ref mut list) = state.pc.apps_list {
            for p in &mut list.programs {
                p.last_opened = None;
            }
        }
        state.switch_room(Room::Apps);

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Apps exceeded EXO_MAX_BACKDROP_BLURS: {blurs}"
        );
        assert!(blurs >= 3, "Apps should render Liquid Glass, got {blurs}");

        assert!(
            rt.find("apps-head-last-used").is_none(),
            "Last used column should be omitted when all apps have last_opened == None"
        );

        let (_, rest_opacity) = rt
            .presented("app-uninst-app:obs")
            .expect("app-uninst-app:obs");
        assert!(
            rest_opacity < 0.01,
            "Uninstall button must be hidden at rest, got opacity {rest_opacity}"
        );

        let (rx, ry) = rt.find("app-row-app:obs").expect("app-row-app:obs");
        rt.pointer_move(rx, ry);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (_, hovered_opacity) = rt
            .presented("app-uninst-app:obs")
            .expect("app-uninst-app:obs");
        assert!(
            hovered_opacity > 0.95,
            "Uninstall button must reveal on row hover, got opacity {hovered_opacity}"
        );

        let (bx, by) = rt.find("app-uninst-app:obs").expect("app-uninst-app:obs");
        rt.pointer_down(bx, by);
        rt.pointer_up(bx, by);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state
                .pc
                .armed_uninstall
                .as_ref()
                .map(|(id, _)| id.as_str()),
            Some("app:obs")
        );
        assert!(rt.state.notice.is_none());

        rt.pointer_move(10.0, 10.0);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (_, armed_opacity) = rt
            .presented("app-confirm-app:obs")
            .expect("app-confirm-app:obs");
        assert!(
            armed_opacity > 0.95,
            "Armed confirm button must remain visible when pointer leaves, got {armed_opacity}"
        );

        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_none());

        rt.pointer_move(rx, ry);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (bx2, by2) = rt.find("app-uninst-app:obs").expect("app-uninst-app:obs");
        rt.pointer_down(bx2, by2);
        rt.pointer_up(bx2, by2);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_some());

        let (bx3, by3) = rt.find("app-confirm-app:obs").expect("app-confirm-app:obs");
        rt.pointer_down(bx3, by3);
        rt.pointer_up(bx3, by3);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_none());
        assert_eq!(
            rt.state.notice.as_deref(),
            Some("Starting application uninstaller…")
        );
    }

    #[test]
    fn apps_shows_last_used_column_when_populated() {
        let mut state = AppState::default();
        state.switch_room(Room::Apps);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(
            rt.find("apps-head-last-used").is_some(),
            "Last used column should render when at least one app has last_opened"
        );
    }

    #[test]
    fn exo_ai_bento_balance_header_alignment_and_blur_cap() {
        let mut state = AppState::default();
        let extra: Vec<_> = state
            .pc
            .shader_games
            .iter()
            .enumerate()
            .map(|(i, g)| exo_rpc::ShaderPrepareGame {
                id: format!("{}-row2-{i}", g.id),
                title: g.title.clone(),
                state: g.state.clone(),
                message: g.message.clone(),
            })
            .collect();
        state.pc.shader_games.extend(extra);
        state.switch_room(Room::ExoAi);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Exo AI exceeded EXO_MAX_BACKDROP_BLURS: {blurs}"
        );
        assert!(blurs >= 3, "Exo AI should render Liquid Glass, got {blurs}");

        let (auto_title, _) = rt
            .presented("exo-ai-auto-tune-title")
            .expect("exo-ai-auto-tune-title");
        let (shaders_title, _) = rt
            .presented("exo-ai-shaders-title")
            .expect("exo-ai-shaders-title");
        assert!(
            (auto_title[1] - shaders_title[1]).abs() <= 1.0,
            "Auto Tune y={} and Shaders y={} headers should align",
            auto_title[1],
            shaders_title[1]
        );

        let (hw_rect, _) = rt.presented("exo-ai-hw-card").expect("exo-ai-hw-card");
        let (upd_rect, _) = rt
            .presented("exo-ai-updates-card")
            .expect("exo-ai-updates-card");
        assert!(
            (hw_rect[0] - upd_rect[0]).abs() <= 1.0 && upd_rect[1] > hw_rect[1],
            "Updates card should sit directly below hardware card in left column"
        );
        assert!(
            hw_rect[3] < 260.0,
            "Hardware card should hug content at natural height, got h={}",
            hw_rect[3]
        );

        let (left_col, _) = rt.presented("exo-ai-left-col").expect("exo-ai-left-col");
        let (right_col, _) = rt.presented("exo-ai-right-col").expect("exo-ai-right-col");
        let left_bottom = left_col[1] + left_col[3];
        let right_bottom = right_col[1] + right_col[3];
        assert!(
            (left_bottom - right_bottom).abs() <= 40.0,
            "Exo AI left bottom ({left_bottom}) and right bottom ({right_bottom}) drifted apart"
        );
    }

    #[test]
    fn apps_hover_and_confirm_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::default();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::Apps);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("hover app row", |rt| {
                if let Some((x, y)) = rt.find("app-row-app:obs") {
                    rt.pointer_move(x, y);
                }
            });
            run.play(0.4);
            run.act("arm uninstall", |rt| Run::tap(rt, "app-uninst-app:obs"));
            run.play(0.4);
            run.act("disarm via escape", |rt| {
                rt.key(KeyEvent {
                    key: Key::Escape,
                    mods: Mods::default(),
                });
            });
            run.play(0.4);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn exo_ai_spec_formatting_matches_react() {
        let about = exo_rpc::SystemAbout {
            windows: Some("Windows 11 Pro".into()),
            windows_version: Some("26H2".into()),
            ..Default::default()
        };
        assert_eq!(super::format_windows_label(Some(&about)), "Windows 11 26H2");

        let insider = exo_rpc::SystemAbout {
            windows: Some("Windows 11 Pro Insider Preview".into()),
            windows_version: Some("26300".into()),
            ..Default::default()
        };
        assert_eq!(
            super::format_windows_label(Some(&insider)),
            "Windows 11 Insider 26300"
        );

        assert_eq!(
            super::short_drive_model("Samsung SSD 990 PRO with Heatsink 2TB"),
            "Samsung 990 PRO"
        );
        assert_eq!(
            super::short_drive_model("WD_BLACK SN850X 2000GB"),
            "WD_BLACK SN850X"
        );
        assert_eq!(
            super::short_drive_model("Crucial® P5 Plus (TM) Solid State Drive 2 TB"),
            "Crucial P5 Plus"
        );
    }

    #[test]
    fn exo_ai_shader_subtitle_and_estimate_parity() {
        let est = |seconds: f64, low_seconds: Option<f64>, learned: bool| exo_rpc::ShaderEstimate {
            seconds,
            low_seconds,
            learned,
        };

        assert_eq!(
            super::format_shader_estimate(&est(30.0, None, false)),
            "under a minute"
        );
        assert_eq!(
            super::format_shader_estimate(&est(120.0, None, false)),
            "1–3 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(200.0, None, false)),
            "2–5 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(600.0, None, false)),
            "5–15 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(900.0, Some(10.0), true)),
            "up to 15 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(3600.0, Some(10.0), false)),
            "up to 1 h"
        );
        assert_eq!(
            super::format_shader_estimate(&est(12600.0, Some(10.0), false)),
            "up to 3 h 30 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(6000.0, None, true)),
            "about 1 h 40 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(45.0, None, true)),
            "under a minute"
        );
        assert_eq!(super::shader_range_text(7200.0, 25200.0), "2–7 h");
        assert_eq!(super::shader_range_text(5400.0, 10800.0), "1 h 30 min–3 h");
        assert_eq!(super::shader_range_text(850.0, 900.0), "about 15 min");

        let needs_rebuild = exo_rpc::ShaderSnapGame {
            id: "g1".into(),
            name: "CONTROL Resonant".into(),
            status_text: Some("Needs rebuilding".into()),
            tone: Some("warn".into()),
            in_add_all_ready: true,
            estimate: Some(est(120.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&needs_rebuild, None, needs_rebuild.estimate.as_ref()),
            "1–3 min"
        );

        let needs_rebuild_no_est = exo_rpc::ShaderSnapGame {
            estimate: None,
            ..needs_rebuild.clone()
        };
        assert_eq!(
            super::shader_poster_subtitle(&needs_rebuild_no_est, None, None),
            "Needs rebuilding"
        );

        let mut est_map = std::collections::HashMap::new();
        est_map.insert("roblox".to_string(), 600.0);
        let ready_from_map = exo_rpc::ShaderSnapGame {
            id: "roblox".into(),
            name: "Roblox".into(),
            status_text: Some("Ready to compile".into()),
            in_add_all_ready: true,
            ..Default::default()
        };
        let resolved = super::resolve_shader_estimate(&ready_from_map, &est_map);
        assert_eq!(
            super::shader_poster_subtitle(&ready_from_map, None, resolved.as_ref()),
            "5–15 min"
        );

        let warmed = exo_rpc::ShaderSnapGame {
            id: "warmed".into(),
            name: "Hades".into(),
            status_text: Some("Warmed".into()),
            tone: Some("good".into()),
            in_add_all_ready: false,
            estimate: Some(est(600.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&warmed, None, warmed.estimate.as_ref()),
            "Warmed"
        );

        let needs_recording = exo_rpc::ShaderSnapGame {
            id: "rec".into(),
            name: "New Game".into(),
            status_text: Some("Needs a 5-min recording".into()),
            tone: Some("warn".into()),
            in_add_all_ready: false,
            estimate: Some(est(600.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(
                &needs_recording,
                None,
                needs_recording.estimate.as_ref()
            ),
            "Needs a 5-min recording"
        );

        let queued = exo_rpc::ShaderSnapGame {
            id: "q1".into(),
            name: "Thymesia".into(),
            status_text: Some("Needs rebuilding".into()),
            queued: true,
            in_add_all_ready: true,
            estimate: Some(est(200.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(1), queued.estimate.as_ref()),
            "Next · 2–5 min"
        );
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(2), queued.estimate.as_ref()),
            "#2 · 2–5 min"
        );
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(1), None),
            "Next"
        );

        let compiling_pct = exo_rpc::ShaderSnapGame {
            id: "c1".into(),
            name: "Wuthering Waves".into(),
            compiling: true,
            percent: Some(42.9),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&compiling_pct, None, None),
            "Compiling · 42%"
        );

        let compiling_stage = exo_rpc::ShaderSnapGame {
            id: "c2".into(),
            name: "Wuthering Waves".into(),
            compiling: true,
            status_text: Some("Linking pipelines".into()),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&compiling_stage, None, None),
            "Linking pipelines"
        );

        let prep = |state: &str, message: Option<&str>| exo_rpc::ShaderPrepareGame {
            id: "p".into(),
            title: "Game".into(),
            state: state.into(),
            message: message.map(str::to_string),
        };
        assert_eq!(
            super::shader_prepare_subtitle(&prep("running", None)),
            "Compiling its first launch"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("stale", None)),
            "Driver changed"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("ready", Some("1–3 min"))),
            "1–3 min"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("ready", None)),
            "First launch"
        );
    }
}
