use crate::components::popover_transition_from;
use crate::glass::{ExoGlass, exo_glass};
use crate::icons::{icon_check, icon_chevron_right};
use crate::tokens::{
    FG, RADIUS_CARD, RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, bad, fg_faint, fg_muted, hairline,
    hover_fill, pressed_fill, type_control, type_small,
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
    pub trailing_icon: Option<El>,
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
            trailing_icon: None,
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

    pub fn trailing_icon(mut self, ic: El) -> Self {
        self.trailing_icon = Some(ic);
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

fn glass_menu_surface(key: String, width: f32, _blur: bool, origin: Vec2) -> El {
    let radius = RADIUS_CARD + 2.0;
    col()
        .key(key)
        .min_w(width)
        .px(6.0)
        .py(6.0)
        .gap(2.0)
        .radius(radius)
        .continuous_corners()
        .clip_rounded(radius)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(exo_glass(ExoGlass::Menu, false))
        .transition(popover_transition_from(origin))
}

fn keycap_badge(hint: String) -> El {
    text(hint).font(type_small()).color(fg_faint())
}

fn first_enabled_key<S: 'static>(entries: &[MenuEntry<S>]) -> Option<String> {
    entries.iter().find_map(|entry| match entry {
        MenuEntry::Item(item) if !item.disabled => Some(item.key.clone()),
        _ => None,
    })
}

struct MenuRowOpts {
    open_submenu: Option<usize>,
    gamepad_focus: bool,
    is_submenu: bool,
    parent_trigger_key: Option<String>,
    item_cursor: Cursor,
}

fn build_menu_rows<S: 'static>(
    entries: Vec<MenuEntry<S>>,
    opts: MenuRowOpts,
    on_dismiss: &MenuDismissCb<S>,
    on_submenu: &MenuSubmenuCb<S>,
) -> (Vec<El>, Option<SubmenuBranch<S>>) {
    let MenuRowOpts {
        open_submenu,
        gamepad_focus,
        is_submenu,
        parent_trigger_key,
        item_cursor,
    } = opts;
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
                        .py(3.0)
                        .px(10.0)
                        .child(el().w_full().h(1.0).fill(hairline())),
                );
            }
            MenuEntry::Section(title) => {
                rows.push(
                    el().w_full()
                        .px(10.0)
                        .pt(if idx > 0 { 10.0 } else { 4.0 })
                        .pb(2.0)
                        .child(text(title).font(type_small()).color(fg_faint())),
                );
            }
            MenuEntry::Item(item) => {
                let MenuItem {
                    key,
                    label,
                    icon: item_icon,
                    trailing_icon,
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
                    .min_h(34.0)
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
                        item_cursor
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
                if let Some(tic) = trailing_icon {
                    item_row = item_row.child(el().size(16.0, 16.0).center().child(tic));
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
    build_context_menu_layers(spec, &[], Cursor::Pointer)
}

pub fn context_menu_layers_live<S: 'static>(
    spec: ContextMenuSpec<S>,
    live_keys: &[&str],
) -> Vec<El> {
    build_context_menu_layers(spec, live_keys, Cursor::Pointer)
}

pub fn context_menu_layers_with_cursor<S: 'static>(
    spec: ContextMenuSpec<S>,
    live_keys: &[&str],
    item_cursor: Cursor,
) -> Vec<El> {
    build_context_menu_layers(spec, live_keys, item_cursor)
}

fn build_context_menu_layers<S: 'static>(
    spec: ContextMenuSpec<S>,
    live_keys: &[&str],
    item_cursor: Cursor,
) -> Vec<El> {
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

    let mut menu_nodes = Vec::with_capacity(3);

    let (anchor_key, side, align, gap, origin) = match anchor {
        MenuAnchor::Point(x, y) => {
            let pt_key = format!("{id}-anchor-pt-{}-{}", x.round() as i32, y.round() as i32);
            menu_nodes.push(el().key(pt_key.clone()).absolute(x, y).size(0.0, 0.0));
            (pt_key, Side::Bottom, Align::Start, 1.0, Vec2::new(0.0, 0.0))
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
        MenuRowOpts {
            open_submenu,
            gamepad_focus,
            is_submenu: false,
            parent_trigger_key: None,
            item_cursor,
        },
        &on_dismiss,
        &on_submenu,
    );

    let mut main_menu = glass_menu_surface(format!("{id}-menu"), width, blur, origin)
        .role("menu")
        .focus_group()
        .anchored_fit(anchor_key, side, align, gap)
        .children(rows);

    if let Some(ref rf) = return_focus {
        main_menu = main_menu.focus_return(rf.clone());
    }

    menu_nodes.push(main_menu);

    if let Some((trigger_key, sub_entries)) = active_sub {
        let (sub_rows, _) = build_menu_rows(
            sub_entries,
            MenuRowOpts {
                open_submenu: None,
                gamepad_focus,
                is_submenu: true,
                parent_trigger_key: Some(trigger_key.clone()),
                item_cursor,
            },
            &on_dismiss,
            &on_submenu,
        );
        let sub_menu = glass_menu_surface(
            format!("{id}-submenu"),
            (width - 20.0).max(180.0),
            submenu_blur,
            Vec2::new(0.0, 0.0),
        )
        .role("menu")
        .focus_group()
        .anchored_fit(trigger_key, Side::Right, Align::Start, 4.0)
        .children(sub_rows);
        menu_nodes.push(sub_menu);
    }

    let dismiss_modal = Rc::clone(&on_dismiss);
    let root = if live_keys.is_empty() {
        el().key(format!("{id}-root"))
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .modal_tx(true, move |s: &mut S, tx| dismiss_modal(s, tx))
            .children(menu_nodes)
    } else {
        el().key(format!("{id}-root"))
            .absolute(0.0, 0.0)
            .size(0.0, 0.0)
            .modal_tx(true, move |s: &mut S, tx| dismiss_modal(s, tx))
            .modal_live(live_keys.iter().copied())
            .children(menu_nodes)
    };
    vec![root]
}

#[cfg(test)]
mod tests {
    use super::*;
    use rune::{Cx, Env, Runtime};

    struct MenuDemo {
        point: Option<(f32, f32)>,
    }

    fn demo_view(s: &MenuDemo, _cx: &mut Cx<MenuDemo>) -> El {
        let mut root = el().w_full().h_full();
        if let Some((x, y)) = s.point {
            let entries = vec![
                MenuEntry::item(MenuItem::new("copy", "Copy")),
                MenuEntry::item(MenuItem::new("open", "Open")),
            ];
            let layers = context_menu_layers(ContextMenuSpec {
                id: "test-ctx".into(),
                anchor: MenuAnchor::Point(x, y),
                width: 200.0,
                blur: true,
                submenu_blur: false,
                open_submenu: None,
                return_focus: None,
                gamepad_focus: false,
                entries,
                on_dismiss: Rc::new(|st: &mut MenuDemo, _| st.point = None),
                on_submenu: Rc::new(|_: &mut MenuDemo, _, _| {}),
            });
            root = root.children(layers);
        }
        root
    }

    #[test]
    fn point_menu_reopening_at_new_coords_anchors_to_new_position() {
        let mut rt = Runtime::new(
            MenuDemo {
                point: Some((120.0, 140.0)),
            },
            demo_view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        let (first_rect, _) = rt.presented("test-ctx-menu").expect("first menu");
        assert!(
            (first_rect[0] - 120.0).abs() < 24.0 && (first_rect[1] - 140.0).abs() < 24.0,
            "first menu should open at (120, 140), got {first_rect:?}"
        );

        // Immediately relocate the point menu without settling an exit frame first.
        rt.update(|s, _| s.point = Some((980.0, 460.0)));
        rt.frame(2.0 / 60.0);
        rt.settle_all();
        rt.frame(3.0 / 60.0);
        let (second_rect, _) = rt.presented("test-ctx-menu").expect("second menu");
        assert!(
            (second_rect[0] - 980.0).abs() < 24.0 && (second_rect[1] - 460.0).abs() < 24.0,
            "relocated menu should anchor at (980, 460), not stick to old point: {second_rect:?}"
        );
    }
}
