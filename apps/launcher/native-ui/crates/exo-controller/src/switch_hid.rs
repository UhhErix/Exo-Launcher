pub const NINTENDO_VENDOR: u16 = 0x057E;
pub const SWITCH_PRO_PID: u16 = 0x2009;
pub const JOYCON_L_PID: u16 = 0x2006;
pub const JOYCON_R_PID: u16 = 0x2007;

pub const SUBCMD_SET_PLAYER_LEDS: u8 = 0x30;
pub const SUBCMD_ENABLE_IMU: u8 = 0x40;
pub const SUBCMD_ENABLE_VIBRATION: u8 = 0x48;

const NEUTRAL_RUMBLE: [u8; 8] = [0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40];

pub fn build_subcommand_packet(packet_num: u8, subcmd: u8, arg: &[u8]) -> [u8; 49] {
    let mut pkt = [0u8; 49];
    pkt[0] = 0x01;
    pkt[1] = packet_num & 0x0F;
    pkt[2..10].copy_from_slice(&NEUTRAL_RUMBLE);
    pkt[10] = subcmd;
    let len = arg.len().min(38);
    pkt[11..11 + len].copy_from_slice(&arg[..len]);
    pkt
}

pub fn build_enable_imu_packet(packet_num: u8, enable: bool) -> [u8; 49] {
    build_subcommand_packet(packet_num, SUBCMD_ENABLE_IMU, &[u8::from(enable)])
}

pub fn build_player_led_packet(packet_num: u8, mask: u8) -> [u8; 49] {
    build_subcommand_packet(packet_num, SUBCMD_SET_PLAYER_LEDS, &[mask & 0x0F])
}

pub fn decode_switch_battery_pct(status_byte: u8) -> (u8, bool) {
    let charging = (status_byte & 0x10) != 0;
    let pct = match status_byte >> 5 {
        0 => 5,
        1 => 25,
        2 => 50,
        3 => 75,
        _ => 100,
    };
    (pct, charging)
}
