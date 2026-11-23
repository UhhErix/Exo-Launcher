use crate::state::AppState;
use exo_kit::{
    FG, RADIUS_PANEL, amoled_card, fg_muted, glass_button, icon_chevron_left, icon_hard_drive,
    icon_refresh, progress_bar, type_section, type_small,
};
use exo_rpc::DriveInfo;
use rune::{El, TextStyle, col, row, text};

pub(crate) fn format_gb_short(gb: f64) -> String {
    if gb >= 1000.0 {
        format!("{:.1} TB", gb / 1024.0)
    } else {
        format!("{:.0} GB", gb.round().max(1.0))
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

pub(crate) fn exo_ai_storage_view(s: &AppState) -> El {
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
