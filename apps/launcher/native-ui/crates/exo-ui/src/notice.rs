use crate::state::{AppState, NoticeItem, NoticeTone};
use exo_kit::{
    FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, SPRING_BASE, SPRING_MOVE, SPRING_QUICK, bad,
    fg_muted, glass_panel_opt, good, hover_fill, icon_close, pressed_fill, type_body,
};
use rune::{El, Phase, Spring, SpringPair, Transition, col, el, row, text};

pub const TOAST_WIDTH: f32 = 320.0;
pub const SWIPE_DISMISS_DX: f32 = 106.0;
pub const SWIPE_DISMISS_VX: f32 = 420.0;
const SWIPE_MIN_DX: f32 = -24.0;

fn toast_transition() -> Transition {
    let enter = Transition::offset(0.0, 8.0).combined(Transition::opacity());
    let exit = Transition::offset(0.0, 4.0).combined(Transition::opacity());
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_BASE))
}

fn toast_card(item: &NoticeItem, allow_blur: bool) -> El {
    let id = item.id;
    let drag_x = item.drag_x;
    let drag_opacity = (1.0 - drag_x.max(0.0) / 220.0).clamp(0.15, 1.0);

    let mut content = row().w_full().gap(10.0).center_y();
    match item.tone {
        NoticeTone::Good => {
            content = content.child(el().size(7.0, 7.0).radius(3.5).fill(good()));
        }
        NoticeTone::Bad => {
            content = content.child(el().size(7.0, 7.0).radius(3.5).fill(bad()));
        }
        NoticeTone::Neutral => {}
    }

    let close_btn = el()
        .key(format!("notice-close-{id}"))
        .role("button")
        .label("Dismiss")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(24.0, 24.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press_tx(move |s: &mut AppState, tx| {
            tx.animated();
            s.dismiss_notice(id);
        })
        .child(icon_close(13.0, fg_muted()));

    content = content
        .child(
            text(item.text.clone())
                .font(type_body())
                .color(FG)
                .grow(1.0)
                .wrap(),
        )
        .child(close_btn);

    glass_panel_opt(format!("notice-{id}"), TOAST_WIDTH, allow_blur)
        .role("status")
        .label(item.text.clone())
        .radius(RADIUS_CARD)
        .clip_rounded(RADIUS_CARD)
        .px(14.0)
        .py(10.0)
        .offset(drag_x, 0.0)
        .offset_motion(if item.dragging {
            Spring::NONE
        } else {
            SPRING_MOVE
        })
        .opacity(drag_opacity)
        .opacity_motion(if item.dragging {
            Spring::NONE
        } else {
            SPRING_BASE
        })
        .transition(toast_transition())
        .drag_threshold(6.0)
        .on_hover_change(move |s: &mut AppState, hovering, tx| {
            s.set_notice_hovered(id, hovering, tx);
        })
        .on_drag(move |s: &mut AppState, d, tx| match d.phase {
            Phase::Down => {
                s.begin_notice_drag(id, tx.clock());
            }
            Phase::Move => {
                let x = d.dx.max(SWIPE_MIN_DX);
                s.update_notice_drag(id, x, tx.clock());
            }
            Phase::Up => {
                let cancelled = d.cancelled();
                tx.animated();
                s.end_notice_drag(id, cancelled, tx);
            }
        })
        .child(content)
}

pub fn view(s: &AppState, allow_blur: bool) -> El {
    let last_idx = s.notices.len().saturating_sub(1);
    col()
        .key("notice-stack")
        .pin(None, None, Some(20.0), Some(20.0))
        .w(TOAST_WIDTH)
        .gap(8.0)
        .children(
            s.notices
                .iter()
                .enumerate()
                .map(|(idx, item)| toast_card(item, allow_blur && idx == last_idx)),
        )
}
