use crate::events::{EventRegistry, HostEventKind};
use crate::state::{AppState, Room};
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, RADIUS_SMALL, SPRING_MOVE,
    SPRING_QUICK, TRANSPARENT, apply_glass, bad, fg_muted, good, hairline, hover_fill,
    icon_headphones, icon_headphones_off, icon_mic, icon_mic_off, icon_phone, icon_phone_off,
    popover_transition_from, pressed_fill, selected_fill, slider_range, slider_track, type_body,
    type_section, type_small,
};
use exo_rpc::{DiscordChannels, DiscordHome, DiscordVoiceMember, HostEvent};
use rune::{Cursor, El, Phase, SpringPair, SurfaceFit, Transition, col, el, row, text};
use serde_json::json;

const MAX_FACES: usize = 4;

#[derive(Debug, Clone, PartialEq)]
pub struct IslandPersonMenu {
    pub user_id: String,
    pub name: String,
    pub is_me: bool,
    pub x: f32,
    pub y: f32,
}

#[derive(Debug, Clone, Default)]
pub struct IslandState {
    pub call_started_at: Option<f64>,
    pub place_channel: Option<String>,
    pub place_server: Option<String>,
    pub place_icon: Option<String>,
    pub place_members: Vec<DiscordVoiceMember>,
    pub ringing_channel_id: Option<String>,
    pub ringing_caller: Option<String>,
    pub person_menu: Option<IslandPersonMenu>,
    pub dragging_user_volume: bool,
}

pub fn format_call_duration(elapsed_secs: u64) -> String {
    let h = elapsed_secs / 3600;
    let m = (elapsed_secs / 60) % 60;
    let s = elapsed_secs % 60;
    if h > 0 {
        format!("{h}:{m:02}:{s:02}")
    } else {
        format!("{m}:{s:02}")
    }
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on(HostEventKind::DiscordVoice, |state, ev| {
        let HostEvent::DiscordVoice(v) = ev else {
            return false;
        };
        let connected = v.state == "connected";
        let in_call = v.state != "off";
        if connected {
            if state.island.call_started_at.is_none() {
                state.island.call_started_at = Some(state.clock_secs());
            }
        } else {
            state.island.call_started_at = None;
        }
        if in_call {
            state.refresh_island_call_place();
        } else {
            state.island.place_channel = None;
            state.island.place_server = None;
            state.island.place_icon = None;
            state.island.place_members.clear();
            state.island.person_menu = None;
        }
        state.room != Room::Discord
    });

    reg.on(HostEventKind::DiscordChanged, |state, ev| {
        let HostEvent::DiscordChanged(dc) = ev else {
            return false;
        };
        if state.discord.voice.state != "off" && dc.guild_id == state.discord.voice.guild_id {
            state.refresh_island_call_place();
        }
        false
    });

    reg.on_raw("discord.call", |state, data| {
        let channel_id = data
            .get("channelId")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let ringing = data
            .get("ringing")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
        if channel_id.is_empty() {
            return false;
        }
        if ringing {
            let caller = state
                .discord
                .home
                .dms
                .iter()
                .find(|d| d.id == channel_id)
                .map(|d| d.name.clone())
                .or_else(|| {
                    data.get("caller")
                        .and_then(|v| v.as_str())
                        .map(|s| s.to_string())
                })
                .unwrap_or_else(|| "Incoming call".into());
            state.island.ringing_channel_id = Some(channel_id);
            state.island.ringing_caller = Some(caller);
            true
        } else if state.island.ringing_channel_id.as_deref() == Some(channel_id.as_str()) {
            state.island.ringing_channel_id = None;
            state.island.ringing_caller = None;
            true
        } else {
            false
        }
    });
}

impl AppState {
    pub fn island_in_call(&self) -> bool {
        let st = self.discord.voice.state.as_str();
        !st.is_empty() && st != "off"
    }

    pub fn island_visible(&self) -> bool {
        (self.island_in_call() && self.room != Room::Discord)
            || self.island.ringing_channel_id.is_some()
    }

    pub fn refresh_island_call_place(&mut self) {
        if !self.island_in_call() {
            return;
        }
        let Some(channel_id) = self.discord.voice.channel_id.clone() else {
            return;
        };
        if let Some(guild_id) = self.discord.voice.guild_id.clone() {
            if let Some(chans) = self.discord.channels_by_guild.get(&guild_id)
                && let Some(ch) = chans.channels.iter().find(|c| c.id == channel_id)
            {
                self.island.place_channel = Some(ch.name.clone());
                self.island.place_server = chans.name.clone();
                self.island.place_icon = chans.icon.clone();
                self.island.place_members = ch.members.clone().unwrap_or_default();
            }
            #[cfg(windows)]
            {
                let cid = channel_id.clone();
                self.call_host_async(
                    "discord.channels",
                    json!({ "guildId": guild_id }),
                    move |s, res: Result<DiscordChannels, String>| {
                        if let Ok(list) = res {
                            let ch = list.channels.iter().find(|c| c.id == cid);
                            s.island.place_channel =
                                Some(ch.map(|c| c.name.clone()).unwrap_or_else(|| "Voice".into()));
                            s.island.place_server = list.name;
                            s.island.place_icon = list.icon;
                            s.island.place_members =
                                ch.and_then(|c| c.members.clone()).unwrap_or_default();
                        }
                    },
                );
            }
        } else {
            if let Some(dm) = self.discord.home.dms.iter().find(|d| d.id == channel_id) {
                self.island.place_channel = Some(dm.name.clone());
                self.island.place_server = None;
                self.island.place_icon = dm.avatar.clone();
                self.island.place_members = dm.call.clone().unwrap_or_default();
            }
            #[cfg(windows)]
            {
                let cid = channel_id.clone();
                self.call_host_async(
                    "discord.home",
                    json!({}),
                    move |s, res: Result<DiscordHome, String>| {
                        if let Ok(home) = res {
                            let dm = home.dms.iter().find(|d| d.id == cid);
                            s.island.place_channel =
                                Some(dm.map(|d| d.name.clone()).unwrap_or_else(|| "Call".into()));
                            s.island.place_server = None;
                            s.island.place_icon = dm.and_then(|d| d.avatar.clone());
                            s.island.place_members =
                                dm.and_then(|d| d.call.clone()).unwrap_or_default();
                        }
                    },
                );
            }
        }
    }

    pub fn island_open_call(&mut self) {
        let guild_id = self.discord.voice.guild_id.clone();
        let channel_id = self.discord.voice.channel_id.clone();
        self.switch_room(Room::Discord);
        if let Some(gid) = guild_id {
            self.open_discord_guild(&gid);
            if let Some(cid) = channel_id {
                self.open_discord_channel(&cid);
            }
        } else if let Some(cid) = channel_id {
            self.open_discord_dm(&cid);
        }
    }

    pub fn island_answer_ring(&mut self, video: bool) {
        let Some(cid) = self.island.ringing_channel_id.take() else {
            return;
        };
        self.island.ringing_caller = None;
        self.switch_room(Room::Discord);
        self.open_discord_dm(&cid);
        #[cfg(windows)]
        self.call_host_async(
            "discord.voice",
            json!({ "action": "join", "guildId": Value::Null, "channelId": cid, "video": video }),
            |_, _: Result<Value, String>| {},
        );
        #[cfg(not(windows))]
        let _ = video;
    }

    pub fn island_decline_ring(&mut self) {
        let Some(cid) = self.island.ringing_channel_id.take() else {
            return;
        };
        self.island.ringing_caller = None;
        #[cfg(windows)]
        self.call_host_async(
            "discord.ring",
            json!({ "channelId": cid, "decline": true }),
            |_, _: Result<Value, String>| {},
        );
    }

    pub fn island_open_person_menu(&mut self, user_id: String, name: String, x: f32, y: f32) {
        let is_me = self
            .discord
            .home
            .me
            .as_ref()
            .is_some_and(|me| me.id == user_id);
        self.island.person_menu = Some(IslandPersonMenu {
            user_id,
            name,
            is_me,
            x,
            y,
        });
    }

    pub fn island_close_person_menu(&mut self) {
        self.island.person_menu = None;
        self.island.dragging_user_volume = false;
    }

    pub fn tick_island_timer(&mut self, now: f64, tx: &mut rune::Tx) -> bool {
        if self.discord.voice.state == "connected" {
            if self.island.call_started_at.is_none() {
                self.island.call_started_at = Some(now);
            }
            if self.room != Room::Discord {
                let elapsed = (now - self.island.call_started_at.unwrap_or(now)).max(0.0);
                let next_sec = elapsed.floor() + 1.0;
                let due = self.island.call_started_at.unwrap_or(now) + next_sec;
                tx.wake_at(due.max(now + 0.05));
                return true;
            }
        }
        false
    }
}

use serde_json::Value;

fn island_transition() -> Transition {
    let enter = Transition::offset(0.0, -8.0).combined(Transition::opacity());
    let exit = Transition::offset(0.0, -6.0).combined(Transition::opacity());
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

fn island_btn(
    key: &'static str,
    label: &'static str,
    icon: El,
    is_off: bool,
    is_leave: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    el().key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(TRANSPARENT)
        .hover(if is_leave {
            bad().alpha(0.18)
        } else if is_off {
            bad().alpha(0.14)
        } else {
            hover_fill()
        })
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(on_press)
        .child(icon)
}

pub fn capsule(s: &AppState, scale: f32) -> Option<El> {
    if let Some(caller) = s.island.ringing_caller.as_ref() {
        let mut ring = row()
            .key("exo-island-ring")
            .role("group")
            .label("Incoming call")
            .h(36.0)
            .pl(10.0)
            .pr(4.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .transition(island_transition())
            .child(el().size(7.0, 7.0).radius(3.5).fill(good()))
            .child(
                text(caller.clone())
                    .font(type_small())
                    .color(FG)
                    .max_w(120.0)
                    .truncate(),
            )
            .child(island_btn(
                "island-ring-answer",
                "Answer call",
                icon_phone(14.0, good()),
                false,
                false,
                |s| s.island_answer_ring(false),
            ))
            .child(island_btn(
                "island-ring-decline",
                "Decline call",
                icon_phone_off(14.0, bad()),
                false,
                true,
                |s| s.island_decline_ring(),
            ));
        ring = apply_glass(ring, ExoGlass::Tinted(good().alpha(0.32)));
        return Some(ring);
    }

    if !s.island_in_call() || s.room == Room::Discord {
        return None;
    }

    let voice = &s.discord.voice;
    let speaking = !s.discord.speaking.is_empty();
    let streaming =
        !s.discord.voice_ui.golive_state.is_empty() && s.discord.voice_ui.golive_state != "off";
    let connecting = voice.state == "connecting";
    let channel_label = if connecting {
        "Connecting".to_string()
    } else {
        s.island
            .place_channel
            .clone()
            .unwrap_or_else(|| "In a call".into())
    };

    let dot = el().size(7.0, 7.0).radius(3.5).fill(good()).outline(
        if speaking { 3.0 } else { 0.0 },
        0.0,
        good().alpha(0.30),
    );

    let leading = if let Some(ref icon_url) = s.island.place_icon
        && let Some(img) = s.discord_remote_image_sized(icon_url, 22.0, scale)
    {
        el().size(22.0, 22.0)
            .radius(RADIUS_PILL)
            .clip_rounded(RADIUS_PILL)
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
    } else {
        el().size(18.0, 18.0).center().child(dot)
    };

    let mut label_col = col().gap(0.0).min_w(0.0).child(
        text(channel_label)
            .font(type_small())
            .color(FG)
            .max_w(116.0)
            .truncate(),
    );
    if let Some(ref server) = s.island.place_server {
        label_col = label_col.child(
            text(server.clone())
                .font(rune::TextStyle::new(10.5, 450.0))
                .color(fg_muted())
                .max_w(116.0)
                .truncate(),
        );
    }

    let main_btn = row()
        .key("exo-island-open")
        .role("button")
        .label("Open Discord call")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(30.0)
        .pl(4.0)
        .pr(8.0)
        .gap(7.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.island_open_call())
        .child(leading)
        .child(label_col);

    let mut cap = row()
        .key("exo-island")
        .role("group")
        .label("Discord call")
        .h(36.0)
        .pl(3.0)
        .pr(3.0)
        .gap(3.0)
        .center_y()
        .radius(RADIUS_PILL)
        .transition(island_transition())
        .child(main_btn);

    if !s.island.place_members.is_empty() {
        let mut faces = row().gap(3.0).center_y().px(2.0);
        for member in s.island.place_members.iter().take(MAX_FACES) {
            let muted_user = s
                .discord
                .voice_ui
                .user_mix
                .get(&member.id)
                .is_some_and(|u| u.muted);
            let is_speaking = s.discord.speaking.contains(&member.id) && !muted_user;
            let initial = member
                .name
                .chars()
                .next()
                .map(|c| c.to_uppercase().to_string())
                .unwrap_or_else(|| "?".into());
            let uid = member.id.clone();
            let uname = member.name.clone();
            let mut face = if let Some(ref url) = member.avatar
                && let Some(img) = s.discord_remote_image_sized(url, 22.0, scale)
            {
                el().size(22.0, 22.0)
                    .radius(RADIUS_PILL)
                    .clip_rounded(RADIUS_PILL)
                    .pixels_fit(img.pixels(), SurfaceFit::Fill)
            } else {
                el().size(22.0, 22.0)
                    .radius(RADIUS_PILL)
                    .fill(selected_fill())
                    .center()
                    .child(
                        text(initial)
                            .font(rune::TextStyle::new(10.5, 600.0))
                            .color(FG),
                    )
            };
            if is_speaking {
                face = face.outline(2.0, 1.0, good());
            }
            face = face
                .key(format!("island-face-{}", member.id))
                .hit()
                .cursor(Cursor::Pointer)
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.island_open_person_menu(uid.clone(), uname.clone(), p.x, p.y);
                });
            faces = faces.child(face);
        }
        if s.island.place_members.len() > MAX_FACES {
            let extra = s.island.place_members.len() - MAX_FACES;
            faces = faces.child(
                el().h(22.0)
                    .px(6.0)
                    .radius(RADIUS_PILL)
                    .fill(selected_fill())
                    .center()
                    .child(
                        text(format!("+{extra}"))
                            .font(rune::TextStyle::new(10.5, 600.0))
                            .color(fg_muted()),
                    ),
            );
        }
        cap = cap.child(faces);
    }

    if streaming {
        cap = cap.child(
            el().px(6.0)
                .py(2.0)
                .radius(6.0)
                .fill(bad())
                .child(text("Live").font(type_small()).color(FG)),
        );
    }

    if let Some(started) = s.island.call_started_at {
        let elapsed = (s.clock_secs() - started).max(0.0) as u64;
        cap = cap.child(
            el().px(4.0).child(
                text(format_call_duration(elapsed))
                    .font(type_small())
                    .color(fg_muted()),
            ),
        );
    }

    cap = cap
        .child(island_btn(
            "island-mute",
            if voice.muted { "Unmute" } else { "Mute" },
            if voice.muted {
                icon_mic_off(15.0, bad())
            } else {
                icon_mic(15.0, fg_muted())
            },
            voice.muted,
            false,
            |s| s.toggle_discord_mute(),
        ))
        .child(island_btn(
            "island-deafen",
            if voice.deafened { "Undeafen" } else { "Deafen" },
            if voice.deafened {
                icon_headphones_off(15.0, bad())
            } else {
                icon_headphones(15.0, fg_muted())
            },
            voice.deafened,
            false,
            |s| s.toggle_discord_deafen(),
        ))
        .child(island_btn(
            "island-leave",
            "Leave call",
            icon_phone_off(15.0, fg_muted()),
            false,
            true,
            |s| s.leave_discord_voice(),
        ));

    cap = apply_glass(cap, ExoGlass::Bar);
    Some(cap)
}

pub fn person_menu_layers(s: &AppState) -> Vec<El> {
    let Some(ref menu) = s.island.person_menu else {
        return Vec::new();
    };
    let uid = menu.user_id.clone();
    let user_audio = s
        .discord
        .voice_ui
        .user_mix
        .get(&menu.user_id)
        .cloned()
        .unwrap_or_default();
    let vol_frac = (user_audio.volume / 200.0).clamp(0.0, 1.0);
    let vol_pct = user_audio.volume.round() as i32;

    let mut body = col()
        .w_full()
        .px(10.0)
        .py(10.0)
        .gap(8.0)
        .child(text(menu.name.clone()).font(type_section()).color(FG));

    if !menu.is_me {
        let uid_mute = uid.clone();
        let next_muted = !user_audio.muted;
        let uid_vol = uid.clone();
        let mute_row = row()
            .key("island-person-mute")
            .role("menuitem")
            .label(if user_audio.muted { "Unmute" } else { "Mute" })
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .w_full()
            .h(32.0)
            .px(8.0)
            .center_y()
            .justify_between()
            .radius(RADIUS_SMALL)
            .hover(hover_fill())
            .on_press(move |s: &mut AppState| {
                s.set_discord_user_mix(&uid_mute, None, Some(next_muted), None);
            })
            .child(
                text(if user_audio.muted {
                    "Unmute user"
                } else {
                    "Mute user"
                })
                .font(type_body())
                .color(if user_audio.muted { bad() } else { FG }),
            );

        let vol_slider = slider_track(
            "island-person-vol",
            "User volume",
            176.0,
            vol_frac,
            s.island.dragging_user_volume,
            false,
        )
        .on_drag(move |s: &mut AppState, d, _| {
            let frac = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
            let vol = frac * 200.0;
            match d.phase {
                Phase::Down | Phase::Move => {
                    s.island.dragging_user_volume = true;
                    s.set_discord_user_mix(&uid_vol, Some(vol), None, None);
                }
                Phase::Up => {
                    s.island.dragging_user_volume = false;
                    s.set_discord_user_mix(&uid_vol, Some(vol), None, None);
                }
            }
        });
        let uid_keys = uid.clone();
        let vol_slider = slider_range(
            vol_slider,
            f64::from(user_audio.volume.clamp(0.0, 200.0)),
            0.0,
            200.0,
            5.0,
            true,
            move |s: &mut AppState, v| {
                s.set_discord_user_mix(&uid_keys, Some(v as f32), None, None);
            },
        );

        body = body
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(
                col()
                    .w_full()
                    .gap(4.0)
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .child(text("User volume").font(type_small()).color(fg_muted()))
                            .child(
                                text(format!("{vol_pct}%"))
                                    .font(type_small())
                                    .color(fg_muted()),
                            ),
                    )
                    .child(vol_slider),
            )
            .child(mute_row);
    }

    let pt_key = format!(
        "island-person-pt-{}-{}",
        menu.x.round() as i32,
        menu.y.round() as i32
    );
    let card = apply_glass(
        col()
            .key("island-person-menu")
            .w(204.0)
            .radius(RADIUS_CARD)
            .continuous_corners()
            .clip_rounded(RADIUS_CARD)
            .hit()
            .cursor(Cursor::Default),
        ExoGlass::Menu,
    )
    .role("menu")
    .label(menu.name.clone())
    .focus_group()
    .anchored_fit(pt_key.clone(), rune::Side::Bottom, rune::Align::Start, 4.0)
    .transition(popover_transition_from(rune::Vec2::new(0.0, 0.0)))
    .child(body);

    let scrim = el()
        .key("island-person-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.01))
        .hit()
        .on_press(|s: &mut AppState| s.island_close_person_menu());

    vec![
        scrim,
        el().key(pt_key).absolute(menu.x, menu.y).size(0.0, 0.0),
        card,
    ]
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::DiscordVoice;

    #[test]
    fn call_duration_formats_like_facetime() {
        assert_eq!(format_call_duration(0), "0:00");
        assert_eq!(format_call_duration(7), "0:07");
        assert_eq!(format_call_duration(247), "4:07");
        assert_eq!(format_call_duration(3753), "1:02:33");
    }

    #[test]
    fn island_shows_outside_discord_during_call_and_hides_in_discord() {
        let mut s = AppState::default();
        s.room = Room::Library;
        assert!(capsule(&s, 1.0).is_none());

        s.apply_host_event(HostEvent::DiscordVoice(DiscordVoice {
            state: "connected".into(),
            guild_id: Some("g1".into()),
            channel_id: Some("c1".into()),
            muted: false,
            deafened: false,
            ..Default::default()
        }));
        assert!(s.island_in_call());
        assert!(capsule(&s, 1.0).is_some());

        s.room = Room::Discord;
        assert!(capsule(&s, 1.0).is_none());
    }

    #[test]
    fn incoming_call_event_shows_ring_and_clears_on_stop() {
        let mut s = AppState::default();
        s.apply_host_event(HostEvent::Raw {
            event: "discord.call".into(),
            data: json!({ "channelId": "dm-42", "ringing": true, "caller": "Sam" }),
        });
        assert_eq!(s.island.ringing_caller.as_deref(), Some("Sam"));
        assert!(capsule(&s, 1.0).is_some());

        s.apply_host_event(HostEvent::Raw {
            event: "discord.call".into(),
            data: json!({ "channelId": "dm-42", "ringing": false }),
        });
        assert!(s.island.ringing_channel_id.is_none());
        assert!(capsule(&s, 1.0).is_none());
    }
}
