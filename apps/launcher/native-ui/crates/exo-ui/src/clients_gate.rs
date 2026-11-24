use crate::settings::{set_btn, set_primary_btn};
use crate::settings_state::{clients_fix_copy, clients_heading};
use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_CARD, amoled_card, bad, fg_muted, good, hairline, icon_check,
    store_logo_or_fallback, type_body, type_page, type_small, warn,
};
use exo_rpc::ClientsStatus;
use rune::{El, col, el, row, text};

fn client_tile(store: &str, name: &str, status_text: &str, ready: bool) -> El {
    amoled_card(RADIUS_CARD)
        .grow(1.0)
        .min_w(0.0)
        .padding(16.0)
        .gap(8.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(store_logo_or_fallback(store, 20.0))
                        .child(text(name.to_string()).font(type_body()).color(FG)),
                )
                .children(ready.then(|| icon_check(16.0, good()))),
        )
        .child(
            text(status_text.to_string())
                .font(type_small())
                .color(if ready { good() } else { warn() }),
        )
}

fn client_tiles(status: &ClientsStatus) -> El {
    let mut tiles = row().w_full().gap(12.0);
    if status.steam.channel != "none" {
        let (label, ok) = match status.steam.channel.as_str() {
            "beta" => ("Steam Beta", true),
            "stable" => ("Standard Steam (needs Beta)", false),
            _ => ("Could not read Steam branch", false),
        };
        tiles = tiles.child(client_tile("steam", "Steam", label, ok));
    }
    if status.epic.layout != "none" {
        let (label, ok) = match status.epic.layout.as_str() {
            "new" => ("New Epic launcher", true),
            "old" => ("Old Epic launcher (needs update)", false),
            _ => ("Could not read Epic launcher", false),
        };
        tiles = tiles.child(client_tile("epic", "Epic", label, ok));
    }
    tiles
}

pub fn clients_card_view(s: &AppState, status: &ClientsStatus, allow_later: bool) -> El {
    let ready = !status.needs_fix;
    let installing = status.epic.installing;
    let unreadable = status.problem.as_deref();
    let busy = s.wp4.clients_busy;
    let note = s.wp4.clients_note.as_ref();
    let retry = unreadable.is_some() || note.is_some_and(|n| n.error);
    let offer_later =
        allow_later && (note.is_some_and(|n| n.error) || s.wp4.clients_installer_long);

    let mut out = col().w_full().gap(14.0);
    if unreadable.is_none() {
        out = out.child(
            text(if ready {
                "Your game clients are current.".to_string()
            } else {
                clients_fix_copy(status)
            })
            .font(type_body())
            .color(FG)
            .wrap(),
        );
    }

    out = out.child(client_tiles(status));

    if status.epic.old_still_there {
        out = out.child(
            text("The old Epic launcher is still installed. Epic's installer should have removed it; remove it in Windows Settings if you no longer use it.")
                .font(type_small())
                .color(FG)
                .wrap(),
        );
    }

    if let Some(prob) = unreadable {
        out = out.child(
            text(prob.to_string())
                .font(type_small())
                .color(bad())
                .wrap(),
        );
    } else if let Some(n) = note {
        out = out.child(
            text(n.text.clone())
                .font(type_small())
                .color(if n.error { bad() } else { FG })
                .wrap(),
        );
    }

    if installing {
        out = out.child(
            text("Finish Epic's installer. Exo checks again when it closes.")
                .font(type_small())
                .color(FG)
                .wrap(),
        );
    }

    if !ready || unreadable.is_some() {
        let mut actions = row().w_full().justify_end().center_y().gap(10.0);
        if offer_later {
            actions = actions.child(
                set_btn("clients-continue-later", "Continue for now", None)
                    .disabled(busy)
                    .on_press(|s: &mut AppState| s.wp4.clients_deferred = true),
            );
        }
        let disabled = busy || installing;
        let is_unreadable = unreadable.is_some();
        let mut fix_btn = set_primary_btn(
            "clients-fix-btn",
            if busy {
                "Working…"
            } else if retry {
                "Try again"
            } else {
                "Fix"
            },
            None,
        )
        .disabled(disabled)
        .opacity(if disabled { 0.45 } else { 1.0 });
        if !disabled {
            fix_btn = fix_btn.on_press(move |s: &mut AppState| {
                if is_unreadable {
                    s.wp4_load_clients_status(true);
                } else {
                    s.wp4_fix_clients();
                }
            });
        }
        out = out.child(actions.child(fix_btn));
    }

    out
}

pub fn clients_step_view(s: &AppState) -> El {
    if let Some(ref status) = s.pc.clients {
        return clients_card_view(s, status, true);
    }
    if !s.wp4.clients_failed {
        return text("Checking your game clients…")
            .font(type_body())
            .color(fg_muted());
    }
    let checking = s.wp4.clients_checking;
    let msg = if s.wp4.clients_failures > 1 {
        "Exo still could not check your game clients. Check that Steam and Epic's files are readable, then try again."
    } else {
        "Exo could not check your game clients. Check that Steam and Epic's files are readable, then try again."
    };
    col()
        .w_full()
        .gap(14.0)
        .child(text(msg).font(type_body()).color(bad()).wrap())
        .child(
            row().w_full().justify_end().child(
                set_primary_btn(
                    "clients-recheck-btn",
                    if checking { "Checking…" } else { "Try again" },
                    None,
                )
                .disabled(checking)
                .on_press(|s: &mut AppState| s.wp4_load_clients_status(true)),
            ),
        )
}

pub fn clients_start_view(s: &AppState) -> El {
    let Some(ref status) = s.pc.clients else {
        return el();
    };
    let heading = clients_heading(status);
    col()
        .key("clients-start-gate")
        .w_full()
        .h_full()
        .fill(BG)
        .center()
        .padding(32.0)
        .child(
            amoled_card(RADIUS_CARD)
                .w(600.0)
                .padding(28.0)
                .gap(16.0)
                .child(text(heading).font(type_page()).color(FG).wrap())
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(clients_card_view(s, status, true)),
        )
}
