use crate::pc_ai;
use crate::pc_apps;
use crate::pc_devices;
use crate::state::{AppState, Room};
use rune::El;

pub fn view(s: &AppState, scale: f32) -> El {
    match s.room {
        Room::Devices => pc_devices::devices_room(s, scale),
        Room::Apps => pc_apps::apps_room(s),
        _ => pc_ai::exo_ai_room(s, scale),
    }
}

pub fn part_menu_layers(s: &AppState) -> Vec<El> {
    pc_ai::part_menu_layers(s)
}
