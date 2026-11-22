use crate::pc_state::{AppsFilter, AppsSubTab, PcTab, format_bytes};
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, bad, fg_faint, fg_muted, filter_pill, glass_button, glass_rim, good, hairline,
    hex, icon_app_window, icon_cpu, icon_external, icon_hard_drive, icon_keyboard,
    icon_monitor, icon_refresh, icon_search, icon_shield, icon_trash, icon_volume,
    icon_volume_off, icon_zap, primary_button, section_card, segmented_bar, setting_row,
    stat_chip, status_badge, subtle_fill, switch_pill, type_body, type_control, type_label,
    type_page, type_section, type_small, warn,
};
use exo_rpc::{DriveInfo, KeyboardReadReply};
use rune::{Color, El, col, edit_access, el, row, text, text_input};

fn tab_icon(tab: PcTab, active: bool) -> El {
    let c = if active { PRIMARY_FG } else { FG };
    match tab {
        PcTab::ExoAi => icon_zap(14.0, c),
        PcTab::Apps => icon_app_window(14.0, c),
        PcTab::Storage => icon_hard_drive(14.0, c),
        PcTab::Devices => icon_keyboard(14.0, c),
    }
}

fn exo_ai_tab(s: &AppState) -> El {
    let mut stack = col().w_full().gap(16.0);

    let ai = s.pc.exo_ai.as_ref();
    let auto_apply = ai.is_some_and(|a| a.auto_apply_best_settings);
    let drift = ai.is_some_and(|a| a.background_drift_check);
    let fan_preset = ai.map(|a| a.fans.preset.as_str()).unwrap_or("auto");

    let ai_summary = match ai {
        Some(a) if a.gaming.active => format!(
            "Optimizing {} · {} background processes demoted",
            a.gaming.game_title.as_deref().unwrap_or("Active game"),
            a.gaming.demoted_count
        ),
        Some(a) => format!(
            "{} safe optimizations active · {} needing attention",
            a.auto_applied.len(),
            a.needs_attention.len()
        ),
        None => "Loading Exo AI status…".into(),
    };

    let fan_presets = [
        ("auto", "Auto"),
        ("quiet", "Quiet"),
        ("balanced", "Balanced"),
        ("cool", "Cool"),
    ];
    let fan_pills = row().gap(6.0).flex_wrap().children(fan_presets.into_iter().map(|(val, lbl)| {
        let active = fan_preset.eq_ignore_ascii_case(val);
        let key: &'static str = match val {
            "quiet" => "ai-fan-quiet",
            "balanced" => "ai-fan-balanced",
            "cool" => "ai-fan-cool",
            _ => "ai-fan-auto",
        };
        filter_pill(key, lbl.into(), active).on_press(move |s: &mut AppState| {
            s.set_exo_ai_fan_preset(val);
        })
    }));

    let fan_meta = ai
        .filter(|a| a.fans.supported)
        .map(|a| {
            format!(
                "{}°C · {}% fan ({} fans)",
                a.fans.temp_c.unwrap_or(0),
                a.fans.fan_percent.unwrap_or(0),
                a.fans.fan_count.max(1)
            )
        })
        .unwrap_or_else(|| "NVML fan telemetry unavailable on this GPU".into());

    let mut hero_card = section_card("Exo AI & GPU Fan Curves", Some(ai_summary))
        .child(
            row()
                .w_full()
                .gap(8.0)
                .flex_wrap()
                .center_y()
                .child(
                    primary_button(
                        "ai-run-now",
                        "Optimize now",
                        Some(icon_zap(13.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| s.run_exo_ai_now()),
                )
                .child(
                    glass_button(
                        "ai-latency",
                        if s.pc.latency_running {
                            "Checking latency…"
                        } else {
                            "10s Latency check"
                        },
                        Some(icon_shield(13.0, FG)),
                    )
                    .on_press(|s: &mut AppState| s.run_latency_check()),
                )
                .children(s.pc.hdr.as_ref().map(|h| {
                    status_badge(
                        if h.supported {
                            format!(
                                "HDR {} · {} nits",
                                if h.on { "On" } else { "Ready" },
                                h.peak_nits.map_or("—".into(), |n| n.to_string())
                            )
                        } else {
                            "SDR display".into()
                        },
                        if h.supported {
                            BadgeTone::Good
                        } else {
                            BadgeTone::Neutral
                        },
                    )
                }))
                .children(s.pc.shaders.as_ref().map(|sh| {
                    status_badge(
                        if sh.present {
                            format!(
                                "Shaders: {}",
                                sh.message
                                    .as_deref()
                                    .or(sh.state.as_deref())
                                    .unwrap_or("Ready")
                            )
                        } else {
                            "Exo Shaders not installed".into()
                        },
                        if sh.present {
                            BadgeTone::Good
                        } else {
                            BadgeTone::Neutral
                        },
                    )
                })),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(setting_row(
            "Auto-apply safe Best Settings",
            "Keep Windows power plan, timer resolution, and game hooks tuned automatically.",
            switch_pill("ai-auto-apply", "Auto-apply safe Best Settings", auto_apply).on_press(
                move |s: &mut AppState| s.set_exo_ai_bool("autoApplyBestSettings", !auto_apply),
            ),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(setting_row(
            "Background drift check",
            "Re-verify after Windows or driver updates so performance settings never regress.",
            switch_pill("ai-drift-check", "Background drift check", drift).on_press(
                move |s: &mut AppState| s.set_exo_ai_bool("backgroundDriftCheck", !drift),
            ),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(12.0)
                .flex_wrap()
                .child(
                    col()
                        .gap(2.0)
                        .child(text("NVML GPU fan curve").font(type_control()).color(FG))
                        .child(text(fan_meta).font(type_small()).color(fg_muted())),
                )
                .child(fan_pills),
        );

    if let Some(ref lat) = s.pc.tuning.as_ref().and_then(|t| t.latency.as_ref()) {
        let mut lat_chips = row().gap(10.0).flex_wrap();
        if let Some(ref before) = lat.before {
            lat_chips = lat_chips.child(stat_chip(
                "Before DPC",
                format!("{:.0} µs ({})", before.dpc_us, before.dpc_driver.as_deref().unwrap_or("kernel")),
            ));
        }
        if let Some(ref after) = lat.after {
            lat_chips = lat_chips
                .child(stat_chip(
                    "Latest DPC",
                    format!("{:.0} µs ({})", after.dpc_us, after.dpc_driver.as_deref().unwrap_or("kernel")),
                ))
                .child(stat_chip(
                    "Latest ISR",
                    format!("{:.0} µs ({})", after.isr_us, after.isr_driver.as_deref().unwrap_or("kernel")),
                ));
        }
        hero_card = hero_card.child(el().w_full().h(1.0).fill(hairline())).child(
            col()
                .w_full()
                .gap(8.0)
                .child(
                    text(
                        lat.said
                            .clone()
                            .unwrap_or_else(|| "Latest kernel latency measurement".into()),
                    )
                    .font(type_small())
                    .color(good())
                    .w_full()
                    .wrap(),
                )
                .child(lat_chips),
        );
    }

    stack = stack.child(hero_card);

    if let Some(ref ap) = s.pc.autopilot {
        let mut ap_card = section_card(
            "Autopilot Telemetry",
            Some(format!("{} · Memory Integrity: {}", ap.cpu, ap.memory_integrity.reason)),
        );
        if let Some(ref live) = ap.live {
            ap_card = ap_card.child(
                row()
                    .w_full()
                    .gap(10.0)
                    .flex_wrap()
                    .child(stat_chip("Session", live.title.clone()))
                    .child(stat_chip("Avg FPS", format!("{:.1}", live.fps)))
                    .child(stat_chip("1% Low", format!("{:.1}", live.low1)))
                    .child(stat_chip("Stutters", live.stutters.to_string()))
                    .children(live.gpu_temp.map(|t| stat_chip("GPU Temp", format!("{:.0}°C", t))))
                    .children(
                        live.vram_gb
                            .zip(live.vram_total_gb)
                            .map(|(u, tot)| stat_chip("VRAM", format!("{u:.1} / {tot:.0} GB"))),
                    )
                    .children(live.dpc.map(|d| stat_chip("DPC", format!("{d:.1}%")))),
            );
        }
        if !ap.reports.is_empty() {
            let reports = col().w_full().gap(6.0).children(ap.reports.iter().take(4).map(|r| {
                row()
                    .w_full()
                    .px(12.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG.alpha(0.45))
                    .justify_between()
                    .center_y()
                    .child(
                        col()
                            .gap(2.0)
                            .child(text(r.title.clone()).font(type_control()).color(FG))
                            .child(
                                text(format!("{:.0} min session", r.minutes))
                                    .font(type_small())
                                    .color(fg_muted()),
                            ),
                    )
                    .child(
                        row()
                            .gap(8.0)
                            .center_y()
                            .child(status_badge(format!("{:.0} fps", r.fps), BadgeTone::Good))
                            .child(status_badge(format!("1% {:.0}", r.low1), BadgeTone::Neutral))
                            .child(status_badge(
                                format!("{} stutters", r.stutters),
                                if r.stutters == 0 {
                                    BadgeTone::Good
                                } else {
                                    BadgeTone::Warn
                                },
                            )),
                    )
            }));
            ap_card = ap_card.child(reports);
        }
        stack = stack.child(ap_card);
    }

    if let Some(ref tuning) = s.pc.tuning {
        let mut tune_card = section_card(
            "Windows System Tuning",
            Some(if tuning.restart {
                "Restart Windows to finish pending kernel changes.".into()
            } else {
                "Each toggle is reversible and backed by a Windows restore point.".into()
            }),
        );
        for (i, item) in tuning.items.iter().take(8).enumerate() {
            if i > 0 {
                tune_card = tune_card.child(el().w_full().h(1.0).fill(hairline()));
            }
            let item_key = item.key.clone();
            let next_on = !item.on;
            let right = if let Some(ref blk) = item.blocked {
                status_badge(blk.clone(), BadgeTone::Warn)
            } else {
                let sw_key: &'static str = match i {
                    0 => "tune-sw-0",
                    1 => "tune-sw-1",
                    2 => "tune-sw-2",
                    3 => "tune-sw-3",
                    4 => "tune-sw-4",
                    5 => "tune-sw-5",
                    6 => "tune-sw-6",
                    _ => "tune-sw-7",
                };
                switch_pill(sw_key, "Toggle tuning item", item.on).on_press(move |s: &mut AppState| {
                    s.toggle_tuning_item(&item_key, next_on);
                })
            };
            tune_card = tune_card.child(
                row()
                    .w_full()
                    .py(8.0)
                    .gap(14.0)
                    .justify_between()
                    .center_y()
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(text(item.label.clone()).font(type_control()).color(FG))
                            .child(
                                text(item.detail.clone())
                                    .font(type_small())
                                    .color(fg_muted())
                                    .w_full()
                                    .wrap(),
                            ),
                    )
                    .child(right),
            );
        }
        stack = stack.child(tune_card);
    }

    stack
}

fn apps_tab(s: &AppState) -> El {
    let sub_bar = row()
        .w_full()
        .justify_between()
        .center_y()
        .flex_wrap()
        .gap(10.0)
        .child(
            row()
                .gap(6.0)
                .child(
                    filter_pill(
                        "apps-sub-installed",
                        format!(
                            "Installed ({})",
                            s.pc.apps_list.as_ref().map_or(0, |a| a.programs.len())
                        ),
                        s.pc.apps_sub_tab == AppsSubTab::Installed,
                    )
                    .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Installed),
                )
                .child(
                    filter_pill(
                        "apps-sub-startup",
                        format!(
                            "Startup ({})",
                            s.pc.startup_list.as_ref().map_or(0, |st| st.items.len())
                        ),
                        s.pc.apps_sub_tab == AppsSubTab::Startup,
                    )
                    .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Startup),
                ),
        )
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button("apps-refresh", "Refresh", Some(icon_refresh(13.0, FG)))
                        .on_press(|s: &mut AppState| s.load_pc_apps()),
                )
                .children((s.pc.apps_sub_tab == AppsSubTab::Startup).then(|| {
                    primary_button(
                        "startup-clean-btn",
                        "Clean unneeded",
                        Some(icon_zap(13.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| s.clean_startup_unneeded())
                }))
                .children((s.pc.apps_sub_tab == AppsSubTab::Installed).then(|| {
                    glass_button("apps-tidy-btn", "Tidy leftovers", None)
                        .on_press(|s: &mut AppState| s.tidy_app_leftovers())
                })),
        );

    let mut page = col().w_full().gap(14.0).child(sub_bar);

    match s.pc.apps_sub_tab {
        AppsSubTab::Installed => {
            let q_input = text_input(&s.pc.apps_query, type_body())
                .key("apps-search-input")
                .label("Search installed apps")
                .w_full()
                .single_line()
                .color(FG)
                .placeholder("Filter installed apps…", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| &mut s.pc.apps_query));

            let filters = row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(10.0)
                .flex_wrap()
                .child(
                    row()
                        .w(320.0)
                        .h(34.0)
                        .px(12.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(BG.alpha(0.55))
                        .border(1.0, glass_rim())
                        .child(icon_search(13.0, fg_muted()))
                        .child(q_input),
                )
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            filter_pill(
                                "apps-f-all",
                                "All".into(),
                                s.pc.apps_filter == AppsFilter::All,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_filter = AppsFilter::All),
                        )
                        .child(
                            filter_pill(
                                "apps-f-bloat",
                                "Bloatware".into(),
                                s.pc.apps_filter == AppsFilter::Bloat,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_filter = AppsFilter::Bloat),
                        )
                        .child(
                            filter_pill(
                                "apps-f-rem",
                                "Removable".into(),
                                s.pc.apps_filter == AppsFilter::Removable,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_filter = AppsFilter::Removable),
                        ),
                );

            page = page.child(filters);

            let q = s.pc.apps_query.text.trim().to_lowercase();
            let list = s
                .pc
                .apps_list
                .as_ref()
                .map(|a| a.programs.as_slice())
                .unwrap_or(&[]);
            let rows: Vec<_> = list
                .iter()
                .filter(|p| match s.pc.apps_filter {
                    AppsFilter::All => true,
                    AppsFilter::Bloat => p.bloat.is_some(),
                    AppsFilter::Removable => p.removable,
                })
                .filter(|p| {
                    q.is_empty()
                        || p.name.to_lowercase().contains(&q)
                        || p.publisher
                            .as_deref()
                            .is_some_and(|pub_| pub_.to_lowercase().contains(&q))
                })
                .take(40)
                .collect();

            let mut card = section_card(
                "Installed Applications",
                Some(format!("Showing {} of {} programs", rows.len(), list.len())),
            );
            for (idx, app) in rows.into_iter().enumerate() {
                if idx > 0 {
                    card = card.child(el().w_full().h(1.0).fill(hairline()));
                }
                let meta = [
                    app.publisher.clone(),
                    app.version.clone().map(|v| format!("v{v}")),
                    app.bytes.map(format_bytes),
                    app.last_opened.clone().map(|d| format!("Last used {d}")),
                ]
                .into_iter()
                .flatten()
                .collect::<Vec<_>>()
                .join(" · ");

                let mut badges = row().gap(6.0).center_y();
                if let Some(ref why) = app.bloat {
                    badges = badges.child(status_badge(
                        if why.len() > 36 { "Bloat / Heavy".into() } else { why.clone() },
                        BadgeTone::Warn,
                    ));
                }
                if let Some(ref kept) = app.kept {
                    badges = badges.child(status_badge(
                        if kept.len() > 34 { "Needed by games".into() } else { kept.clone() },
                        BadgeTone::Neutral,
                    ));
                }
                if app.removable {
                    let app_id = app.id.clone();
                    badges = badges.child(
                        el().key(format!("app-uninst-{idx}"))
                            .role("button")
                            .label(format!("Uninstall {}", app.name))
                            .focusable(true)
                            .hit()
                            .press_on_down()
                            .h(28.0)
                            .px(10.0)
                            .radius(RADIUS_PILL)
                            .center()
                            .fill(bad().alpha(0.18))
                            .hover(bad().alpha(0.30))
                            .on_press(move |s: &mut AppState| s.uninstall_app(&app_id))
                            .child(
                                row()
                                    .gap(5.0)
                                    .center_y()
                                    .child(icon_trash(12.0, bad()))
                                    .child(text("Uninstall").font(type_small()).color(bad())),
                            ),
                    );
                }

                card = card.child(
                    row()
                        .w_full()
                        .py(8.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(app.name.clone()).font(type_control()).color(FG).truncate())
                                .child(text(meta).font(type_small()).color(fg_muted()).truncate()),
                        )
                        .child(badges),
                );
            }
            page = page.child(card);
        }
        AppsSubTab::Startup => {
            let st = s.pc.startup_list.as_ref();
            let items = st.map(|x| x.items.as_slice()).unwrap_or(&[]);
            let mut card = section_card(
                "Sign-in Startup Programs",
                Some("Disable unneeded background launchers to shorten boot and free RAM.".into()),
            );
            for (idx, item) in items.iter().take(40).enumerate() {
                if idx > 0 {
                    card = card.child(el().w_full().h(1.0).fill(hairline()));
                }
                let mut right = row().gap(8.0).center_y();
                if item.memory > 0 {
                    right = right.child(status_badge(
                        format!("{} RAM", format_bytes(item.memory)),
                        BadgeTone::Neutral,
                    ));
                }
                if item.missing {
                    right = right.child(status_badge("Broken".into(), BadgeTone::Bad));
                } else if item.locked {
                    right = right.child(status_badge("Protected".into(), BadgeTone::Neutral));
                } else if item.unneeded {
                    right = right.child(status_badge("Unneeded".into(), BadgeTone::Warn));
                }
                if !item.locked && !item.read_only {
                    let id = item.id.clone();
                    right = right.child(
                        el().key(format!("startup-rem-{idx}"))
                            .role("button")
                            .label(format!("Disable {}", item.name))
                            .focusable(true)
                            .hit()
                            .press_on_down()
                            .h(28.0)
                            .px(10.0)
                            .radius(RADIUS_PILL)
                            .center()
                            .fill(subtle_fill())
                            .border(1.0, glass_rim())
                            .on_press(move |s: &mut AppState| s.remove_startup_item(&id))
                            .child(text("Disable").font(type_small()).color(FG)),
                    );
                }

                card = card.child(
                    row()
                        .w_full()
                        .py(8.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(item.name.clone()).font(type_control()).color(FG))
                                .child(
                                    text(
                                        item.does
                                            .clone()
                                            .or_else(|| item.status.clone())
                                            .unwrap_or_else(|| item.source.clone()),
                                    )
                                    .font(type_small())
                                    .color(fg_muted())
                                    .w_full()
                                    .wrap(),
                                ),
                        )
                        .child(right),
                );
            }
            page = page.child(card);

            if let Some(removed) = st.map(|x| &x.removed).filter(|r| !r.is_empty()) {
                let mut rem_card = section_card(
                    "Disabled by Exo",
                    Some("Restore any item back to Windows startup in one click.".into()),
                );
                for (idx, item) in removed.iter().take(15).enumerate() {
                    if idx > 0 {
                        rem_card = rem_card.child(el().w_full().h(1.0).fill(hairline()));
                    }
                    let id = item.id.clone();
                    rem_card = rem_card.child(
                        row()
                            .w_full()
                            .py(6.0)
                            .justify_between()
                            .center_y()
                            .child(text(item.name.clone()).font(type_control()).color(fg_muted()))
                            .child(
                                el().key(format!("startup-rest-{idx}"))
                                    .role("button")
                                    .label(format!("Restore {}", item.name))
                                    .focusable(true)
                                    .hit()
                                    .press_on_down()
                                    .h(28.0)
                                    .px(10.0)
                                    .radius(RADIUS_PILL)
                                    .center()
                                    .fill(subtle_fill())
                                    .border(1.0, glass_rim())
                                    .on_press(move |s: &mut AppState| s.restore_startup_item(&id))
                                    .child(text("Restore").font(type_small()).color(FG)),
                            ),
                    );
                }
                page = page.child(rem_card);
            }
        }
    }

    page
}

fn drive_card(d: &DriveInfo) -> El {
    let letters: String = d
        .volumes
        .iter()
        .map(|v| format!("{} ({:.0} GB free / {:.0} GB)", v.letter, v.free_gb, v.total_gb))
        .collect::<Vec<_>>()
        .join("  ·  ");

    let mut chips = row().gap(8.0).flex_wrap().center_y();
    chips = chips.child(status_badge(d.kind.clone(), BadgeTone::Neutral));
    if let Some(ref link) = d.link {
        chips = chips.child(status_badge(
            format!("PCIe Gen{} ×{}", link.pcie_gen, link.width),
            if link.pcie_gen < link.max_gen || link.width < link.max_width {
                BadgeTone::Warn
            } else {
                BadgeTone::Good
            },
        ));
    }
    if let Some(ref temp) = d.temperature {
        let hot = temp.warn.is_some_and(|w| temp.now >= w);
        chips = chips.child(status_badge(
            format!("{}°C", temp.now),
            if hot { BadgeTone::Bad } else { BadgeTone::Good },
        ));
    }
    if let Some(ref h) = d.health {
        if let Some(used) = h.life_used {
            let left = (100 - used).clamp(0, 100);
            chips = chips.child(status_badge(
                format!("{left}% health"),
                if left < 20 { BadgeTone::Bad } else { BadgeTone::Good },
            ));
        }
        if let Some(tb) = h.written_tb {
            chips = chips.child(status_badge(format!("{tb:.1} TBW"), BadgeTone::Neutral));
        }
    }
    if let Some(ref build) = d.build {
        chips = chips.child(status_badge(build.clone(), BadgeTone::Neutral));
    }

    let mut card = col()
        .w_full()
        .px(18.0)
        .py(16.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(subtle_fill())
        .border(1.0, hairline())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(12.0)
                .flex_wrap()
                .child(
                    col()
                        .gap(2.0)
                        .child(text(d.model.clone()).font(type_section()).color(FG))
                        .child(text(letters).font(type_small()).color(fg_muted())),
                )
                .child(chips),
        );

    if let Some(ref sp) = d.space
        && sp.total > 0
    {
        let tot = sp.total as f32;
        let segs = [
            (sp.games as f32 / tot, good()),
            ((sp.caches + sp.leftovers) as f32 / tot, warn()),
            (sp.other as f32 / tot, hex(0x6e7681, 1.0)),
            (sp.free as f32 / tot, Color::WHITE.alpha(0.08)),
        ];
        card = card
            .child(segmented_bar(&segs))
            .child(
                row()
                    .gap(14.0)
                    .flex_wrap()
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(el().size(8.0, 8.0).radius(4.0).fill(good()))
                            .child(
                                text(format!("Games {}", format_bytes(sp.games)))
                                    .font(type_small())
                                    .color(fg_muted()),
                            ),
                    )
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(el().size(8.0, 8.0).radius(4.0).fill(warn()))
                            .child(
                                text(format!(
                                    "Caches {}",
                                    format_bytes(sp.caches + sp.leftovers)
                                ))
                                .font(type_small())
                                .color(fg_muted()),
                            ),
                    )
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(el().size(8.0, 8.0).radius(4.0).fill(hex(0x6e7681, 1.0)))
                            .child(
                                text(format!("Other {}", format_bytes(sp.other)))
                                    .font(type_small())
                                    .color(fg_muted()),
                            ),
                    )
                    .child(
                        text(format!("Free {}", format_bytes(sp.free)))
                            .font(type_small())
                            .color(FG),
                    ),
            );
    }

    if !d.games.is_empty() {
        let top_games = row().gap(8.0).flex_wrap().children(d.games.iter().take(4).map(|g| {
            row()
                .px(10.0)
                .py(6.0)
                .gap(8.0)
                .radius(RADIUS_SMALL)
                .fill(BG.alpha(0.45))
                .child(text(g.title.clone()).font(type_small()).color(FG))
                .child(text(format_bytes(g.bytes)).font(type_small()).color(fg_muted()))
        }));
        card = card.child(top_games);
    }

    for v in &d.verdicts {
        card = card.child(
            text(v.text.clone())
                .font(type_small())
                .color(if v.bad { warn() } else { good() })
                .w_full()
                .wrap(),
        );
    }

    card
}

fn storage_tab(s: &AppState) -> El {
    let mut stack = col().w_full().gap(14.0).child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(
                text("NVMe health, storage breakdown, and drive tuning")
                    .font(type_small())
                    .color(fg_muted()),
            )
            .child(
                row()
                    .gap(8.0)
                    .child(
                        glass_button("drv-clean-junk", "Clean temp & caches", None)
                            .on_press(|s: &mut AppState| s.clean_system_junk()),
                    )
                    .child(
                        glass_button("drv-refresh", "Refresh", Some(icon_refresh(13.0, FG)))
                            .on_press(|s: &mut AppState| s.load_pc_drives()),
                    ),
            ),
    );

    if let Some(ref drv) = s.pc.drives {
        if !drv.fixes.is_empty() {
            let mut fix_card = section_card(
                "Drive Optimizations",
                Some("Write caching and TRIM schedule for game drives.".into()),
            );
            for (idx, f) in drv.fixes.iter().enumerate() {
                if idx > 0 {
                    fix_card = fix_card.child(el().w_full().h(1.0).fill(hairline()));
                }
                let action_key = f.key.clone();
                let right = if f.applied {
                    status_badge("Applied".into(), BadgeTone::Good)
                } else {
                    el().key(format!("drv-fix-{idx}"))
                        .role("button")
                        .label(format!("Apply {}", f.label))
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .h(28.0)
                        .px(12.0)
                        .radius(RADIUS_PILL)
                        .center()
                        .fill(FG)
                        .on_press(move |s: &mut AppState| s.apply_drive_fix(&action_key))
                        .child(text("Apply").font(type_small()).color(PRIMARY_FG))
                };
                fix_card = fix_card.child(
                    row()
                        .w_full()
                        .py(6.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(f.label.clone()).font(type_control()).color(FG))
                                .child(
                                    text(f.detail.clone())
                                        .font(type_small())
                                        .color(fg_muted())
                                        .w_full()
                                        .wrap(),
                                ),
                        )
                        .child(right),
                );
            }
            stack = stack.child(fix_card);
        }

        for d in &drv.drives {
            stack = stack.child(drive_card(d));
        }
    } else {
        stack = stack.child(
            el().w_full()
                .h(140.0)
                .radius(RADIUS_PANEL)
                .fill(subtle_fill())
                .center()
                .child(
                    text(if s.pc.drives_loading {
                        "Scanning drives and game storage…"
                    } else {
                        "No drives loaded yet."
                    })
                    .font(type_section())
                    .color(fg_muted()),
                ),
        );
    }

    stack
}

fn wooting_key_matrix(kb: &KeyboardReadReply) -> El {
    const ROWS: [&[(&str, f32)]; 5] = [
        &[
            ("ESC", 1.0), ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0), ("5", 1.0), ("6", 1.0),
            ("7", 1.0), ("8", 1.0), ("9", 1.0), ("0", 1.0), ("-", 1.0), ("=", 1.0), ("BKSP", 2.0),
        ],
        &[
            ("TAB", 1.5), ("Q", 1.0), ("W", 1.0), ("E", 1.0), ("R", 1.0), ("T", 1.0), ("Y", 1.0),
            ("U", 1.0), ("I", 1.0), ("O", 1.0), ("P", 1.0), ("[", 1.0), ("]", 1.0), ("\\", 1.5),
        ],
        &[
            ("CAPS", 1.75), ("A", 1.0), ("S", 1.0), ("D", 1.0), ("F", 1.0), ("G", 1.0), ("H", 1.0),
            ("J", 1.0), ("K", 1.0), ("L", 1.0), (";", 1.0), ("'", 1.0), ("ENTER", 2.25),
        ],
        &[
            ("SHIFT", 2.25), ("Z", 1.0), ("X", 1.0), ("C", 1.0), ("V", 1.0), ("B", 1.0), ("N", 1.0),
            ("M", 1.0), (",", 1.0), (".", 1.0), ("/", 1.0), ("SHIFT", 2.75),
        ],
        &[
            ("CTRL", 1.25), ("WIN", 1.25), ("ALT", 1.25), ("SPACE", 6.25), ("ALT", 1.25),
            ("FN", 1.25), ("MENU", 1.25), ("CTRL", 1.25),
        ],
    ];
    let unit = 38.0_f32;
    let gap = 4.0_f32;

    col()
        .px(12.0)
        .py(12.0)
        .gap(gap)
        .radius(RADIUS_CARD)
        .fill(BG.alpha(0.70))
        .border(1.0, glass_rim())
        .children(ROWS.into_iter().map(|keys| {
            row().gap(gap).children(keys.iter().map(|(label, w)| {
                let is_wasd = matches!(*label, "W" | "A" | "S" | "D");
                let key_w = unit * *w + gap * (*w - 1.0).max(0.0);
                let fill = if is_wasd && kb.rapid_trigger {
                    good().alpha(0.24)
                } else if is_wasd {
                    Color::WHITE.alpha(0.16)
                } else {
                    Color::WHITE.alpha(0.06)
                };
                let rim = if is_wasd && kb.rapid_trigger {
                    good().alpha(0.65)
                } else {
                    hairline()
                };
                el().size(key_w, 34.0)
                    .radius(6.0)
                    .center()
                    .fill(fill)
                    .border(1.0, rim)
                    .child(
                        text(*label)
                            .font(type_label())
                            .color(if is_wasd { FG } else { fg_muted() }),
                    )
            }))
        }))
}

fn wooting_card(s: &AppState, kb: &KeyboardReadReply) -> El {
    let act_mm = kb.actuation_mm.unwrap_or(0.4);
    let rt_mm = kb.rapid_trigger_mm.unwrap_or(0.15);
    let model = kb.model.clone().unwrap_or_else(|| "Wooting Keyboard".into());
    let sub = format!(
        "{} · {} · {} Hz",
        kb.layout.as_deref().unwrap_or("ANSI"),
        kb.firmware.as_deref().unwrap_or("Hall-Effect"),
        kb.max_scan_hz.unwrap_or(1000)
    );

    let profile_pills = row().gap(6.0).flex_wrap().children(kb.profiles.iter().map(|p| {
        let active = p.index == kb.active_profile;
        let idx = p.index;
        let key: &'static str = match idx {
            0 => "kb-prof-0",
            1 => "kb-prof-1",
            2 => "kb-prof-2",
            _ => "kb-prof-3",
        };
        filter_pill(key, format!("{}. {}", p.index + 1, p.name), active)
            .on_press(move |s: &mut AppState| s.switch_keyboard_profile(idx))
    }));

    let rapid = kb.rapid_trigger;
    let tachyon = kb.tachyon;

    let mut card = col()
        .w_full()
        .px(18.0)
        .py(16.0)
        .gap(14.0)
        .radius(RADIUS_PANEL)
        .fill(subtle_fill())
        .border(1.0, hairline())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .flex_wrap()
                .gap(10.0)
                .child(
                    col()
                        .gap(2.0)
                        .child(text(model).font(type_section()).color(FG))
                        .child(text(sub).font(type_small()).color(fg_muted())),
                )
                .child(
                    row()
                        .gap(8.0)
                        .child(
                            glass_button("kb-backup", "Backup profiles", None)
                                .on_press(|s: &mut AppState| s.backup_keyboard_profiles()),
                        )
                        .children(kb.wootility_installed.then(|| {
                            glass_button(
                                "kb-open-woot",
                                "Wootility",
                                Some(icon_external(13.0, FG)),
                            )
                            .on_press(|s: &mut AppState| s.open_wootility())
                        })),
                ),
        )
        .child(profile_pills)
        .child(
            row()
                .w_full()
                .gap(16.0)
                .items_start()
                .flex_wrap()
                .child(wooting_key_matrix(kb))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(260.0)
                        .gap(10.0)
                        .child(
                            row()
                                .w_full()
                                .px(14.0)
                                .py(10.0)
                                .justify_between()
                                .center_y()
                                .radius(RADIUS_CARD)
                                .fill(BG.alpha(0.45))
                                .border(1.0, hairline())
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(text("Actuation point").font(type_small()).color(fg_muted()))
                                        .child(text(format!("{act_mm:.1} mm")).font(type_section()).color(FG)),
                                )
                                .child(
                                    row()
                                        .gap(6.0)
                                        .child(
                                            glass_button("kb-act-down", "−", None).on_press(move |s: &mut AppState| {
                                                s.update_keyboard_settings(Some((act_mm - 0.1).max(0.1)), None, None, None);
                                            }),
                                        )
                                        .child(
                                            glass_button("kb-act-up", "+", None).on_press(move |s: &mut AppState| {
                                                s.update_keyboard_settings(Some((act_mm + 0.1).min(4.0)), None, None, None);
                                            }),
                                        ),
                                ),
                        )
                        .child(
                            row()
                                .w_full()
                                .px(14.0)
                                .py(10.0)
                                .justify_between()
                                .center_y()
                                .radius(RADIUS_CARD)
                                .fill(BG.alpha(0.45))
                                .border(1.0, hairline())
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(text("Rapid Trigger").font(type_small()).color(fg_muted()))
                                        .child(
                                            text(if rapid { format!("{rt_mm:.2} mm") } else { "Off".into() })
                                                .font(type_section())
                                                .color(if rapid { good() } else { fg_muted() }),
                                        ),
                                )
                                .child(
                                    switch_pill("kb-rt-switch", "Rapid Trigger", rapid).on_press(
                                        move |s: &mut AppState| {
                                            s.update_keyboard_settings(None, Some(!rapid), None, None);
                                        },
                                    ),
                                ),
                        )
                        .child(
                            row()
                                .w_full()
                                .px(14.0)
                                .py(10.0)
                                .justify_between()
                                .center_y()
                                .radius(RADIUS_CARD)
                                .fill(BG.alpha(0.45))
                                .border(1.0, hairline())
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(text("Tachyon mode").font(type_small()).color(fg_muted()))
                                        .child(
                                            text(if tachyon { "Ultra-low latency" } else { "Standard" })
                                                .font(type_section())
                                                .color(FG),
                                        ),
                                )
                                .child(
                                    switch_pill("kb-tach-switch", "Tachyon mode", tachyon).on_press(
                                        move |s: &mut AppState| {
                                            s.update_keyboard_settings(None, None, None, Some(!tachyon));
                                        },
                                    ),
                                ),
                        ),
                ),
        );

    if !s.pc.keyboard_backups.is_empty() {
        let backups = row().gap(8.0).flex_wrap().children(
            s.pc.keyboard_backups.iter().take(4).enumerate().map(|(i, b)| {
                let fname = b.file_name.clone();
                row()
                    .px(10.0)
                    .py(6.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(BG.alpha(0.45))
                    .child(text(b.created_utc.clone()).font(type_small()).color(fg_muted()))
                    .child(
                        el().key(format!("kb-restore-{i}"))
                            .role("button")
                            .label(format!("Restore {}", b.file_name))
                            .focusable(true)
                            .hit()
                            .press_on_down()
                            .px(8.0)
                            .h(22.0)
                            .radius(RADIUS_PILL)
                            .center()
                            .fill(subtle_fill())
                            .on_press(move |s: &mut AppState| s.restore_keyboard_backup(&fname))
                            .child(text("Restore").font(type_small()).color(FG)),
                    )
            }),
        );
        card = card.child(backups);
    }

    card
}

fn devices_tab(s: &AppState) -> El {
    let mut stack = col().w_full().gap(16.0).child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(
                text("Hall-effect keyboards, audio endpoints, and connected peripherals")
                    .font(type_small())
                    .color(fg_muted()),
            )
            .child(
                glass_button("dev-refresh", "Refresh", Some(icon_refresh(13.0, FG)))
                    .on_press(|s: &mut AppState| s.load_pc_devices()),
            ),
    );

    if let Some(ref kb) = s.pc.keyboard
        && kb.ok
    {
        stack = stack.child(wooting_card(s, kb));
    }

    if let Some(ref audio) = s.pc.audio {
        let out_vol = audio.output.as_ref().and_then(|o| o.volume).unwrap_or(0);
        let out_muted = audio.output.as_ref().is_some_and(|o| o.muted);
        let mic_vol = audio.mic.as_ref().and_then(|m| m.volume).unwrap_or(0);
        let mic_muted = audio.mic.as_ref().is_some_and(|m| m.muted);

        let audio_card = section_card(
            "Audio & Exo Voice",
            Some(format!(
                "Noise suppression: {} · Voice focus: {}",
                if audio.voice.noise.is_empty() {
                    "Off"
                } else {
                    audio.voice.noise.as_str()
                },
                if audio.voice.focus { "On" } else { "Off" }
            )),
        )
        .child(
            row()
                .w_full()
                .gap(12.0)
                .flex_wrap()
                .child(
                    row()
                        .grow(1.0)
                        .min_w(260.0)
                        .px(14.0)
                        .py(12.0)
                        .justify_between()
                        .center_y()
                        .radius(RADIUS_CARD)
                        .fill(BG.alpha(0.45))
                        .border(1.0, hairline())
                        .child(
                            row()
                                .gap(10.0)
                                .center_y()
                                .child(if out_muted {
                                    icon_volume_off(16.0, bad())
                                } else {
                                    icon_volume(16.0, FG)
                                })
                                .child(
                                    col()
                                        .gap(2.0)
                                        .child(text(format!("Output · {out_vol}%")).font(type_control()).color(FG))
                                        .child(
                                            text(
                                                audio
                                                    .output
                                                    .as_ref()
                                                    .and_then(|o| o.format.clone())
                                                    .unwrap_or_else(|| "Windows default".into()),
                                            )
                                            .font(type_small())
                                            .color(fg_muted()),
                                        ),
                                ),
                        )
                        .child(
                            row()
                                .gap(6.0)
                                .child(
                                    glass_button("aud-out-dn", "−", None).on_press(
                                        move |s: &mut AppState| s.set_audio_volume(true, (out_vol - 10).max(0)),
                                    ),
                                )
                                .child(
                                    glass_button("aud-out-up", "+", None).on_press(
                                        move |s: &mut AppState| s.set_audio_volume(true, (out_vol + 10).min(100)),
                                    ),
                                )
                                .child(
                                    glass_button(
                                        "aud-out-mute",
                                        if out_muted { "Unmute" } else { "Mute" },
                                        None,
                                    )
                                    .on_press(move |s: &mut AppState| s.set_audio_mute(true, !out_muted)),
                                ),
                        ),
                )
                .child(
                    row()
                        .grow(1.0)
                        .min_w(260.0)
                        .px(14.0)
                        .py(12.0)
                        .justify_between()
                        .center_y()
                        .radius(RADIUS_CARD)
                        .fill(BG.alpha(0.45))
                        .border(1.0, hairline())
                        .child(
                            col()
                                .gap(2.0)
                                .child(text(format!("Microphone · {mic_vol}%")).font(type_control()).color(FG))
                                .child(
                                    text(
                                        audio
                                            .mic
                                            .as_ref()
                                            .and_then(|m| m.format.clone())
                                            .unwrap_or_else(|| "Windows default".into()),
                                    )
                                    .font(type_small())
                                    .color(fg_muted()),
                                ),
                        )
                        .child(
                            row()
                                .gap(6.0)
                                .child(
                                    glass_button("aud-mic-dn", "−", None).on_press(
                                        move |s: &mut AppState| s.set_audio_volume(false, (mic_vol - 10).max(0)),
                                    ),
                                )
                                .child(
                                    glass_button("aud-mic-up", "+", None).on_press(
                                        move |s: &mut AppState| s.set_audio_volume(false, (mic_vol + 10).min(100)),
                                    ),
                                )
                                .child(
                                    glass_button(
                                        "aud-mic-mute",
                                        if mic_muted { "Unmute" } else { "Mute" },
                                        None,
                                    )
                                    .on_press(move |s: &mut AppState| s.set_audio_mute(false, !mic_muted)),
                                ),
                        ),
                ),
        );
        stack = stack.child(audio_card);
    }

    if let Some(ref list) = s.pc.devices {
        let mut grid = row().w_full().gap(12.0).flex_wrap();
        for dev in list.devices.iter().take(12) {
            let icon = match dev.kind.as_str() {
                "keyboard" => icon_keyboard(16.0, FG),
                "audio" => icon_volume(16.0, FG),
                "display" => icon_monitor(16.0, FG),
                _ => icon_cpu(16.0, FG),
            };
            let mut badges = row().gap(6.0).flex_wrap();
            if let Some(hz) = dev.polling_hz {
                badges = badges.child(status_badge(format!("{hz} Hz"), BadgeTone::Good));
            }
            if let Some(bat) = dev.battery {
                badges = badges.child(status_badge(
                    format!("{bat}%"),
                    if bat <= 20 { BadgeTone::Warn } else { BadgeTone::Neutral },
                ));
            }
            if dev.kind == "mouse" {
                badges = badges.child(status_badge("Exo Mouse ready".into(), BadgeTone::Neutral));
            }

            grid = grid.child(
                col()
                    .w(286.0)
                    .px(16.0)
                    .py(14.0)
                    .gap(8.0)
                    .radius(RADIUS_CARD)
                    .fill(subtle_fill())
                    .border(1.0, hairline())
                    .child(
                        row()
                            .gap(10.0)
                            .center_y()
                            .child(icon)
                            .child(
                                col()
                                    .grow(1.0)
                                    .min_w(0.0)
                                    .gap(2.0)
                                    .child(text(dev.name.clone()).font(type_control()).color(FG).truncate())
                                    .child(
                                        text(
                                            dev.connection
                                                .clone()
                                                .or_else(|| dev.role.clone())
                                                .unwrap_or_else(|| dev.kind.clone()),
                                        )
                                        .font(type_small())
                                        .color(fg_faint())
                                        .truncate(),
                                    ),
                            ),
                    )
                    .child(badges),
            );
        }
        stack = stack.child(section_card("Connected Peripherals", None).child(grid));
    }

    stack
}

pub fn view(s: &AppState) -> El {
    let chips = row().gap(8.0).flex_wrap().children(PcTab::ALL.into_iter().map(|tab| {
        let active = s.pc.pc_tab == tab;
        row()
            .key(tab.key())
            .role("tab")
            .label(tab.label())
            .focusable(true)
            .hit()
            .press_on_down()
            .h(34.0)
            .px(14.0)
            .gap(7.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(if active { FG } else { subtle_fill() })
            .border(1.0, if active { FG } else { glass_rim() })
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| s.switch_pc_tab(tab))
            .child(tab_icon(tab, active))
            .child(
                text(tab.label())
                    .font(type_control())
                    .color(if active { PRIMARY_FG } else { FG }),
            )
    }));

    let body = match s.pc.pc_tab {
        PcTab::ExoAi => exo_ai_tab(s),
        PcTab::Apps => apps_tab(s),
        PcTab::Storage => storage_tab(s),
        PcTab::Devices => devices_tab(s),
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
                .flex_wrap()
                .gap(12.0)
                .child(text("This PC").font(type_page()).color(FG))
                .children(
                    s.notice
                        .as_ref()
                        .map(|n| status_badge(n.clone(), BadgeTone::Neutral)),
                ),
        )
        .child(chips)
        .child(body)
}
