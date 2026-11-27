use crate::pc_state::SettingsSection;
use crate::settings_account::account_section;
use crate::settings_privacy::{backup_section, privacy_section};
use crate::settings_state::{
    CHANGELOG_URL, DOWNLOAD_HOURS, OBS_OVERLAY_URL, PRIVACY_URL, SUPPORT_URL, download_hours_label,
    search_settings,
};
use crate::settings_stores::{shaders_section, stores_section};
use crate::state::{AppState, InputMode, OpenDropdown};
use exo_kit::{
    BG, FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, SPRING_SMOOTH, SPRING_SNAPPY, SelectTrigger, TRANSPARENT, bad, card_border,
    fg_muted, glass_rim_bottom, glass_rim_top, glass_still_control_fill, good, hairline,
    hover_fill, icon_chevron_left, icon_close, icon_download, icon_info, icon_library, icon_search,
    icon_sliders, icon_trophy, icon_user, pressed_fill, progress_bar, scaled, select_trigger,
    selected_fill, slider_range, slider_track, surface_card, switch_pill, type_body, type_control,
    type_page, type_section, type_small, type_title,
};
use rune::{
    Cursor, El, Phase, ScrollSpec, SpringPair, TextStyle, Transition, col, edit_access, el, row,
    text, text_input,
};

pub(crate) fn settings_card() -> El {
    exo_kit::amoled_card(RADIUS_CARD)
        .w_full()
        .clip_rounded(RADIUS_CARD)
}

pub(crate) fn set_btn(key: impl Into<String>, label: impl Into<String>, icon: Option<El>) -> El {
    let label = label.into();
    let mut btn = row()
        .key(key.into())
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(FG))
}

pub(crate) fn set_primary_btn(
    key: impl Into<String>,
    label: impl Into<String>,
    icon: Option<El>,
) -> El {
    let label = label.into();
    let mut btn = row()
        .key(key.into())
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(PRIMARY_FG))
}

pub(crate) fn set_danger_btn(
    key: impl Into<String>,
    label: impl Into<String>,
    icon: Option<El>,
) -> El {
    let label = label.into();
    let mut btn = row()
        .key(key.into())
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(bad()))
}

pub(crate) fn nav_label(sec: SettingsSection) -> &'static str {
    match sec {
        SettingsSection::General => "General",
        SettingsSection::Library => "Library",
        SettingsSection::Downloads => "Downloads",
        SettingsSection::InGame => "In game",
        SettingsSection::Capture => "Capture",
        SettingsSection::Account => "Account",
        SettingsSection::About => "About",
    }
}

fn nav_icon(sec: SettingsSection, active: bool) -> El {
    let c = if active { FG } else { FG.alpha(0.48) };
    match sec {
        SettingsSection::General => icon_sliders(15.0, c),
        SettingsSection::Library => icon_library(15.0, c),
        SettingsSection::Downloads => icon_download(15.0, c),
        SettingsSection::InGame => icon_trophy(15.0, c),
        SettingsSection::Capture => exo_kit::icon_camera(15.0, c),
        SettingsSection::Account => icon_user(15.0, c),
        SettingsSection::About => icon_info(15.0, c),
    }
}

fn text_size_label(raw: &str) -> &'static str {
    match raw {
        "small" => "Small",
        "large" => "Large",
        _ => "Normal",
    }
}

pub(crate) fn set_row(
    title: &str,
    subtitle: Option<&str>,
    error: Option<&str>,
    highlight: bool,
    control: El,
) -> El {
    let mut left = col().grow(1.0).min_w(0.0).gap(3.0).child(
        text(title.to_string())
            .key(format!("set-label-{title}"))
            .font(TextStyle::new(scaled(13.0), 500.0))
            .color(FG),
    );
    if let Some(sub) = subtitle {
        left = left.child(
            text(sub.to_string())
                .key(format!("set-sub-{title}"))
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(fg_muted())
                .wrap(),
        );
    }
    if let Some(err) = error {
        left = left.child(
            text(err.to_string())
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(bad())
                .wrap(),
        );
    }
    let mut wrap = el().w_full().py(12.0).px(18.0);
    if highlight {
        wrap = wrap.fill(FG.alpha(0.06));
    }
    wrap.child(
        row()
            .w_full()
            .min_h(if subtitle.is_some() { 41.0 } else { 38.0 })
            .gap(16.0)
            .justify_between()
            .center_y()
            .child(left)
            .child(control),
    )
}

fn toggle_row(
    key: &'static str,
    title: &'static str,
    subtitle: Option<&'static str>,
    field: &'static str,
    value: bool,
    highlight: bool,
) -> El {
    set_row(
        title,
        subtitle,
        None,
        highlight,
        switch_pill(key, title, value).on_press(move |s: &mut AppState| {
            s.set_bool_setting(field, !value);
        }),
    )
}

fn section_head(title: &str) -> El {
    row()
        .w_full()
        .min_h(28.0)
        .px(2.0)
        .center_y()
        .justify_between()
        .child(
            text(title.to_string())
                .key(format!("set-sec-head-{title}"))
                .font(type_section())
                .color(FG),
        )
}

fn card_of(rows: Vec<El>) -> El {
    let mut card = settings_card();
    for (idx, r) in rows.into_iter().enumerate() {
        if idx > 0 {
            card = card.child(el().w_full().h(1.0).fill(hairline()));
        }
        card = card.child(r);
    }
    card
}

pub(crate) fn settings_group(title: &str, rows: Vec<El>) -> El {
    col()
        .w_full()
        .gap(8.0)
        .child(section_head(title))
        .child(card_of(rows))
}

fn is_highlighted(s: &AppState, id: &str) -> bool {
    s.wp4.highlight_row.is_some_and(|(rid, _)| rid == id)
}

fn section_general(s: &AppState) -> El {
    let set = &s.settings;
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let discord_classic = set.discord_layout.as_deref() == Some("classic");
    let layout_pills = row()
        .gap(6.0)
        .center_y()
        .child(if !discord_classic {
            set_primary_btn("set-discord-layout-exo", "Exo", None)
                .on_press(|s: &mut AppState| s.set_discord_layout("exo"))
        } else {
            set_btn("set-discord-layout-exo", "Exo", None)
                .on_press(|s: &mut AppState| s.set_discord_layout("exo"))
        })
        .child(if discord_classic {
            set_primary_btn("set-discord-layout-classic", "Classic Discord", None)
                .on_press(|s: &mut AppState| s.set_discord_layout("classic"))
        } else {
            set_btn("set-discord-layout-classic", "Classic Discord", None)
                .on_press(|s: &mut AppState| s.set_discord_layout("classic"))
        });

    let mut rows = vec![
        set_row(
            "Text size",
            None,
            None,
            is_highlighted(s, "general-text-size"),
            select_trigger(
                SelectTrigger::new(
                    "set-text-size-select",
                    "Text size",
                    text_size_label(set.text_size.as_deref().unwrap_or("normal")),
                )
                .flat(true)
                .open(s.open_dropdown == Some(OpenDropdown::TextSize))
                .gamepad_focus(gamepad_focus),
                |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::TextSize),
            ),
        ),
        toggle_row(
            "set-start-win",
            "Open Exo when Windows starts",
            None,
            "startWithWindows",
            set.start_with_windows,
            is_highlighted(s, "general-startup"),
        ),
        set_row(
            "Discord layout",
            Some("Exo's top server bar or classic Discord's left server rail."),
            None,
            false,
            layout_pills,
        ),
    ];
    rows.extend(music_rows(s));

    col()
        .w_full()
        .gap(24.0)
        .child(card_of(rows))
        .child(backup_section(s))
}

fn section_library(s: &AppState) -> El {
    let set = &s.settings;
    let tools_card = settings_card().child(toggle_row(
        "set-lib-tools",
        "Show tools and soundtracks",
        None,
        "showLibraryTools",
        set.show_library_tools,
        is_highlighted(s, "library-tools"),
    ));

    let mut out = col().w_full().gap(24.0).child(stores_section(s)).child(
        col()
            .w_full()
            .gap(8.0)
            .child(section_head("Display"))
            .child(tools_card),
    );

    if let Some(shaders) = shaders_section(s) {
        out = out.child(shaders);
    }
    out
}

fn section_downloads(s: &AppState) -> El {
    let set = &s.settings;
    let root = set
        .default_install_root
        .as_deref()
        .map(str::trim)
        .filter(|p| !p.is_empty());
    let picking = s.wp4.picking_folder;

    let mut folder_btns = row().gap(8.0).center_y();
    if root.is_some() {
        folder_btns = folder_btns.child(
            set_btn("set-install-root-reset", "Reset", None)
                .disabled(picking)
                .on_press(|s: &mut AppState| s.wp4_reset_install_folder()),
        );
    }
    folder_btns = folder_btns.child(
        set_btn(
            "set-install-root-change",
            if picking { "Choosing…" } else { "Change" },
            None,
        )
        .disabled(picking)
        .on_press(|s: &mut AppState| s.wp4_pick_install_folder()),
    );

    let hours_label = download_hours_label(set.download_hours_start, set.download_hours_end);
    let hours_open = s.wp4.download_hours_open;
    let hours_trigger = select_trigger(
        SelectTrigger::new("set-download-hours-select", "Download hours", hours_label)
            .flat(true)
            .open(hours_open),
        |s: &mut AppState, _| s.wp4.download_hours_open = !s.wp4.download_hours_open,
    );

    let mut card = settings_card()
        .child(set_row(
            "Install folder",
            Some(root.unwrap_or("Exo's Games folder")),
            s.wp4.folder_error.as_deref(),
            is_highlighted(s, "downloads-folder"),
            folder_btns,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Download hours",
            None,
            None,
            is_highlighted(s, "downloads-hours"),
            hours_trigger,
        ));

    if hours_open {
        let mut choices = row().w_full().px(18.0).pb(12.0).gap(8.0).flex_wrap();
        for preset in DOWNLOAD_HOURS {
            let active =
                set.download_hours_start == preset.start && set.download_hours_end == preset.end;
            let btn = if active {
                set_primary_btn(format!("set-dl-hours-{}", preset.value), preset.label, None)
            } else {
                set_btn(format!("set-dl-hours-{}", preset.value), preset.label, None)
            }
            .on_press(move |s: &mut AppState| {
                s.wp4_set_download_hours(preset.start, preset.end);
            });
            choices = choices.child(btn);
        }
        card = card.child(choices);
    }

    let upscaler_ac = set.upscaler_anti_cheat_everywhere;
    card = card
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-auto-upd",
            "Update games automatically",
            None,
            "autoUpdateGames",
            set.auto_update_games,
            is_highlighted(s, "downloads-auto-update"),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Update DLSS, FSR and XeSS in anti-cheat games",
            Some("May carry a ban risk. Never touches Riot games or Vanguard."),
            None,
            is_highlighted(s, "downloads-upscalers"),
            switch_pill(
                "set-upscalers-anticheat",
                "Update DLSS, FSR and XeSS in anti-cheat games",
                upscaler_ac,
            )
            .on_press(move |s: &mut AppState| s.wp4_set_upscaler_anticheat(!upscaler_ac)),
        ));

    col().w_full().gap(24.0).child(card)
}

fn section_in_game(s: &AppState) -> El {
    let set = &s.settings;
    let volume = set.achievement_volume.unwrap_or(60).clamp(0, 100);
    let vol_label = if volume == 0 {
        "Off".to_string()
    } else {
        format!("{volume}%")
    };
    let frac = volume as f32 / 100.0;
    let slider = slider_track(
        "set-ach-volume-slider",
        "Pop-up sound",
        160.0,
        frac,
        s.wp4.achievement_slider_dragging,
        false,
    )
    .on_drag(|s: &mut AppState, d, _| {
        let pct = ((d.x / d.w.max(1.0)).clamp(0.0, 1.0) * 20.0).round() as i32 * 5;
        match d.phase {
            Phase::Down | Phase::Move => {
                s.wp4.achievement_slider_dragging = true;
                s.wp4_set_achievement_volume(pct, false);
            }
            Phase::Up => {
                s.wp4.achievement_slider_dragging = false;
                if !d.cancelled() {
                    s.wp4_set_achievement_volume(pct, true);
                }
            }
        }
    });
    let slider = slider_range(
        slider,
        f64::from(volume),
        0.0,
        100.0,
        5.0,
        true,
        |s: &mut AppState, v| s.wp4_set_achievement_volume(v.round() as i32, true),
    );

    let copied = s
        .wp4
        .obs_copied_until
        .is_some_and(|until| s.wp4_now_secs() < until);

    let fps_on =
        s.pc.fps_counter_state
            .as_ref()
            .map(|f| f.on)
            .unwrap_or(set.fps_counter);
    let fps_signout =
        s.pc.fps_counter_state
            .as_ref()
            .is_some_and(|f| f.state == "signout");

    let ach_card = settings_card()
        .child(toggle_row(
            "set-ach-banners",
            "Show achievement pop-ups",
            None,
            "achievementBanners",
            set.achievement_banners,
            is_highlighted(s, "ingame-achievements"),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Pop-up sound",
            None,
            s.wp4.achievement_volume_error.as_deref(),
            is_highlighted(s, "ingame-sound"),
            row()
                .gap(12.0)
                .center_y()
                .child(slider)
                .child(text(vol_label).font(type_small()).color(fg_muted()).w(36.0)),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Test pop-up",
            None,
            s.wp4.achievement_preview_error.as_deref(),
            is_highlighted(s, "ingame-test"),
            set_btn("set-ach-preview", "Show", None)
                .on_press(|s: &mut AppState| s.wp4_preview_achievement()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "OBS overlay URL",
            None,
            None,
            is_highlighted(s, "ingame-obs"),
            set_btn("set-obs-copy", if copied { "Copied" } else { "Copy" }, None).on_press_tx(
                |s: &mut AppState, tx| {
                    tx.copy(OBS_OVERLAY_URL);
                    s.wp4.obs_copied_until = Some(s.wp4_now_secs() + 2.0);
                },
            ),
        ));

    let mut fps_card = settings_card().child(set_row(
        "Show FPS counter",
        fps_signout.then_some("Sign out to finish"),
        None,
        is_highlighted(s, "ingame-fps"),
        switch_pill("set-fps-counter", "Show FPS counter", fps_on)
            .on_press(move |s: &mut AppState| s.toggle_fps_counter(!fps_on)),
    ));

    if fps_on {
        let cur_pos = set.fps_counter_position.as_deref().unwrap_or("right");
        let pos_label = match cur_pos {
            "left" => "Top left",
            "middle" => "Top middle",
            _ => "Top right",
        };
        let gamepad_focus = s.input_mode == InputMode::Gamepad;
        fps_card = fps_card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(set_row(
                "Position",
                None,
                None,
                false,
                select_trigger(
                    SelectTrigger::new("set-fps-position", "Position", pos_label)
                        .flat(true)
                        .open(s.open_dropdown == Some(OpenDropdown::FpsPosition))
                        .gamepad_focus(gamepad_focus),
                    |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::FpsPosition),
                ),
            ));

        const FPS_ITEMS: &[(&str, &str, &str)] = &[
            ("set-fps-low", "low", "1% low"),
            ("set-fps-frametime", "frametime", "Frame time"),
            ("set-fps-gpu", "gpu", "GPU use"),
            ("set-fps-gputemp", "gputemp", "GPU temperature"),
            ("set-fps-cpu", "cpu", "CPU use"),
            ("set-fps-vram", "vram", "VRAM"),
            ("set-fps-ram", "ram", "RAM"),
            ("set-fps-battery", "battery", "Lowest battery"),
            ("set-fps-clock", "clock", "Time of day"),
        ];
        let mut chips = row().w_full().flex_wrap().gap(8.0);
        for &(key, id, label) in FPS_ITEMS {
            let checked = set.fps_counter_items.iter().any(|x| x == id);
            let chip = if checked {
                set_primary_btn(key, label, None)
            } else {
                set_btn(key, label, None)
            };
            chips = chips.child(
                chip.checked(checked)
                    .on_press(move |s: &mut AppState| s.toggle_fps_counter_item(id)),
            );
        }
        fps_card = fps_card.child(el().w_full().h(1.0).fill(hairline())).child(
            col()
                .w_full()
                .py(14.0)
                .px(18.0)
                .gap(10.0)
                .child(
                    text("Also show")
                        .key("set-label-Also show")
                        .font(TextStyle::new(scaled(13.0), 500.0))
                        .color(FG),
                )
                .child(chips),
        );
    }

    col()
        .w_full()
        .gap(24.0)
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(section_head("Achievements"))
                .child(ach_card),
        )
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(section_head("FPS counter"))
                .child(fps_card),
        )
}

fn yt_notification_label(raw: &str) -> &'static str {
    match raw {
        "allow" => "Allow",
        "block" => "Block",
        _ => "Ask",
    }
}

fn next_yt_notification(raw: &str) -> &'static str {
    match raw {
        "ask" => "allow",
        "allow" => "block",
        _ => "ask",
    }
}

fn music_rows(s: &AppState) -> Vec<El> {
    let web_prefs = s.wp4.web_prefs.as_ref().or(s.pc.web_preferences.as_ref());
    let pref_svc = web_prefs.and_then(|w| w.music_service.as_deref());
    let current = match pref_svc {
        Some("spotify") => "Spotify",
        Some("apple") => "Apple Music",
        Some("youtube") => "YouTube Music",
        _ => s.media.music_service.label(),
    };
    let is_youtube = pref_svc == Some("youtube")
        || (pref_svc.is_none()
            && s.media.music_service == crate::media_state::MusicService::YouTube);
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let mut rows = vec![set_row(
        "Music service",
        None,
        s.wp4.web_prefs_error.as_deref(),
        is_highlighted(s, "music-service"),
        select_trigger(
            SelectTrigger::new("set-music-service-select", "Music service", current)
                .flat(true)
                .open(s.open_dropdown == Some(OpenDropdown::MusicService))
                .gamepad_focus(gamepad_focus),
            |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::MusicService),
        ),
    )];
    if is_youtube {
        let notif = web_prefs
            .map(|p| p.notifications.as_str())
            .filter(|v| !v.is_empty())
            .unwrap_or("ask");
        let next = next_yt_notification(notif);
        rows.push(set_row(
            "YouTube Music notifications",
            None,
            None,
            false,
            set_btn("set-yt-notifications", yt_notification_label(notif), None)
                .disabled(s.wp4.web_prefs_saving)
                .on_press(move |s: &mut AppState| s.wp4_set_web_notifications(next)),
        ));
    }
    rows
}

fn about_link(
    key: &'static str,
    label: &'static str,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    el().key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .h(32.0)
        .px(4.0)
        .center_y()
        .radius(6.0)
        .hover(hover_fill())
        .on_press(on_press)
        .child(text(label).font(type_body()).color(fg_muted()))
}

fn section_about(s: &AppState) -> El {
    let ver = s
        .settings
        .app_version
        .as_deref()
        .or_else(|| {
            s.pc.system_about
                .as_ref()
                .and_then(|a| a.version.as_deref())
        })
        .or_else(|| {
            s.pc.update_check
                .as_ref()
                .and_then(|u| u.current.as_deref())
        });
    let upd_avail =
        s.pc.update_check
            .as_ref()
            .is_some_and(|u| u.update_available);
    let upd_title = match ver {
        Some(v) if !v.is_empty() => format!("Exo Launcher {v}"),
        _ => "Exo Launcher".to_string(),
    };
    let upd_status_note = s
        .wp4
        .update_note
        .as_ref()
        .filter(|n| !n.error)
        .map(|n| n.text.clone());
    let upd_err = s
        .wp4
        .update_note
        .as_ref()
        .filter(|n| n.error)
        .map(|n| n.text.as_str());

    let mut card = settings_card().child(set_row(
        &upd_title,
        None,
        upd_err,
        is_highlighted(s, "about-updates"),
        row()
            .gap(10.0)
            .center_y()
            .children(upd_status_note.map(|note| text(note).font(type_body()).color(fg_muted())))
            .children((!upd_avail).then(|| {
                set_btn(
                    "set-check-upd",
                    if s.pc.checking_update {
                        "Checking…"
                    } else {
                        "Check for updates"
                    },
                    None,
                )
                .disabled(s.pc.checking_update)
                .on_press(|s: &mut AppState| s.check_app_update())
            }))
            .children(upd_avail.then(|| {
                set_primary_btn("set-install-upd", "Install update", None)
                    .on_press(|s: &mut AppState| s.install_app_update())
            })),
    ));

    if let Some(ref prog) = s.pc.update_progress {
        let pct = (prog.percent.unwrap_or(0.0) / 100.0).clamp(0.0, 1.0) as f32;
        card = card.child(
            col()
                .w_full()
                .px(18.0)
                .pb(16.0)
                .gap(6.0)
                .child(text(prog.status.clone()).font(type_small()).color(good()))
                .child(progress_bar(320.0, pct)),
        );
    }

    let caches_busy = s.wp4.caches_busy;
    card = card
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Clear caches",
            s.wp4.caches_done.then_some("Caches cleared."),
            s.wp4.caches_error.as_deref(),
            is_highlighted(s, "about-caches"),
            set_btn(
                "set-clear-caches",
                if caches_busy { "Clearing…" } else { "Clear" },
                None,
            )
            .disabled(caches_busy)
            .on_press(|s: &mut AppState| s.wp4_clear_caches()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(
            row()
                .w_full()
                .px(14.0)
                .py(12.0)
                .gap(20.0)
                .center_y()
                .flex_wrap()
                .child(about_link(
                    "set-about-report",
                    "Report a problem",
                    |s: &mut AppState| s.wp4_open_report(),
                ))
                .child(about_link(
                    "set-about-tour",
                    "Take the tour",
                    |s: &mut AppState| s.start_tour(),
                ))
                .child(about_link(
                    "set-about-changelog",
                    "Changelog",
                    |s: &mut AppState| s.wp4_open_url(CHANGELOG_URL),
                ))
                .child(about_link(
                    "set-about-privacy",
                    "Privacy",
                    |s: &mut AppState| s.wp4_open_url(PRIVACY_URL),
                ))
                .child(about_link(
                    "set-about-support",
                    "Support Exo",
                    |s: &mut AppState| s.wp4_open_url(SUPPORT_URL),
                )),
        );

    col()
        .w_full()
        .gap(24.0)
        .child(card)
        .child(privacy_section(s))
}

pub fn view(s: &AppState) -> El {
    let q = s.pc.settings_query.text.trim().to_string();
    let searching = !q.is_empty();
    let hits = if searching {
        search_settings(&q)
    } else {
        Vec::new()
    };

    let back_btn = el()
        .key("settings-back")
        .role("button")
        .label("Back")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(30.0, 30.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_settings())
        .child(icon_chevron_left(16.0, fg_muted()));

    let mut search_bar = row()
        .w_full()
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .hit()
        .cursor(Cursor::Text)
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border(1.0, card_border())
        .child(icon_search(14.0, fg_muted()))
        .child(
            text_input(&s.pc.settings_query, type_body())
                .key("settings-search")
                .label("Search settings")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Search settings", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| &mut s.pc.settings_query)),
        );
    if searching {
        search_bar = search_bar.child(
            el().key("settings-search-clear")
                .role("button")
                .label("Clear settings search")
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .size(22.0, 22.0)
                .radius(11.0)
                .center()
                .hover(hover_fill())
                .on_press(|s: &mut AppState| {
                    s.pc.settings_query.text.clear();
                    s.pc.settings_query.sync_invariants();
                })
                .child(icon_close(12.0, fg_muted())),
        );
    }

    let nav_body = if searching {
        if hits.is_empty() {
            el().w_full().px(12.0).py(8.0).child(
                text("No matching settings")
                    .font(type_small())
                    .color(fg_muted()),
            )
        } else {
            col()
                .key("settings-search-hits")
                .role("listbox")
                .label("Matching settings")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .gap(2.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .children(hits.into_iter().map(|entry| {
                    col()
                        .key(format!("set-hit-{}", entry.id))
                        .role("option")
                        .label(entry.label)
                        .focusable(true)
                        .hit()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(12.0)
                        .py(8.0)
                        .gap(2.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .press(pressed_fill())
                        .on_press(move |s: &mut AppState| s.wp4_jump_to_setting(entry))
                        .child(text(entry.label).font(type_body()).color(FG).wrap())
                        .child(text(entry.page_label).font(type_small()).color(fg_muted()))
                }))
        }
    } else {
        col()
            .w_full()
            .gap(3.0)
            .children(SettingsSection::ALL.into_iter().map(|sec| {
                let active = s.pc.settings_section == sec;
                let mut item = el()
                    .key(sec.key())
                    .role("tab")
                    .label(nav_label(sec))
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .w_full()
                    .min_h(36.0)
                    .radius(RADIUS_SMALL)
                    .fill(TRANSPARENT)
                    .hover(if active { TRANSPARENT } else { hover_fill() })
                    .press(if active { TRANSPARENT } else { pressed_fill() })
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.pc.settings_section = sec;
                        s.pc.settings_query.text.clear();
                        s.pc.settings_query.sync_invariants();
                    });
                if active {
                    item = item.child(
                        el().key("settings-nav-pill")
                            .matched("settings-nav-pill")
                            .motion(SPRING_SNAPPY)
                            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                            .radius(RADIUS_SMALL)
                            .fill(selected_fill())
                            .inert(),
                    );
                }
                item.child(
                    row()
                        .w_full()
                        .min_h(36.0)
                        .py(6.0)
                        .px(12.0)
                        .gap(10.0)
                        .center_y()
                        .child(nav_icon(sec, active))
                        .child(
                            text(nav_label(sec))
                                .font(if active { type_section() } else { type_body() })
                                .color(if active { FG } else { fg_muted() }),
                        ),
                )
            }))
    };

    let sidebar = col()
        .key("settings-sidebar")
        .w(scaled(256.0))
        .h_full()
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .fill(surface_card())
        .border(1.0, card_border())
        .py(14.0)
        .px(12.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(2.0)
                .gap(6.0)
                .center_y()
                .child(back_btn)
                .child(
                    text("Settings")
                        .key("settings-sidebar-title")
                        .font(type_title())
                        .color(FG),
                ),
        )
        .child(search_bar)
        .child(nav_body);

    let title = nav_label(s.pc.settings_section);
    let body = match s.pc.settings_section {
        SettingsSection::General => section_general(s),
        SettingsSection::Library => section_library(s),
        SettingsSection::Downloads => section_downloads(s),
        SettingsSection::InGame => section_in_game(s),
        SettingsSection::Capture => crate::captures::settings_body(s),
        SettingsSection::Account => account_section(s),
        SettingsSection::About => section_about(s),
    };

    let right_col = col()
        .key("settings-scroll")
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .chain_scroll_offset(crate::shell::bar_scroll_watch)
        .child(
            col()
                .key(format!("settings-pane-{:?}", s.pc.settings_section))
                .transition(
                    Transition::offset(0.0, 10.0)
                        .combined(Transition::opacity())
                        .springs(SpringPair::new(SPRING_SMOOTH, SPRING_QUICK)),
                )
                .w_full()
                .max_w(scaled(920.0))
                .pt(20.0)
                .px(36.0)
                .pb(56.0)
                .child(
                    row()
                        .key("settings-page-title")
                        .h(scaled(40.0))
                        .center_y()
                        .child(
                            text(title)
                                .key("settings-page-title-text")
                                .font(type_page().letter_spacing(-0.02))
                                .color(FG),
                        ),
                )
                .child(col().w_full().pt(18.0).child(body)),
        );

    col()
        .key("room-settings")
        .w_full()
        .h_full()
        .pt(52.0)
        .px(GAP)
        .pb(GAP)
        .fill(BG)
        .child(
            row()
                .key("settings-shell")
                .w_full()
                .h_full()
                .gap(GAP)
                .items_start()
                .child(sidebar)
                .child(right_col),
        )
}

#[cfg(test)]
mod tests {
    use crate::pc_state::SettingsSection;
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_LIQUID_GLASS;
    use rune::{Env, Runtime};

    #[test]
    fn settings_gridlines_gutter_and_glass_bound() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| s.switch_room(Room::Settings));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let glass_draws = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count();
        assert!(
            glass_draws <= 3,
            "Settings: {glass_draws} Liquid Glass draws, bound 3"
        );

        let (shell_rect, _) = rt.presented("settings-shell").expect("settings-shell");
        let (sidebar_rect, _) = rt.presented("settings-sidebar").expect("settings-sidebar");
        let (scroll_rect, _) = rt.presented("settings-scroll").expect("settings-scroll");
        let (pane_rect, _) = rt
            .presented("settings-pane-General")
            .expect("settings-pane-General");

        assert!(
            (shell_rect[2] - 1900.0).abs() <= 1.0,
            "expected inset 1900px settings shell, got {}",
            shell_rect[2]
        );
        assert!(
            (sidebar_rect[0] - 10.0).abs() <= 1.0 && (sidebar_rect[2] - 256.0).abs() <= 1.0,
            "expected floating 256px sidebar at x=10, got {:?}",
            sidebar_rect
        );
        assert!(
            (scroll_rect[0] - 276.0).abs() <= 1.0 && (scroll_rect[2] - 1634.0).abs() <= 1.0,
            "expected right scroll pane at x=276 w=1634, got {:?}",
            scroll_rect
        );
        assert!(
            (pane_rect[0] - 276.0).abs() <= 1.0 && (pane_rect[2] - 920.0).abs() <= 1.0,
            "expected left-aligned 920px settings inner pane at x=276, got {:?}",
            pane_rect
        );

        let (title_rect, _) = rt
            .presented("settings-page-title-text")
            .expect("settings-page-title-text");
        let (sec_rect, _) = rt
            .presented("set-sec-head-Backups")
            .expect("set-sec-head-Backups");
        let (row1_rect, _) = rt
            .presented("set-label-Text size")
            .expect("set-label-Text size");
        let (row2_rect, _) = rt
            .presented("set-label-Last backup")
            .expect("set-label-Last backup");

        assert!(
            (title_rect[0] - 312.0).abs() <= 1.5,
            "expected page title at x=312, got {}",
            title_rect[0]
        );
        assert!(
            (sec_rect[0] - 314.0).abs() <= 1.5,
            "expected section head at x=314, got {}",
            sec_rect[0]
        );
        assert!(
            (row1_rect[0] - 330.0).abs() <= 1.5,
            "expected row label at x=330 (312+18), got {}",
            row1_rect[0]
        );
        assert!(
            (row2_rect[0] - row1_rect[0]).abs() <= 1.0,
            "backup row label x={} missed primary row label x={}",
            row2_rect[0],
            row1_rect[0]
        );
    }

    #[test]
    fn settings_capture_matches_other_cards_and_reports_missing_install() {
        let mut state = AppState::default();
        state.status = crate::state::HostStatus::Standalone;
        state.switch_room(Room::Settings);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        let (general_row, _) = rt.presented("set-label-Text size").expect("Text size");
        let (general_head, _) = rt.presented("set-sec-head-Backups").expect("Backups");

        rt.update(|s, _| s.pc.settings_section = SettingsSection::Capture);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        let (head, _) = rt.presented("set-sec-head-Recording").expect("Recording");
        assert!(
            (head[0] - general_head[0]).abs() <= 1.0,
            "{head:?} vs {general_head:?}"
        );

        rt.update(|s, _| {
            s.media.capture_status = Some(exo_rpc::CaptureStatusReply {
                ok: true,
                present: false,
                ..Default::default()
            });
        });
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        let (label, _) = rt
            .presented("set-label-Exo Capture")
            .expect("Exo Capture row");
        assert!(
            (label[0] - general_row[0]).abs() <= 1.0,
            "{label:?} vs {general_row:?}"
        );
        assert!(rt.find("set-sec-head-Exo Capture").is_none());
        assert!(rt.find("cap-set-get").is_none());
        assert!(rt.presented("set-sub-Exo Capture").is_some());
    }

    #[test]
    fn settings_section_switch_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::default();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::Settings);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("switch to Library", |rt| Run::tap(rt, "set-sec-library"));
            run.play(0.5);
            run.act("switch to About", |rt| Run::tap(rt, "set-sec-about"));
            run.play(0.5);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn settings_search_jumps_to_target_section_and_clears_query() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Settings);
            s.pc.settings_query = rune::TextEdit::new("obs");
        });
        rt.frame(0.0);
        assert!(rt.presented("set-hit-ingame-obs").is_some());
        rt.update(|s, _| {
            let hits = crate::settings_state::search_settings("obs");
            s.wp4_jump_to_setting(hits[0]);
        });
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.pc.settings_section, SettingsSection::InGame);
        assert!(rt.state.pc.settings_query.text.is_empty());
        assert!(rt.presented("set-label-OBS overlay URL").is_some());
    }

    #[test]
    fn wp4_overlays_steam_signin_account_sheet_clients_gate_and_events_render_cleanly() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Settings);
            s.pc.settings_section = SettingsSection::About;
            s.wp4_open_report();
        });
        rt.frame(0.0);
        assert!(rt.presented("report-problem-modal").is_some());
        rt.update(|s, _| {
            assert!(s.dismiss_top_layer());
        });
        rt.frame(1.0 / 60.0);
        assert!(!rt.state.wp4.report_open);

        rt.update(|s, _| {
            s.pc.settings_section = SettingsSection::Library;
            s.wp4.stores.push(exo_rpc::StoreStatus {
                store: "steam".into(),
                display_name: "Steam".into(),
                agent_present: true,
                client_present: Some(true),
                signed_in: true,
                ..Default::default()
            });
            s.wp4.steam_panel_open = true;
            s.wp4.steam_step = Some(exo_rpc::SteamSignInStep {
                state: "waiting".into(),
                challenge_url: Some("https://s.team/q/1/1234567890123456".into()),
                interval_seconds: Some(5),
                ..Default::default()
            });
        });
        rt.frame(2.0 / 60.0);
        assert!(rt.presented("steam-library-panel").is_some());
        rt.update(|s, _| {
            s.wp4_steam_switch_mode(crate::settings_state::SteamSignInMode::Password);
        });
        rt.frame(3.0 / 60.0);
        assert!(rt.presented("steam-pass-input").is_some());
        assert!(rt.state.wp4.steam_pass.is_masked());

        rt.update(|s, _| {
            let events = s.events.clone();
            assert!(events.dispatch(
                s,
                exo_rpc::HostEvent::BackupStopped(exo_rpc::BackupStoppedEvent {
                    sentence: "Backups paused because disk is full.".into(),
                    since: "2026-11-23".into(),
                })
            ));
            assert!(events.dispatch(
                s,
                exo_rpc::HostEvent::GiftsChanged(exo_rpc::StoreGiftsReply {
                    ok: true,
                    steam_signed_in: true,
                    ..Default::default()
                })
            ));
            assert!(events.dispatch(
                s,
                exo_rpc::HostEvent::ShadersEvent(exo_rpc::ShadersEventPayload {
                    name: "state".into(),
                    data: serde_json::json!({ "recorder": { "auto": true } }),
                })
            ));
        });
        assert_eq!(
            rt.state.wp4.backup_note.as_deref(),
            Some("Backups paused because disk is full.")
        );
        assert_eq!(rt.state.wp4.gifts_steam_signed_in, Some(true));
        assert!(rt.state.wp4.shaders_auto);

        rt.update(|s, _| {
            s.status = crate::state::HostStatus::Connected;
            s.wp4.account_overview = Some(exo_rpc::ExoAccountOverview {
                signed_in: false,
                ..Default::default()
            });
        });
        rt.frame(4.0 / 60.0);
        assert!(rt.presented("exo-account-sheet").is_some());

        rt.update(|s, _| {
            s.wp4_continue_without_account();
            s.settings.onboarding_complete = Some(true);
            s.pc.clients = Some(exo_rpc::ClientsStatus {
                ok: true,
                needs_fix: true,
                problem: None,
                epic: exo_rpc::ClientsEpic {
                    layout: "old".into(),
                    ..Default::default()
                },
                steam: exo_rpc::ClientsSteam {
                    channel: "stable".into(),
                },
            });
        });
        rt.frame(5.0 / 60.0);
        assert!(rt.presented("clients-start-gate").is_some());
    }

    #[test]
    fn setup_pc_defaults_count_is_nonzero_when_tuning_items_exist() {
        let mut s = AppState::default();
        s.setup.picks = None;
        s.pc.tuning = Some(exo_rpc::SystemTuningState {
            items: vec![
                exo_rpc::TuningItem {
                    key: "game_mode".into(),
                    label: "Game Mode".into(),
                    detail: "Windows Game Mode".into(),
                    on: true,
                    ..Default::default()
                },
                exo_rpc::TuningItem {
                    key: "hags".into(),
                    label: "HAGS".into(),
                    detail: "GPU scheduling".into(),
                    on: false,
                    ..Default::default()
                },
            ],
            ..Default::default()
        });
        let picks = s.effective_setup_picks();
        assert_eq!(picks, vec!["game_mode".to_string()]);
    }

    #[test]
    fn settings_parity_room_landing_dropdown_cursors_and_slider_pointer() {
        let mut s = AppState::default();
        s.room = Room::Library;
        s.toggle_settings();
        assert_eq!(s.room, Room::Settings);
        assert_eq!(s.pc.settings_section, SettingsSection::Library);

        s.toggle_settings();
        assert_eq!(s.room, Room::Library);
        s.room = Room::Music;
        s.toggle_settings();
        assert_eq!(s.pc.settings_section, SettingsSection::General);

        s.toggle_settings();
        s.room = Room::Store;
        s.toggle_settings();
        assert_eq!(s.pc.settings_section, SettingsSection::General);

        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Settings);
            s.pc.settings_section = SettingsSection::General;
            s.toggle_dropdown(crate::state::OpenDropdown::TextSize);
        });
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let (bx, by) = rt.find("nav-library").expect("nav-library");
        rt.pointer_move(bx, by);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Pointer);

        let (tx, ty) = rt
            .find("set-text-size-select")
            .expect("set-text-size-select");
        rt.pointer_move(tx, ty);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Pointer);

        let (ox, oy) = rt.find("dd-text-size-opt-normal").expect("dropdown option");
        rt.pointer_move(ox, oy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Default);

        rt.update(|s, _| {
            s.close_dropdown();
            s.pc.settings_section = SettingsSection::InGame;
            s.settings.fps_counter = true;
        });
        rt.frame(1.0 / 60.0);
        let (sx, sy) = rt
            .find("set-ach-volume-slider")
            .expect("achievement volume slider");
        rt.pointer_move(sx, sy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Pointer);
        assert!(rt.presented("set-fps-position").is_some());
    }
}
