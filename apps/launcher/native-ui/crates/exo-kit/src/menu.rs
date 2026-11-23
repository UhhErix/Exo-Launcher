use crate::components::popover_transition_from;
use crate::icons::{icon_check, icon_chevron_right};
use crate::tokens::{
    FG, GLASS_PANEL_SIGMA, RADIUS_CARD, RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, bad, fg_faint,
    fg_muted, glass_panel_fill, glass_panel_shadow_color, glass_rim_bottom, glass_rim_top,
    glass_still_panel_fill, hairline, hover_fill, pressed_fill, subtle_fill, type_control,
    type_label, type_small,
};
use rune::{Align, Cursor, El, Key, Side, Tx, Vec2, col, el, row, text};
use std::rc::Rc;

pub type MenuAction<S> = Rc<dyn Fn(&mut S, &mut Tx)>;

pub enum MenuAnchor {
    Point(f32, f32),
    Element {
        key: String,
        side: Side,
        align: Align,
        gap: f32,
    },
}

pub struct MenuItem<S: 'static> {
    pub key: String,
    pub label: String,
    pub icon: Option<El>,
    pub hint: Option<String>,
    pub checked: bool,
    pub danger: bool,
    pub disabled: bool,
    pub on_select: Option<MenuAction<S>>,
    pub submenu: Option<Vec<MenuEntry<S>>>,
}

impl<S: 'static> MenuItem<S> {
    pub fn new(key: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            key: key.into(),
            label: label.into(),
            icon: None,
            hint: None,
            checked: false,
            danger: false,
            disabled: false,
            on_select: None,
            submenu: None,
        }
    }

    pub fn icon(mut self, ic: El) -> Self {
        self.icon = Some(ic);
        self
    }

    pub fn hint(mut self, hint: impl Into<String>) -> Self {
        self.hint = Some(hint.into());
        self
    }

    pub fn checked(mut self, on: bool) -> Self {
        self.checked = on;
        self
    }

    pub fn danger(mut self) -> Self {
        self.danger = true;
        self
    }

    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }

    pub fn on_select(mut self, f: impl Fn(&mut S, &mut Tx) + 'static) -> Self {
        self.on_select = Some(Rc::new(f));
        self
    }

    pub fn submenu(mut self, items: Vec<MenuEntry<S>>) -> Self {
        self.submenu = Some(items);
        self
    }
}

pub type MenuDismissCb<S> = Rc<dyn Fn(&mut S, &mut Tx)>;
pub type MenuSubmenuCb<S> = Rc<dyn Fn(&mut S, Option<usize>, &mut Tx)>;
type SubmenuBranch<S> = (String, Vec<MenuEntry<S>>);

pub enum MenuEntry<S: 'static> {
    Item(Box<MenuItem<S>>),
    Separator,
    Section(String),
}

impl<S: 'static> MenuEntry<S> {
    pub fn item(item: MenuItem<S>) -> Self {
        Self::Item(Box::new(item))
    }
}

pub struct ContextMenuSpec<S: 'static> {
    pub id: String,
    pub anchor: MenuAnchor,
    pub width: f32,
    pub blur: bool,
    pub submenu_blur: bool,
    pub open_submenu: Option<usize>,
    pub return_focus: Option<String>,
    pub gamepad_focus: bool,
    pub entries: Vec<MenuEntry<S>>,
    pub on_dismiss: MenuDismissCb<S>,
    pub on_submenu: MenuSubmenuCb<S>,
}

fn glass_menu_surface(key: String, width: f32, blur: bool, origin: Vec2) -> El {
    let mut panel = col()
        .key(key)
        .min_w(width)
        .px(6.0)
        .py(6.0)
        .gap(2.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .hit()
        .cursor(Cursor::Default)
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .shadow(0.0, 40.0, glass_panel_shadow_color())
        .transition(popover_transition_from(origin));
    if blur {
        panel = panel.backdrop_blur(GLASS_PANEL_SIGMA);
    }
    panel
}

fn keycap_badge(hint: String) -> El {
    el().px(6.0)
        .py(2.0)
        .radius(5.0)
        .fill(subtle_fill())
        .child(text(hint).font(type_small()).color(fg_faint()))
}

fn first_enabled_key<S: 'static>(entries: &[MenuEntry<S>]) -> Option<String> {
    entries.iter().find_map(|entry| match entry {
        MenuEntry::Item(item) if !item.disabled => Some(item.key.clone()),
        _ => None,
    })
}

fn build_menu_rows<S: 'static>(
    entries: Vec<MenuEntry<S>>,
    open_submenu: Option<usize>,
    gamepad_focus: bool,
    is_submenu: bool,
    parent_trigger_key: Option<String>,
    on_dismiss: &MenuDismissCb<S>,
    on_submenu: &MenuSubmenuCb<S>,
) -> (Vec<El>, Option<SubmenuBranch<S>>) {
    let has_icon_col = entries.iter().any(|e| match e {
        MenuEntry::Item(item) => item.icon.is_some(),
        _ => false,
    });

    let mut rows = Vec::with_capacity(entries.len());
    let mut active_sub: Option<SubmenuBranch<S>> = None;

    for (idx, entry) in entries.into_iter().enumerate() {
        match entry {
            MenuEntry::Separator => {
                rows.push(
                    el().w_full()
                        .py(4.0)
                        .px(8.0)
                        .child(el().w_full().h(1.0).fill(hairline())),
                );
            }
            MenuEntry::Section(title) => {
                rows.push(
                    el().w_full()
                        .px(10.0)
                        .pt(6.0)
                        .pb(2.0)
                        .child(text(title).font(type_label()).color(fg_faint())),
                );
            }
            MenuEntry::Item(item) => {
                let MenuItem {
                    key,
                    label,
                    icon: item_icon,
                    hint,
                    checked,
                    danger,
                    disabled,
                    on_select,
                    submenu,
                } = *item;

                let has_sub = submenu.is_some();
                let is_sub_open = !is_submenu && has_sub && open_submenu == Some(idx);
                let first_sub_key = submenu.as_deref().and_then(first_enabled_key);

                if is_sub_open && let Some(sub_entries) = submenu {
                    active_sub = Some((key.clone(), sub_entries));
                }

                let text_color = if disabled {
                    fg_faint()
                } else if danger {
                    bad()
                } else {
                    FG
                };

                let mut item_row = row()
                    .key(key.clone())
                    .role("menuitem")
                    .label(label.clone())
                    .w_full()
                    .min_h(36.0)
                    .px(10.0)
                    .gap(10.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .continuous_corners()
                    .hit()
                    .disabled(disabled)
                    .focusable(!disabled)
                    .cursor(if disabled {
                        Cursor::Default
                    } else {
                        Cursor::Pointer
                    })
                    .fill(if is_sub_open {
                        hover_fill()
                    } else {
                        TRANSPARENT
                    })
                    .motion(SPRING_QUICK);

                if !disabled {
                    item_row = item_row
                        .press_on_down()
                        .press_scale(0.985)
                        .focus_highlight(hover_fill())
                        .press(pressed_fill());
                    if gamepad_focus {
                        item_row = item_row.focus_ring(2.0, 3.0, FG);
                    }

                    if has_sub {
                        let on_sub_hover = Rc::clone(on_submenu);
                        item_row = item_row.on_hover_change(move |s: &mut S, hovering, tx| {
                            if hovering {
                                on_sub_hover(s, Some(idx), tx);
                            }
                        });
                        let on_sub_press = Rc::clone(on_submenu);
                        let sub_focus = first_sub_key.clone();
                        item_row = item_row.on_press_tx(move |s: &mut S, tx| {
                            on_sub_press(s, Some(idx), tx);
                            if let Some(ref k) = sub_focus {
                                tx.focus(k.clone(), true);
                            }
                        });
                        let on_sub_key = Rc::clone(on_submenu);
                        let sub_focus_key = first_sub_key;
                        item_row = item_row.on_key(move |s: &mut S, ev, tx| {
                            if ev.key == Key::Right {
                                on_sub_key(s, Some(idx), tx);
                                if let Some(ref k) = sub_focus_key {
                                    tx.focus(k.clone(), true);
                                }
                                return true;
                            }
                            false
                        });
                    } else {
                        if !is_submenu {
                            let on_sub_close = Rc::clone(on_submenu);
                            item_row = item_row.on_hover_change(move |s: &mut S, hovering, tx| {
                                if hovering {
                                    on_sub_close(s, None, tx);
                                }
                            });
                        } else if let Some(ref trigger_k) = parent_trigger_key {
                            let on_sub_close = Rc::clone(on_submenu);
                            let back_key = trigger_k.clone();
                            item_row = item_row.on_key(move |s: &mut S, ev, tx| {
                                if ev.key == Key::Left {
                                    on_sub_close(s, None, tx);
                                    tx.focus(back_key.clone(), true);
                                    return true;
                                }
                                false
                            });
                        }
                        if let Some(action) = on_select {
                            let dismiss = Rc::clone(on_dismiss);
                            item_row = item_row.on_press_tx(move |s: &mut S, tx| {
                                dismiss(s, tx);
                                action(s, tx);
                            });
                        }
                    }
                } else {
                    item_row = item_row.on_hover(|_: &mut S| {});
                }

                if has_icon_col {
                    let mut slot = el().size(16.0, 16.0).center();
                    if let Some(ic) = item_icon {
                        slot = slot.child(ic);
                    }
                    item_row = item_row.child(slot);
                }

                item_row =
                    item_row.child(text(label).font(type_control()).color(text_color).grow(1.0));

                if let Some(h) = hint {
                    item_row = item_row.child(keycap_badge(h));
                }
                if checked {
                    item_row = item_row.child(icon_check(14.0, FG));
                }
                if has_sub {
                    item_row = item_row.child(icon_chevron_right(13.0, fg_muted()));
                }

                rows.push(item_row);
            }
        }
    }

    (rows, active_sub)
}

pub fn context_menu_layers<S: 'static>(spec: ContextMenuSpec<S>) -> Vec<El> {
    let ContextMenuSpec {
        id,
        anchor,
        width,
        blur,
        submenu_blur,
        open_submenu,
        return_focus,
        gamepad_focus,
        entries,
        on_dismiss,
        on_submenu,
    } = spec;

    let mut layers = Vec::with_capacity(4);

    let dismiss_scrim = Rc::clone(&on_dismiss);
    let dismiss_scrim_sec = Rc::clone(&on_dismiss);
    layers.push(
        el().key(format!("{id}-scrim"))
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .hit()
            .cursor(Cursor::Default)
            .on_press_tx(move |s: &mut S, tx| dismiss_scrim(s, tx))
            .on_secondary_press(move |s: &mut S, _, tx| dismiss_scrim_sec(s, tx)),
    );

    let (anchor_key, side, align, gap, origin) = match anchor {
        MenuAnchor::Point(x, y) => {
            let pt_key = format!("{id}-anchor-pt");
            layers.push(el().key(pt_key.clone()).absolute(x, y).size(0.0, 0.0));
            (pt_key, Side::Bottom, Align::Start, 2.0, Vec2::new(0.0, 0.0))
        }
        MenuAnchor::Element {
            key,
            side,
            align,
            gap,
        } => {
            let origin = match align {
                Align::End => Vec2::new(1.0, 0.0),
                Align::Center => Vec2::new(0.5, 0.0),
                Align::Start => Vec2::new(0.0, 0.0),
            };
            (key, side, align, gap, origin)
        }
    };

    let (rows, active_sub) = build_menu_rows(
        entries,
        open_submenu,
        gamepad_focus,
        false,
        None,
        &on_dismiss,
        &on_submenu,
    );

    let dismiss_modal = Rc::clone(&on_dismiss);
    let mut main_menu = glass_menu_surface(format!("{id}-menu"), width, blur, origin)
        .role("menu")
        .focus_group()
        .anchored_fit(anchor_key, side, align, gap)
        .modal_tx(false, move |s: &mut S, tx| dismiss_modal(s, tx))
        .children(rows);

    if let Some(ref rf) = return_focus {
        main_menu = main_menu.focus_return(rf.clone());
    }

    layers.push(main_menu);

    if let Some((trigger_key, sub_entries)) = active_sub {
        let (sub_rows, _) = build_menu_rows(
            sub_entries,
            None,
            gamepad_focus,
            true,
            Some(trigger_key.clone()),
            &on_dismiss,
            &on_submenu,
        );
        let dismiss_sub = Rc::clone(&on_dismiss);
        let sub_menu = glass_menu_surface(
            format!("{id}-submenu"),
            (width - 20.0).max(180.0),
            submenu_blur,
            Vec2::new(0.0, 0.0),
        )
        .role("menu")
        .focus_group()
        .anchored_fit(trigger_key, Side::Right, Align::Start, 4.0)
        .modal_tx(false, move |s: &mut S, tx| dismiss_sub(s, tx))
        .children(sub_rows);
        layers.push(sub_menu);
    }

    layers
}
