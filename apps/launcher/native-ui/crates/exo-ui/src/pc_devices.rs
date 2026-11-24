use crate::state::AppState;
use exo_kit::{
    BG, FG, GAP, PRESS_SCALE, RADIUS_CARD, RADIUS_PANEL, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK,
    amoled_card, bad, card_border, fg_faint, fg_muted, filter_pill, float_line, glass_button, good,
    hover_fill, icon_chevron_left, icon_gamepad, icon_headphones, icon_keyboard, icon_mouse,
    icon_refresh, pressed_fill, scaled, type_body, type_control, type_label, type_section,
    type_small, type_title, warn,
};
use exo_rpc::keyboard_layouts::{FormFactor, KeyCap, KeyKind, build_keyboard_layout, find_key};
use exo_rpc::{KeyboardReadReply, PeripheralCard};
use rune::{Cursor, El, ScrollSpec, SurfaceFit, TextStyle, col, el, row, text};

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

fn key_cap_width(u: f32, unit: f32, gap: f32) -> f32 {
    u * unit + (u - 1.0).max(0.0) * gap
}

fn render_key_section(
    rows: &[Vec<KeyCap>],
    unit: f32,
    key_h: f32,
    gap: f32,
    act: Option<f64>,
    rt_on: bool,
    selected_key: Option<&str>,
) -> El {
    let mut section = col().gap(gap);
    for r in rows {
        let mut krow = row().gap(gap);
        for cap in r {
            let w = key_cap_width(cap.u, unit, gap);
            if cap.kind == KeyKind::Gap {
                krow = krow.child(el().w(w).h(key_h));
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
                    .w(w)
                    .h(key_h)
                    .px(6.0)
                    .radius(RADIUS_SMALL)
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
                            card_border()
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

            let is_hot = matches!(cap.kind, KeyKind::Wasd | KeyKind::Analog);
            let fill = if is_selected {
                FG.alpha(0.22)
            } else if is_hot && rt_on {
                good().alpha(0.18)
            } else if is_hot {
                FG.alpha(0.14)
            } else if cap.kind == KeyKind::Mod {
                FG.alpha(0.06)
            } else {
                FG.alpha(0.04)
            };
            let border_c = if is_selected {
                FG.alpha(0.75)
            } else if is_hot && rt_on {
                good().alpha(0.70)
            } else {
                card_border()
            };
            let lbl_font = if unit <= 30.0 {
                type_label()
            } else {
                type_small()
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
                    .w(w)
                    .h(key_h)
                    .radius(7.0)
                    .center()
                    .gap(1.0)
                    .fill(fill)
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .border(1.0, border_c)
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| s.toggle_keyboard_key(key_id.clone()))
                    .child(text(cap.label.clone()).font(lbl_font).color(FG))
                    .children((is_hot && act.is_some()).then(|| {
                        let v = act.unwrap_or(0.4);
                        text(format!("{v:.1}")).font(type_label()).color(if rt_on {
                            good()
                        } else {
                            fg_muted()
                        })
                    })),
            );
        }
        section = section.child(krow);
    }
    section
}

fn wooting_key_matrix(s: &AppState, kb: &KeyboardReadReply) -> El {
    let spec = build_keyboard_layout(
        kb.form_factor.as_deref(),
        kb.layout_code.as_deref(),
        kb.model.as_deref(),
        kb.layout.as_deref(),
    );
    let (unit, key_h, gap) = match spec.form {
        FormFactor::Sixty => (42.0, 38.0, 4.0),
        FormFactor::Eighty | FormFactor::Tkl => (34.0, 34.0, 3.0),
        FormFactor::Full => (28.0, 32.0, 3.0),
        FormFactor::Keypad3 => (56.0, 46.0, 6.0),
    };
    let rt_on = kb.has_rapid_trigger && kb.rapid_trigger.unwrap_or(false);
    let selected_id = s.pc.keyboard_selected_key.as_deref();

    let mut sections = row().gap(12.0).child(render_key_section(
        &spec.main_rows,
        unit,
        key_h,
        gap,
        kb.actuation_mm,
        rt_on,
        selected_id,
    ));
    if !spec.nav_rows.is_empty() {
        sections = sections.child(render_key_section(
            &spec.nav_rows,
            unit,
            key_h,
            gap,
            kb.actuation_mm,
            rt_on,
            selected_id,
        ));
    }
    if !spec.num_rows.is_empty() {
        sections = sections.child(render_key_section(
            &spec.num_rows,
            unit,
            key_h,
            gap,
            kb.actuation_mm,
            rt_on,
            selected_id,
        ));
    }

    let inspect_line = selected_id.and_then(|id| find_key(&spec, id)).map(|cap| {
        let mut parts = vec![cap.label.clone()];
        if let Some(ref note) = cap.note {
            parts.push(note.clone());
        }
        if let Some(a) = kb.actuation_mm {
            parts.push(format!("Actuation {a:.1} mm"));
        }
        if rt_on && let Some(rtm) = kb.rapid_trigger_mm {
            parts.push(format!("RT {rtm:.2} mm"));
        }
        row()
            .key("kb-key-inspect")
            .w_full()
            .pt(6.0)
            .justify_between()
            .center_y()
            .child(text(parts.join(" · ")).font(type_small()).color(fg_muted()))
    });

    col()
        .key("kb-matrix")
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(8.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, card_border())
        .child(sections)
        .children(inspect_line)
}

fn kb_button(
    key: &'static str,
    label: &'static str,
    enabled: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    let mut btn = glass_button(key, label, None);
    if enabled {
        btn = btn.on_press(on_press);
    } else {
        btn = btn.cursor(Cursor::NotAllowed).opacity(0.45);
    }
    btn
}

fn kb_pill(
    key: &'static str,
    label: String,
    active: bool,
    enabled: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    let mut pill = filter_pill(key, label, active);
    if enabled {
        pill = pill.on_press(on_press);
    } else {
        pill = pill.cursor(Cursor::NotAllowed).opacity(0.45);
    }
    pill
}

fn wooting_detail(s: &AppState, kb: &KeyboardReadReply) -> El {
    let min_act = kb.min_actuation_mm.unwrap_or(0.1);
    let max_act = kb.max_actuation_mm.unwrap_or(4.0);
    let act = kb.actuation_mm.unwrap_or(min_act).clamp(min_act, max_act);
    let rt_on = kb.has_rapid_trigger && kb.rapid_trigger.unwrap_or(false);
    let rt_mm = kb.rapid_trigger_mm.unwrap_or(0.15).clamp(0.10, 2.35);
    let show_tach = kb.tachyon.is_some() || kb.hall_effect;
    let tach = kb.tachyon.unwrap_or(false);
    let rgb = kb.rgb_brightness.unwrap_or(100).clamp(0, 100);
    let can_write = kb.can_write;

    let mut meta_parts: Vec<String> = Vec::new();
    if let Some(ref lay) = kb.layout
        && !lay.is_empty()
    {
        meta_parts.push(lay.clone());
    }
    if let Some(ref fw) = kb.firmware
        && !fw.is_empty()
    {
        meta_parts.push(format!("FW {fw}"));
    }
    if let Some(hz) = kb.max_scan_hz
        && hz > 0
    {
        meta_parts.push(format!("{hz} Hz"));
    }

    let latest_backup = s.pc.keyboard_backups.first().map(|b| b.file_name.clone());

    let profiles = row()
        .gap(6.0)
        .flex_wrap()
        .children(kb.profiles.iter().map(|p| {
            let idx = p.index;
            let active = kb.active_profile == idx;
            kb_pill(
                match idx {
                    0 => "kb-prof-0",
                    1 => "kb-prof-1",
                    2 => "kb-prof-2",
                    _ => "kb-prof-3",
                },
                p.name.clone(),
                active,
                can_write,
                move |s: &mut AppState| s.switch_keyboard_profile(idx),
            )
        }));

    let error_banner = s.pc.keyboard_error.as_ref().map(|err| {
        row()
            .key("kb-write-error")
            .w_full()
            .px(12.0)
            .py(10.0)
            .gap(10.0)
            .justify_between()
            .center_y()
            .radius(RADIUS_SMALL)
            .fill(bad().alpha(0.12))
            .border(1.0, bad().alpha(0.45))
            .child(text(err.clone()).font(type_small()).color(bad()))
            .child(
                glass_button("kb-error-dismiss", "Dismiss", None)
                    .on_press(|s: &mut AppState| s.pc.keyboard_error = None),
            )
    });

    let readonly_banner = (!can_write && s.pc.keyboard_error.is_none()).then(|| {
        let msg = kb
            .message
            .clone()
            .unwrap_or_else(|| "Keyboard is read-only right now.".into());
        row()
            .key("kb-readonly-banner")
            .w_full()
            .px(12.0)
            .py(8.0)
            .radius(RADIUS_SMALL)
            .fill(warn().alpha(0.10))
            .border(1.0, warn().alpha(0.35))
            .child(text(msg).font(type_small()).color(warn()))
    });

    let mut controls = row()
        .w_full()
        .gap(12.0)
        .justify_between()
        .center_y()
        .flex_wrap()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    text(format!("Actuation {act:.1} mm"))
                        .font(type_control())
                        .color(FG),
                )
                .child(kb_button(
                    "kb-act-down",
                    "−0.1",
                    can_write && act > min_act + 0.001,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            Some((act - 0.1).max(min_act)),
                            None,
                            None,
                            None,
                            None,
                        );
                    },
                ))
                .child(kb_button(
                    "kb-act-up",
                    "+0.1",
                    can_write && act < max_act - 0.001,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            Some((act + 0.1).min(max_act)),
                            None,
                            None,
                            None,
                            None,
                        );
                    },
                )),
        );

    if kb.has_rapid_trigger && rt_on {
        controls = controls.child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    text(format!("RT {rt_mm:.2} mm"))
                        .font(type_control())
                        .color(FG),
                )
                .child(kb_button(
                    "kb-rt-down",
                    "−0.05",
                    can_write && rt_mm > 0.101,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            None,
                            None,
                            Some((rt_mm - 0.05).max(0.10)),
                            None,
                            None,
                        );
                    },
                ))
                .child(kb_button(
                    "kb-rt-up",
                    "+0.05",
                    can_write && rt_mm < 2.349,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            None,
                            None,
                            Some((rt_mm + 0.05).min(2.35)),
                            None,
                            None,
                        );
                    },
                )),
        );
    }

    if kb.has_rgb {
        controls = controls.child(
            row()
                .gap(8.0)
                .center_y()
                .child(text(format!("RGB {rgb}%")).font(type_control()).color(FG))
                .child(kb_button(
                    "kb-rgb-down",
                    "−5%",
                    can_write && rgb > 0,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(None, None, None, None, Some((rgb - 5).max(0)));
                    },
                ))
                .child(kb_button(
                    "kb-rgb-up",
                    "+5%",
                    can_write && rgb < 100,
                    move |s: &mut AppState| {
                        s.update_keyboard_settings(
                            None,
                            None,
                            None,
                            None,
                            Some((rgb + 5).min(100)),
                        );
                    },
                )),
        );
    }

    let mut toggles = row().gap(8.0).center_y();
    if kb.has_rapid_trigger {
        toggles = toggles.child(kb_pill(
            "kb-rt-toggle",
            "Rapid Trigger".into(),
            rt_on,
            can_write,
            move |s: &mut AppState| {
                s.update_keyboard_settings(None, Some(!rt_on), None, None, None);
            },
        ));
    }
    if show_tach {
        toggles = toggles.child(kb_pill(
            "kb-tach-toggle",
            "Tachyon".into(),
            tach,
            can_write,
            move |s: &mut AppState| {
                s.update_keyboard_settings(None, None, None, Some(!tach), None);
            },
        ));
    }
    controls = controls.child(toggles);

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
                .gap(12.0)
                .child(
                    col()
                        .gap(2.0)
                        .child(
                            text(
                                kb.model
                                    .clone()
                                    .unwrap_or_else(|| "Wooting Hall-Effect".into()),
                            )
                            .font(type_title())
                            .color(FG),
                        )
                        .children((!meta_parts.is_empty()).then(|| {
                            text(meta_parts.join(" · "))
                                .font(type_small())
                                .color(fg_muted())
                        })),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            glass_button("kb-backup", "Backup profiles", None)
                                .on_press(|s: &mut AppState| s.backup_keyboard_profiles()),
                        )
                        .children(latest_backup.map(|file_name| {
                            kb_button(
                                "kb-restore-latest",
                                "Restore backup",
                                can_write,
                                move |s: &mut AppState| {
                                    s.restore_keyboard_backup(file_name.clone());
                                },
                            )
                        })),
                ),
        )
        .children(error_banner)
        .children(readonly_banner)
        .child(profiles)
        .child(wooting_key_matrix(s, kb))
        .child(controls)
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
        let display_name = clean_device_name(dev, s);
        let status_line = match dev.battery {
            Some(b) => format!(
                "{} · {}%",
                connection_label(dev.connection.as_deref()),
                format_battery_pct(b)
            ),
            None => connection_label(dev.connection.as_deref()).to_string(),
        };

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

        if dev.kind == "keyboard" && dev.lab {
            if let Some(ref kb) = s.pc.keyboard
                && kb.ok
            {
                page = page.child(wooting_detail(s, kb));
                if let Some(ref port) = dev.port {
                    let good_port = port.r#where.as_deref() == Some("cpu") && port.hubs == 0;
                    let badge =
                        crate::devices_audio::port_short(port).unwrap_or_else(|| "USB".into());
                    page = page.child(crate::devices_audio::device_panel(
                        "USB port",
                        vec![crate::devices_audio::device_row(
                            "USB port",
                            Some(crate::devices_audio::port_words(port)),
                            None,
                            Some(exo_kit::status_badge(
                                badge,
                                if good_port {
                                    exo_kit::BadgeTone::Good
                                } else {
                                    exo_kit::BadgeTone::Neutral
                                },
                            )),
                        )],
                    ));
                }
            } else if s.pc.keyboard.is_some() || s.pc.keyboard_error.is_some() {
                let err_msg =
                    s.pc.keyboard_error
                        .clone()
                        .or_else(|| s.pc.keyboard.as_ref().and_then(|k| k.message.clone()))
                        .unwrap_or_else(|| "Could not read Wooting keyboard.".into());
                page = page.child(
                    amoled_card(RADIUS_PANEL)
                        .key("kb-error-card")
                        .w_full()
                        .px(20.0)
                        .py(18.0)
                        .gap(12.0)
                        .child(text("Keyboard unavailable").font(type_section()).color(FG))
                        .child(text(err_msg).font(type_body()).color(bad()))
                        .child(
                            row().child(
                                glass_button("kb-retry", "Retry", Some(icon_refresh(14.0, FG)))
                                    .on_press(|s: &mut AppState| s.reload_keyboard()),
                            ),
                        ),
                );
            } else {
                page = page.child(
                    amoled_card(RADIUS_PANEL).w_full().px(20.0).py(18.0).child(
                        text("Reading keyboard…")
                            .font(type_body())
                            .color(fg_muted()),
                    ),
                );
            }
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
