#![forbid(unsafe_code)]

use exo_kit::{
    BG, EXO_GLASS_POLICY, ExoGlass, FG, FONT_FAMILIES, FONTS, RADIUS_PANEL, RADIUS_PILL,
    apply_glass, bad, hex, type_control, type_page, type_small,
};
use rune::{App, AppSpec, Cx, El, col, el, row, text};

struct Lab;

const W: f32 = 1280.0;
const H: f32 = 720.0;

fn backdrop() -> El {
    let stripes = [
        0xff3b30, 0xff9500, 0xffcc00, 0x34c759, 0x00c7be, 0x30b0c7, 0x32ade6, 0x007aff, 0x5856d6,
        0xaf52de, 0xff2d55, 0xa2845e,
    ];
    let mut bars = row().absolute(0.0, 0.0).size(W, 250.0);
    for n in 0..40 {
        bars = bars.child(
            el().w(32.0)
                .h_full()
                .fill(hex(stripes[n % stripes.len()], 1.0)),
        );
    }
    let mut root = el()
        .absolute(0.0, 0.0)
        .size(W, H)
        .linear_gradient(
            2.2,
            [
                (0.0, hex(0x1a2a44, 1.0)),
                (0.5, hex(0x6b3a52, 1.0)),
                (1.0, hex(0xd9a35f, 1.0)),
            ],
        )
        .child(bars);
    for n in 0..64 {
        root = root.child(
            el().absolute(n as f32 * 20.0, 250.0)
                .size(1.0, 250.0)
                .fill(FG.alpha(0.85)),
        );
    }
    for n in 0..13 {
        root = root.child(
            el().absolute(0.0, 250.0 + n as f32 * 20.0)
                .size(W, 1.0)
                .fill(FG.alpha(0.85)),
        );
    }
    root.child(
        text("Liquid Glass bends what is behind it 0123456789")
            .absolute(24.0, 530.0)
            .font(type_page())
            .color(FG),
    )
    .child(
        text("The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog.")
            .absolute(24.0, 590.0)
            .font(type_control())
            .color(FG),
    )
    .child(
        el().absolute(0.0, 640.0).size(W, 80.0).fill(BG),
    )
}

fn label(s: &str) -> El {
    text(s.to_string()).font(type_control()).color(FG)
}

fn capsule(tier: ExoGlass, w: f32, h: f32, name: &str) -> El {
    apply_glass(
        row()
            .size(w, h)
            .center()
            .radius(RADIUS_PILL)
            .child(label(name)),
        tier,
    )
}

fn slab(tier: ExoGlass, w: f32, h: f32, name: &str) -> El {
    apply_glass(
        col()
            .size(w, h)
            .padding(16.0)
            .gap(8.0)
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .child(label(name))
            .child(
                text("Words on the slab stay readable.")
                    .font(type_small())
                    .color(FG.alpha(0.72)),
            ),
        tier,
    )
}

fn band(y: f32) -> El {
    row()
        .absolute(24.0, y)
        .gap(14.0)
        .center_y()
        .child(capsule(
            ExoGlass::Bar,
            420.0,
            36.0,
            "Library  Store  Discord  Music",
        ))
        .child(capsule(ExoGlass::Control, 132.0, 32.0, "Control"))
        .child(capsule(ExoGlass::Control, 40.0, 40.0, "+"))
        .child(capsule(ExoGlass::ClearMedia, 150.0, 28.0, "Clear media"))
        .child(capsule(
            ExoGlass::Tinted(bad().alpha(0.45)),
            96.0,
            28.0,
            "Tinted",
        ))
}

fn slabs(y: f32) -> El {
    row()
        .absolute(24.0, y)
        .gap(14.0)
        .child(slab(ExoGlass::Menu, 220.0, 150.0, "Menu"))
        .child(slab(ExoGlass::Popover, 260.0, 150.0, "Popover"))
        .child(slab(ExoGlass::Sheet, 360.0, 150.0, "Sheet"))
}
fn view(_s: &Lab, _cx: &mut Cx<Lab>) -> El {
    el().size(W, H)
        .fill(BG)
        .child(backdrop())
        .child(band(40.0))
        .child(band(150.0))
        .child(band(262.0))
        .child(slabs(340.0))
        .child(band(540.0))
        .child(band(662.0))
}

fn main() {
    let mut spec = AppSpec::new("Exo glass lab", (W, H), || Lab, view);
    spec.fonts = FONTS;
    spec.families = FONT_FAMILIES;
    App::new(spec).glass(EXO_GLASS_POLICY).run();
}
