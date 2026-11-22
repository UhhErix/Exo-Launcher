use crate::state::{AppState, Room};
use exo_kit::{
    BG, FG, RADIUS_PILL, SPRING_BASE, SPRING_QUICK, fg_muted, glass_button, glass_panel, hairline,
    icon_sparkles, primary_button, type_body, type_small, type_title,
};
use rune::{Color, El, col, el, row, text};

pub struct TourStep {
    pub title: &'static str,
    pub body: &'static str,
    pub room: Room,
}

pub const STEPS: [TourStep; 8] = [
    TourStep {
        title: "Welcome to Exo",
        body: "Everything you use to play — games, store deals, chat, music, and PC tuning — now running natively on DirectX 12 at 120 Hz. Skip anytime or replay from Ctrl+K.",
        room: Room::Library,
    },
    TourStep {
        title: "Your unified Library",
        body: "All your Steam, Epic, GOG, Riot, Xbox, EA, and local games live in one poster deck. Click any game for instant stats, properties, or one-click Play.",
        room: Room::Library,
    },
    TourStep {
        title: "Instant Ctrl+K Search",
        body: "Press Ctrl+K from anywhere to launch a game, jump rooms, activate a key, or run actions without lifting your hands off the keyboard.",
        room: Room::Library,
    },
    TourStep {
        title: "Store & Price Compare",
        body: "Browse featured picks, search Steam titles, and compare official prices across Steam, Epic, GOG, and Ubisoft alongside historical lows.",
        room: Room::Store,
    },
    TourStep {
        title: "Built-in Discord",
        body: "Chat with friends, join voice calls, and watch hardware-decoded streams inside Exo's AMOLED design system without extra browser tabs.",
        room: Room::Discord,
    },
    TourStep {
        title: "Music without clutter",
        body: "Control Apple Music, Spotify, or YouTube Music right beside your games while playback stays out of your way.",
        room: Room::Music,
    },
    TourStep {
        title: "This PC & Exo AI",
        body: "Keep Windows tuned automatically, monitor live frame telemetry, manage Startup apps and drives, and tune Wooting keyboards and audio.",
        room: Room::Pc,
    },
    TourStep {
        title: "You're ready to play",
        body: "Use Settings anytime to sync Riot hours, customize motion, check for delta updates, or replay this tour.",
        room: Room::Settings,
    },
];

pub fn view(s: &AppState) -> El {
    let Some(idx) = s.tour_step else {
        return el();
    };
    let step = &STEPS[idx.min(STEPS.len() - 1)];
    let is_first = idx == 0;
    let is_last = idx + 1 >= STEPS.len();

    let dots = row().gap(4.0).center_y().children((0..STEPS.len()).map(|i| {
        let active = i == idx;
        el().key(format!("tour-dot-{i}"))
            .role("button")
            .label(format!("Step {}", i + 1))
            .focusable(true)
            .hit()
            .press_on_down()
            .size(22.0, 22.0)
            .center()
            .on_press(move |s: &mut AppState| s.jump_tour(i))
            .child(
                el().size(if active { 16.0 } else { 6.0 }, 6.0)
                    .radius(3.0)
                    .fill(if active { FG } else { Color::WHITE.alpha(0.28) })
                    .motion(SPRING_QUICK),
            )
    }));

    let body = col()
        .w_full()
        .px(22.0)
        .py(20.0)
        .gap(10.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(icon_sparkles(15.0, FG))
                        .child(
                            text(format!("Step {} of {}", idx + 1, STEPS.len()))
                                .font(type_small())
                                .color(fg_muted()),
                        ),
                )
                .child(
                    el().key("tour-skip")
                        .role("button")
                        .label("Skip")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .px(10.0)
                        .h(26.0)
                        .radius(RADIUS_PILL)
                        .center()
                        .hover(Color::WHITE.alpha(0.10))
                        .on_press(|s: &mut AppState| s.finish_tour())
                        .child(text("Skip").font(type_small()).color(fg_muted())),
                ),
        )
        .child(text(step.title).font(type_title()).color(FG).w_full().wrap())
        .child(text(step.body).font(type_body()).color(fg_muted()).w_full().wrap());

    let mut nav = row().gap(8.0).center_y();
    if !is_first {
        nav = nav.child(
            glass_button("tour-prev", "Back", None)
                .on_press(move |s: &mut AppState| s.jump_tour(idx.saturating_sub(1))),
        );
    }
    nav = nav.child(
        primary_button("tour-next", if is_last { "Done" } else { "Next" }, None).on_press(
            move |s: &mut AppState| {
                if is_last {
                    s.finish_tour();
                } else {
                    s.jump_tour(idx + 1);
                }
            },
        ),
    );

    let footer = row()
        .w_full()
        .px(22.0)
        .py(14.0)
        .justify_between()
        .center_y()
        .child(dots)
        .child(nav);

    let card = glass_panel("exo-tour-card", 460.0)
        .motion(SPRING_BASE)
        .child(body)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer);

    el().key("tour-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.50))
        .child(
            col()
                .w_full()
                .h_full()
                .pb(44.0)
                .justify_end()
                .items_center()
                .child(card),
        )
}
