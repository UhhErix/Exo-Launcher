use crate::components::popover_transition_from;
use crate::glass::{ExoGlass, apply_glass};
use crate::icons::{icon_check, icon_chevron_down};
use crate::tokens::{
    FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, fg_faint,
    fg_muted, glass_rim_bottom, glass_rim_top, glass_still_control_fill, hover_fill, pressed_fill,
    type_control,
};
use rune::{Align, Cursor, El, Key, Side, Tx, Vec2, col, el, row, text};
use std::rc::Rc;

pub struct SelectOption {
    pub value: String,
    pub label: String,
    pub icon: Option<El>,
    pub disabled: bool,
}

impl SelectOption {
    pub fn new(value: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            value: value.into(),
            label: label.into(),
            icon: None,
            disabled: false,
        }
    }

    pub fn icon(mut self, ic: El) -> Self {
        self.icon = Some(ic);
        self
    }

    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }
}

pub type SelectPickCb<S> = Rc<dyn Fn(&mut S, &str, &mut Tx)>;
pub type SelectDismissCb<S> = Rc<dyn Fn(&mut S, &mut Tx)>;

pub struct SelectTrigger {
    pub key: String,
    pub aria_label: String,
    pub display_label: String,
    pub leading_icon: Option<El>,
    pub open: bool,
    pub disabled: bool,
    pub gamepad_focus: bool,
    pub flat: bool,
}

impl SelectTrigger {
    pub fn new(
        key: impl Into<String>,
        aria_label: impl Into<String>,
        display_label: impl Into<String>,
    ) -> Self {
        Self {
            key: key.into(),
            aria_label: aria_label.into(),
            display_label: display_label.into(),
            leading_icon: None,
            open: false,
            disabled: false,
            gamepad_focus: false,
            flat: false,
        }
    }

    pub fn leading(mut self, ic: Option<El>) -> Self {
        self.leading_icon = ic;
        self
    }

    pub fn open(mut self, open: bool) -> Self {
        self.open = open;
        self
    }

    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }

    pub fn gamepad_focus(mut self, gamepad_focus: bool) -> Self {
        self.gamepad_focus = gamepad_focus;
        self
    }

    /// A flat field (no backdrop blur), as React's library header controls are.
    pub fn flat(mut self, flat: bool) -> Self {
        self.flat = flat;
        self
    }
}

pub fn select_trigger<S: 'static>(
    spec: SelectTrigger,
    on_toggle: impl Fn(&mut S, &mut Tx) + Clone + 'static,
) -> El {
    let SelectTrigger {
        key,
        aria_label,
        display_label,
        leading_icon,
        open,
        disabled,
        gamepad_focus,
        flat,
    } = spec;
    let mut trigger = row()
        .key(key)
        .role("combobox")
        .label(aria_label)
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .hit()
        .disabled(disabled)
        .focusable(!disabled)
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .motion(SPRING_QUICK);
    if flat {
        trigger = trigger
            .fill(if open {
                hover_fill()
            } else {
                glass_still_control_fill()
            })
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
    } else {
        if open {
            trigger = trigger.fill(hover_fill());
        }
        trigger = apply_glass(trigger, ExoGlass::Control);
    }

    if !disabled {
        let toggle_press = on_toggle.clone();
        let toggle_key = on_toggle;
        trigger = trigger
            .press_on_down()
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press_tx(move |s: &mut S, tx| toggle_press(s, tx))
            .on_key(move |s: &mut S, ev, tx| {
                let is_space = matches!(&ev.key, Key::Text(t) if t == " ");
                if matches!(ev.key, Key::Down | Key::Up | Key::Enter) || is_space {
                    toggle_key(s, tx);
                    return true;
                }
                false
            });
        if flat {
            trigger = trigger.press_scale(PRESS_SCALE);
        }
        if gamepad_focus {
            trigger = trigger.focus_ring(2.0, 3.0, FG);
        }
    } else {
        trigger = trigger.on_hover(|_: &mut S| {});
    }

    if let Some(ic) = leading_icon {
        trigger = trigger.child(ic);
    }

    trigger
        .child(text(display_label).font(type_control()).color(if disabled {
            fg_faint()
        } else {
            FG
        }))
        .child(icon_chevron_down(
            12.0,
            if disabled { fg_faint() } else { fg_muted() },
        ))
}

pub struct SelectMenuSpec<S: 'static> {
    pub id: String,
    pub trigger_key: String,
    pub selected: String,
    pub options: Vec<SelectOption>,
    pub width: f32,
    pub align: Align,
    pub gamepad_focus: bool,
    pub on_select: SelectPickCb<S>,
    pub on_dismiss: SelectDismissCb<S>,
}

pub fn select_menu_layers<S: 'static>(spec: SelectMenuSpec<S>) -> Vec<El> {
    let SelectMenuSpec {
        id,
        trigger_key,
        selected,
        options,
        width,
        align,
        gamepad_focus,
        on_select,
        on_dismiss,
    } = spec;

    let has_icon_col = options.iter().any(|o| o.icon.is_some());
    let origin = match align {
        Align::End => Vec2::new(1.0, 0.0),
        Align::Center => Vec2::new(0.5, 0.0),
        Align::Start => Vec2::new(0.0, 0.0),
    };

    let rows: Vec<El> = options
        .into_iter()
        .map(|opt| {
            let is_selected = opt.value == selected;
            let opt_key = format!("{id}-opt-{}", opt.value);
            let value = opt.value;
            let mut item = row()
                .key(opt_key)
                .role("option")
                .label(opt.label.clone())
                .checked(is_selected)
                .focus_selected(is_selected)
                .w_full()
                .min_h(34.0)
                .px(10.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .continuous_corners()
                .hit()
                .disabled(opt.disabled)
                .focusable(!opt.disabled)
                .cursor(if opt.disabled {
                    Cursor::NotAllowed
                } else {
                    Cursor::Default
                })
                .fill(TRANSPARENT)
                .motion(SPRING_QUICK);

            if !opt.disabled {
                let select_cb = Rc::clone(&on_select);
                let dismiss_cb = Rc::clone(&on_dismiss);
                item = item
                    .press_on_down()
                    .press_scale(0.985)
                    .focus_highlight(hover_fill())
                    .press(pressed_fill())
                    .on_press_tx(move |s: &mut S, tx| {
                        select_cb(s, &value, tx);
                        dismiss_cb(s, tx);
                    });
                if gamepad_focus {
                    item = item.focus_ring(2.0, 3.0, FG);
                }
            } else {
                item = item.on_hover(|_: &mut S| {});
            }

            if has_icon_col {
                let mut slot = el().size(18.0, 18.0).center();
                if let Some(ic) = opt.icon {
                    slot = slot.child(ic);
                }
                item = item.child(slot);
            }

            item = item.child(
                text(opt.label)
                    .font(type_control())
                    .color(if opt.disabled { fg_faint() } else { FG })
                    .grow(1.0),
            );

            if is_selected {
                item = item.child(icon_check(14.0, FG));
            }
            item
        })
        .collect();

    let dismiss_modal = Rc::clone(&on_dismiss);
    let panel_radius = RADIUS_CARD + 2.0;
    let panel = apply_glass(
        col()
            .key(format!("{id}-menu"))
            .role("listbox")
            .focus_group()
            .min_w(width)
            .px(6.0)
            .py(6.0)
            .gap(2.0)
            .radius(panel_radius)
            .continuous_corners()
            .clip_rounded(panel_radius)
            .hit()
            .cursor(Cursor::Default)
            .anchored_fit(trigger_key.clone(), Side::Bottom, align, 4.0)
            .transition(popover_transition_from(origin))
            .modal_tx(true, move |s: &mut S, tx| dismiss_modal(s, tx))
            .modal_live(["top-bar", trigger_key.as_str()])
            .focus_return(trigger_key)
            .children(rows),
        ExoGlass::Menu,
    );

    vec![panel]
}
