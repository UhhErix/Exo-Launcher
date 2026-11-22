use crate::pc_state::SettingsSection;
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, fg_muted, filter_pill, glass_button, glass_rim, hairline, hover_fill,
    icon_check, icon_cpu, icon_download, icon_folder, icon_refresh, icon_search, icon_settings,
    icon_sparkles, icon_user, icon_wrench, primary_button, progress_bar, section_card,
    setting_row, stat_chip, status_badge, subtle_fill, switch_pill, type_body, type_control,
    type_page, type_section, type_small,
};
use rune::{El, col, edit_access, el, row, text, text_input};

fn section_icon(sec: SettingsSection, active: bool) -> El {
    let c = if active { PRIMARY_FG } else { fg_muted() };
    match sec {
        SettingsSection::General => icon_settings(14.0, c),
        SettingsSection::Account => icon_user(14.0, c),
        SettingsSection::Appearance => icon_sparkles(14.0, c),
        SettingsSection::Graphics => icon_cpu(14.0, c),
        SettingsSection::Updates => icon_download(14.0, c),
        SettingsSection::About => icon_check(14.0, c),
    }
}

fn matches_query(query: &str, haystack: &str) -> bool {
    query.is_empty() || haystack.to_lowercase().contains(query)
}

fn toggle_setting(
    key: &'static str,
    title: &'static str,
    desc: &'static str,
    field: &'static str,
    value: bool,
) -> El {
    setting_row(
        title,
        desc,
        switch_pill(key, title, value).on_press(move |s: &mut AppState| {
            s.set_bool_setting(field, !value);
        }),
    )
}

fn general_section(s: &AppState, q: &str) -> Option<El> {
    let set = &s.settings;
    let items: Vec<(&'static str, &'static str, &'static str, &'static str, bool)> = vec![
        (
            "set-start-win",
            "Start with Windows",
            "Open Exo quietly in the background when you sign in.",
            "startWithWindows",
            set.start_with_windows,
        ),
        (
            "set-start-max",
            "Start maximized",
            "Open the launcher filling the display.",
            "startMaximized",
            set.start_maximized,
        ),
        (
            "set-lib-tools",
            "Show library tools",
            "Keep Activate key and library quick controls visible in the header.",
            "showLibraryTools",
            set.show_library_tools,
        ),
        (
            "set-pause-dl",
            "Pause downloads while playing",
            "Automatically pause background game downloads as soon as a game launches.",
            "pauseDownloadsWhilePlaying",
            set.pause_downloads_while_playing,
        ),
        (
            "set-awake-dl",
            "Keep PC awake for downloads",
            "Prevent Windows sleep while a game download or patch is active.",
            "keepAwakeDownloads",
            set.keep_awake_downloads,
        ),
        (
            "set-auto-upd",
            "Auto-update games when idle",
            "Keep installed games patched while nobody is using the PC.",
            "autoUpdateGames",
            set.auto_update_games,
        ),
        (
            "set-quiet-clients",
            "Keep store clients quiet",
            "Suppress noisy store client popups and idle background helpers.",
            "storeClientsQuiet",
            set.store_clients_quiet,
        ),
    ];

    let matched: Vec<El> = items
        .into_iter()
        .filter(|(_, title, desc, _, _)| matches_query(q, &format!("{title} {desc} general downloads")))
        .map(|(k, title, desc, field, val)| toggle_setting(k, title, desc, field, val))
        .collect();

    if matched.is_empty() {
        return None;
    }

    let mut card = section_card(
        "General & Downloads",
        Some("Startup behavior, download protection, and quiet store clients.".into()),
    );
    for (idx, row_el) in matched.into_iter().enumerate() {
        if idx > 0 {
            card = card.child(el().w_full().h(1.0).fill(hairline()));
        }
        card = card.child(row_el);
    }
    Some(card)
}

fn account_section(s: &AppState, q: &str) -> Option<El> {
    if !matches_query(q, "account sync exo email riot playtime hours sign out") {
        return None;
    }
    let acct = &s.pc.account;
    let status = if acct.signed_in {
        status_badge("Signed in".into(), BadgeTone::Good)
    } else {
        status_badge("Local only".into(), BadgeTone::Neutral)
    };
    let email_text = acct
        .email
        .clone()
        .unwrap_or_else(|| "Sign in to sync settings and playtime across PCs.".into());

    let mut acct_actions = row().gap(8.0).center_y();
    if acct.signed_in {
        acct_actions = acct_actions
            .child(
                glass_button("set-acct-sync", "Sync now", Some(icon_refresh(13.0, FG)))
                    .on_press(|s: &mut AppState| s.sync_exo_account()),
            )
            .child(
                glass_button("set-acct-signout", "Sign out", None)
                    .on_press(|s: &mut AppState| s.sign_out_exo_account()),
            );
    } else {
        acct_actions = acct_actions.child(status_badge("Available in Settings".into(), BadgeTone::Neutral));
    }

    let riot_label = s
        .settings
        .riot_id
        .clone()
        .unwrap_or_else(|| "Not linked — refresh once Riot Client has run".into());

    Some(
        col()
            .w_full()
            .gap(14.0)
            .child(
                section_card("Exo Account", Some(email_text))
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(status)
                            .child(acct_actions),
                    ),
            )
            .child(
                section_card(
                    "Riot Playtime Sync",
                    Some(format!("Riot ID: {riot_label}")),
                )
                .child(
                    row().gap(8.0).center_y().child(
                        glass_button(
                            "set-riot-refresh",
                            "Refresh Riot hours",
                            Some(icon_refresh(13.0, FG)),
                        )
                        .on_press(|s: &mut AppState| s.refresh_riot_playtime()),
                    ),
                ),
            ),
    )
}

fn appearance_section(s: &AppState, q: &str) -> Option<El> {
    if !matches_query(q, "appearance motion overlay fps counter position achievement banners text size") {
        return None;
    }
    let set = &s.settings;
    let cur_pos = set
        .fps_counter_position
        .as_deref()
        .unwrap_or("top-right");
    let positions = [
        ("top-left", "Top left"),
        ("top-right", "Top right"),
        ("bottom-left", "Bottom left"),
        ("bottom-right", "Bottom right"),
    ];

    let pos_pills = row().gap(6.0).flex_wrap().children(positions.into_iter().map(|(val, lbl)| {
        let active = cur_pos.eq_ignore_ascii_case(val);
        let key: &'static str = match val {
            "top-left" => "set-fps-tl",
            "top-right" => "set-fps-tr",
            "bottom-left" => "set-fps-bl",
            _ => "set-fps-br",
        };
        filter_pill(key, lbl.into(), active).on_press(move |s: &mut AppState| {
            s.set_string_setting("fpsCounterPosition", val);
        })
    }));

    Some(
        section_card(
            "Appearance & In-Game Overlay",
            Some("Motion comfort, FPS counter placement, and achievement toasts.".into()),
        )
        .child(toggle_setting(
            "set-reduce-motion",
            "Reduce motion",
            "Shorten transitions and keep surfaces calm.",
            "reduceMotion",
            set.reduce_motion,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_setting(
            "set-fps-counter",
            "FPS counter overlay",
            "Lightweight real-time frame rate HUD during gameplay.",
            "fpsCounter",
            set.fps_counter,
        ))
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(12.0)
                .child(text("Overlay corner").font(type_small()).color(fg_muted()))
                .child(pos_pills),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_setting(
            "set-ach-banners",
            "Achievement banners",
            "Show native unlocked achievement toasts when games award them.",
            "achievementBanners",
            set.achievement_banners,
        )),
    )
}

fn graphics_section(s: &AppState, q: &str) -> Option<El> {
    if !matches_query(q, "graphics hdr upscaler dlss fsr chipset auto update monitor") {
        return None;
    }
    let set = &s.settings;
    let hdr_note = match &s.pc.hdr {
        Some(h) if h.supported => format!(
            "{} · {} nits · {}",
            h.name.as_deref().unwrap_or("Primary display"),
            h.peak_nits.map_or("—".into(), |n| n.to_string()),
            if h.on { "HDR on" } else { "SDR desktop" }
        ),
        Some(_) => "Primary display does not report HDR support.".into(),
        None => "Checking display HDR capability…".into(),
    };

    Some(
        section_card("Graphics, HDR & Drivers", Some(hdr_note))
            .child(toggle_setting(
                "set-hdr-games",
                "HDR for HDR games",
                "Automatically switch Windows HDR on for verified HDR games and restore SDR after.",
                "hdrForGames",
                set.hdr_for_games,
            ))
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(toggle_setting(
                "set-auto-upscalers",
                "Auto-update DLSS & FSR upscalers",
                "Keep supported single-player upscalers updated when new versions release.",
                "autoUpdateUpscalers",
                set.auto_update_upscalers,
            ))
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(toggle_setting(
                "set-auto-chipset",
                "Auto-update chipset drivers",
                "Check for official AMD or Intel chipset updates quietly in the background.",
                "autoUpdateChipset",
                set.auto_update_chipset,
            )),
    )
}

fn updates_section(s: &AppState, q: &str) -> Option<El> {
    if !matches_query(q, "updates delta version install check logs cache repair") {
        return None;
    }
    let upd = s.pc.update_check.as_ref();
    let current = upd
        .and_then(|u| u.current.clone())
        .or_else(|| s.pc.system_about.as_ref().and_then(|a| a.version.clone()))
        .unwrap_or_else(|| "3.29.0".into());
    let latest = upd.and_then(|u| u.latest.clone()).unwrap_or_else(|| current.clone());
    let has_update = upd.is_some_and(|u| u.update_available);

    let badge = if has_update {
        status_badge(format!("Update {latest} ready"), BadgeTone::Good)
    } else if s.pc.checking_update {
        status_badge("Checking…".into(), BadgeTone::Warn)
    } else {
        status_badge(format!("v{current} · Up to date"), BadgeTone::Neutral)
    };

    let mut actions = row().gap(8.0).flex_wrap().center_y();
    if has_update {
        actions = actions.child(
            primary_button(
                "set-upd-install",
                if s.pc.installing_update {
                    "Installing…"
                } else {
                    "Install update"
                },
                Some(icon_download(13.0, PRIMARY_FG)),
            )
            .on_press(|s: &mut AppState| s.install_app_update()),
        );
    }
    actions = actions
        .child(
            glass_button(
                "set-upd-check",
                if s.pc.checking_update {
                    "Checking…"
                } else {
                    "Check for updates"
                },
                Some(icon_refresh(13.0, FG)),
            )
            .on_press(|s: &mut AppState| s.check_app_update()),
        )
        .child(
            glass_button(
                "set-repair-cache",
                "Repair cover cache",
                Some(icon_wrench(13.0, FG)),
            )
            .on_press(|s: &mut AppState| s.repair_app_cache()),
        )
        .child(
            glass_button("set-open-logs", "Open logs", Some(icon_folder(13.0, FG)))
                .on_press(|s: &mut AppState| s.open_app_logs()),
        );

    let mut card = section_card(
        "Updates & Maintenance",
        upd.and_then(|u| u.message.clone())
            .or_else(|| Some("Signed delta updates apply cleanly between game sessions.".into())),
    )
    .child(row().w_full().justify_between().center_y().child(badge))
    .child(actions);

    if let Some(ref prog) = s.pc.update_progress {
        let pct = prog.percent.unwrap_or(0.0) as f32 / 100.0;
        card = card.child(
            col()
                .w_full()
                .gap(6.0)
                .child(text(prog.status.clone()).font(type_small()).color(FG))
                .child(progress_bar(420.0, pct)),
        );
    }
    Some(card)
}

fn about_section(s: &AppState, q: &str) -> Option<El> {
    if !matches_query(q, "about tour guide hardware cpu gpu ram windows rune dx12") {
        return None;
    }
    let about = s.pc.system_about.as_ref();
    let specs = row()
        .w_full()
        .gap(10.0)
        .flex_wrap()
        .child(stat_chip("Renderer", "Rune · DirectX 12".into()))
        .children(about.and_then(|a| a.cpu.clone()).map(|v| stat_chip("CPU", v)))
        .children(about.and_then(|a| a.gpu.clone()).map(|v| stat_chip("GPU", v)))
        .children(
            about
                .and_then(|a| a.ram_gb)
                .map(|r| stat_chip("Memory", format!("{:.0} GB", r))),
        )
        .children(about.and_then(|a| a.os.clone()).map(|v| stat_chip("Windows", v)));

    Some(
        section_card(
            "About Exo Launcher",
            Some("Native Rust + DirectX 12 interface over the Exo host runtime.".into()),
        )
        .child(specs)
        .child(
            row().gap(8.0).center_y().child(
                primary_button(
                    "set-take-tour",
                    "Take the tour",
                    Some(icon_sparkles(13.0, PRIMARY_FG)),
                )
                .on_press(|s: &mut AppState| s.start_tour()),
            ),
        ),
    )
}

pub fn view(s: &AppState) -> El {
    let query_lower = s.pc.settings_query.text.trim().to_lowercase();
    let searching = !query_lower.is_empty();

    let search_input = text_input(&s.pc.settings_query, type_body())
        .key("settings-search")
        .label("Search settings")
        .w_full()
        .single_line()
        .color(FG)
        .placeholder("Search settings…", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.pc.settings_query));

    let search_box = row()
        .w_full()
        .h(36.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(BG.alpha(0.55))
        .border(1.0, glass_rim())
        .child(icon_search(13.0, fg_muted()))
        .child(search_input);

    let nav_items = SettingsSection::ALL.into_iter().map(|sec| {
        let active = !searching && s.pc.settings_section == sec;
        row()
            .key(sec.key())
            .role("tab")
            .label(sec.label())
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .w_full()
            .h(34.0)
            .px(12.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(if active { FG } else { exo_kit::TRANSPARENT })
            .hover(if active { FG } else { hover_fill() })
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.pc.settings_section = sec;
                s.pc.settings_query.text.clear();
                s.pc.settings_query.sync_invariants();
            })
            .child(section_icon(sec, active))
            .child(
                text(sec.label())
                    .font(type_control())
                    .color(if active { PRIMARY_FG } else { FG }),
            )
    });

    let sidebar = col()
        .w(220.0)
        .gap(10.0)
        .child(search_box)
        .child(col().w_full().gap(4.0).children(nav_items));

    let mut cards: Vec<El> = Vec::new();
    if searching {
        for c in [
            general_section(s, &query_lower),
            account_section(s, &query_lower),
            appearance_section(s, &query_lower),
            graphics_section(s, &query_lower),
            updates_section(s, &query_lower),
            about_section(s, &query_lower),
        ]
        .into_iter()
        .flatten()
        {
            cards.push(c);
        }
    } else {
        let one = match s.pc.settings_section {
            SettingsSection::General => general_section(s, ""),
            SettingsSection::Account => account_section(s, ""),
            SettingsSection::Appearance => appearance_section(s, ""),
            SettingsSection::Graphics => graphics_section(s, ""),
            SettingsSection::Updates => updates_section(s, ""),
            SettingsSection::About => about_section(s, ""),
        };
        if let Some(c) = one {
            cards.push(c);
        }
    }

    let pane = if cards.is_empty() {
        col().grow(1.0).min_w(0.0).child(
            el().w_full()
                .h(140.0)
                .radius(RADIUS_PANEL)
                .fill(subtle_fill())
                .border(1.0, hairline())
                .center()
                .child(
                    text("No matching settings.")
                        .font(type_section())
                        .color(fg_muted()),
                ),
        )
    } else {
        col().grow(1.0).min_w(0.0).max_w(820.0).gap(14.0).children(cards)
    };

    col()
        .w_full()
        .pt(74.0)
        .px(28.0)
        .pb(36.0)
        .gap(18.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Settings").font(type_page()).color(FG))
                .children(
                    s.notice
                        .as_ref()
                        .map(|n| status_badge(n.clone(), BadgeTone::Neutral)),
                ),
        )
        .child(row().w_full().gap(24.0).items_start().child(sidebar).child(pane))
}
