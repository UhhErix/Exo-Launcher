use crate::discord_types::DiscordStatus;
use crate::events::ShellBootstrap;
use crate::game_types::DriveHeat;
use crate::media_types::MusicPlaybackReply;
use crate::pc_types::AutopilotLive;
use crate::types::{
    ArtBase, Game, GameExtras, GameGetReply, GameInclude, GameVariant, InstallProgress,
    LauncherSettings, LibraryPayload,
};
use exo_wire::{WireError, WireReader, WireWriter};

pub fn encode_game_id_param(id: &str) -> Vec<u8> {
    let mut w = WireWriter::with_capacity(4 + id.len());
    w.put_str(id);
    w.into_vec()
}

pub fn encode_library_flags_param(force: bool, full: bool) -> [u8; 1] {
    let mut flags = 0u8;
    if force {
        flags |= 1 << 0;
    }
    if full {
        flags |= 1 << 1;
    }
    [flags]
}

pub fn encode_cancel_param(target_req_id: u64) -> [u8; 8] {
    target_req_id.to_le_bytes()
}

pub fn decode_install_progress(r: &mut WireReader<'_>) -> Result<InstallProgress, WireError> {
    let game_id = r.get_opt_string()?;
    let phase = r.get_string()?;
    let percent = r.get_f64()?;
    let bytes_downloaded = r.get_u64()?;
    let bytes_to_download = r.get_u64()?;
    let bytes_per_second = r.get_u64()?;
    let status = r.get_opt_string()?;
    let flags = r.get_u8()?;
    Ok(InstallProgress {
        game_id,
        phase,
        percent,
        bytes_downloaded,
        bytes_to_download,
        bytes_per_second,
        status,
        can_cancel: (flags & (1 << 0)) != 0,
        is_active: (flags & (1 << 1)) != 0,
    })
}

pub fn decode_install_progress_frame(bytes: &[u8]) -> Result<InstallProgress, WireError> {
    let mut r = WireReader::new(bytes);
    let val = decode_install_progress(&mut r)?;
    r.finish()?;
    Ok(val)
}

fn parse_anti_cheat_class(raw: Option<&str>) -> crate::anticheat::AntiCheatClass {
    match raw {
        Some("competitivePvP" | "competitivePvpOnline") => {
            crate::anticheat::AntiCheatClass::CompetitivePvpOnline
        }
        Some("onlinePvE" | "onlineNonCompetitivePve") => {
            crate::anticheat::AntiCheatClass::OnlineNonCompetitivePve
        }
        Some("offline" | "noAntiCheat" | "offlineAntiCheat") => {
            crate::anticheat::AntiCheatClass::OfflineAntiCheat
        }
        _ => crate::anticheat::AntiCheatClass::Unknown,
    }
}

pub fn decode_game(r: &mut WireReader<'_>) -> Result<Game, WireError> {
    let id = r.get_string()?;
    let title = r.get_string()?;
    let store = r.get_string()?;
    let stores = r.get_string_vec()?;
    let flags = r.get_u16()?;
    let entitlement_state = r.get_opt_string()?;
    let primary_action = r.get_opt_string()?;
    let canonical_title_key = r.get_opt_string()?;
    let selected_variant_id = r.get_opt_string()?;
    let buy_url = r.get_opt_string()?;
    let cover_url = r.get_opt_string()?;
    let cover_source = r.get_opt_string()?;
    let hero_url = r.get_opt_string()?;
    let launch_target = r.get_opt_string()?;
    let art_revision = Some(r.get_u64()?);
    let playtime_minutes = r.get_u64()?;
    let size_bytes = r.get_opt_u64()?;
    let status = r.get_opt_string()?;
    let launch_note = r.get_opt_string()?;
    let shared_by = r.get_opt_string()?;
    let last_played_utc = r.get_opt_string()?;
    let tags = r.get_string_vec()?;
    let anti_cheat_class = parse_anti_cheat_class(r.get_opt_string()?.as_deref());

    let includes_len = r.get_u32()? as usize;
    if includes_len > r.remaining() / 2 {
        return Err(WireError::UnexpectedEof);
    }
    let mut includes = Vec::with_capacity(includes_len.min(256));
    for _ in 0..includes_len {
        includes.push(GameInclude {
            app_id: r.get_opt_string()?,
            title: r.get_opt_string()?,
        });
    }

    let variants_len = r.get_u32()? as usize;
    if variants_len > r.remaining() / 16 {
        return Err(WireError::UnexpectedEof);
    }
    let mut variants = Vec::with_capacity(variants_len.min(64));
    for _ in 0..variants_len {
        let v_id = r.get_string()?;
        let v_store = r.get_string()?;
        let v_flags = r.get_u8()?;
        let v_entitlement = r.get_opt_string()?;
        let v_primary = r.get_opt_string()?;
        let v_target = r.get_opt_string()?;
        let v_playtime = Some(r.get_u64()?);
        let v_last_played = r.get_opt_string()?;
        let v_status = r.get_string()?;
        variants.push(GameVariant {
            id: v_id,
            store: v_store,
            installed: (v_flags & (1 << 0)) != 0,
            owned: (v_flags & (1 << 1)) != 0,
            entitlement_state: v_entitlement,
            update_available: (v_flags & (1 << 2)) != 0,
            can_install: (v_flags & (1 << 3)) != 0,
            primary_action: v_primary,
            launch_target: v_target,
            playtime_minutes: v_playtime,
            last_played_utc: v_last_played,
            status: v_status,
            is_running: (v_flags & (1 << 4)) != 0,
            can_stop: (v_flags & (1 << 5)) != 0,
        });
    }

    Ok(Game {
        id,
        title,
        store,
        stores,
        installed: (flags & (1 << 0)) != 0,
        owned: (flags & (1 << 1)) != 0,
        entitlement_state,
        update_available: (flags & (1 << 2)) != 0,
        can_install: (flags & (1 << 3)) != 0,
        primary_action,
        canonical_title_key,
        selected_variant_id,
        buy_url,
        cover_url,
        cover_source,
        hero_url,
        launch_target,
        includes,
        anti_cheat_class,
        variants,
        art_revision,
        playtime_minutes,
        size_bytes,
        status,
        launch_note,
        family_shared: (flags & (1 << 4)) != 0,
        shared_by,
        last_played_utc,
        is_running: (flags & (1 << 5)) != 0,
        can_stop: (flags & (1 << 6)) != 0,
        is_add_portable: (flags & (1 << 7)) != 0,
        tags,
    })
}

pub fn decode_library_payload_from(r: &mut WireReader<'_>) -> Result<LibraryPayload, WireError> {
    let count = r.get_u32()? as usize;
    let sort_mode = r.get_opt_string()?;
    let recent = r.get_string_vec()?;
    let progress = if r.get_bool()? {
        Some(decode_install_progress(r)?)
    } else {
        None
    };
    let games_len = r.get_u32()? as usize;
    if games_len > r.remaining() / 32 {
        return Err(WireError::UnexpectedEof);
    }
    let mut games = Vec::with_capacity(games_len.min(16384));
    for _ in 0..games_len {
        games.push(decode_game(r)?);
    }
    Ok(LibraryPayload {
        games,
        count,
        sort_mode,
        recent,
        progress,
    })
}

pub fn decode_library_payload(bytes: &[u8]) -> Result<LibraryPayload, WireError> {
    let mut r = WireReader::new(bytes);
    let val = decode_library_payload_from(&mut r)?;
    r.finish()?;
    Ok(val)
}

pub fn decode_game_get_reply(bytes: &[u8]) -> Result<GameGetReply, WireError> {
    let mut r = WireReader::new(bytes);
    let ok = r.get_bool()?;
    let game = if r.get_bool()? {
        Some(decode_game(&mut r)?)
    } else {
        None
    };
    let message = r.get_opt_string()?;
    r.finish()?;
    Ok(GameGetReply { ok, game, message })
}

pub fn decode_game_extras(bytes: &[u8]) -> Result<GameExtras, WireError> {
    let mut r = WireReader::new(bytes);
    let ok = r.get_bool()?;
    let achievements_unlocked = r.get_opt_u32()?;
    let achievements_total = r.get_opt_u32()?;
    let drive_heat = if r.get_bool()? {
        Some(DriveHeat {
            peak: r.get_f64()?,
            warn: r.get_opt_f64()?,
            slowed_seconds: r.get_f64()?,
        })
    } else {
        None
    };
    let message = r.get_opt_string()?;
    r.finish()?;
    Ok(GameExtras {
        ok,
        achievements_unlocked,
        achievements_total,
        drive_heat,
        message,
    })
}

pub fn decode_autopilot_live(bytes: &[u8]) -> Result<Option<AutopilotLive>, WireError> {
    let mut r = WireReader::new(bytes);
    if !r.get_bool()? {
        r.finish()?;
        return Ok(None);
    }
    let game = r.get_string()?;
    let title = r.get_string()?;
    let seconds = r.get_f64()?;
    let fps = r.get_f64()?;
    let low1 = r.get_f64()?;
    let low01 = r.get_f64()?;
    let stutters = r.get_i32()?;
    let mask = r.get_u16()?;

    let cpu = if (mask & (1 << 0)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let clock = if (mask & (1 << 1)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let gpu = if (mask & (1 << 2)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let gpu_temp = if (mask & (1 << 3)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let vram_gb = if (mask & (1 << 4)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let vram_total_gb = if (mask & (1 << 5)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let ram_gb = if (mask & (1 << 6)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let ram_total_gb = if (mask & (1 << 7)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let standby_gb = if (mask & (1 << 8)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let free_gb = if (mask & (1 << 9)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    let dpc = if (mask & (1 << 10)) != 0 {
        Some(r.get_f64()?)
    } else {
        None
    };
    r.finish()?;

    Ok(Some(AutopilotLive {
        game,
        title,
        seconds,
        fps,
        low1,
        low01,
        stutters,
        cpu,
        clock,
        gpu,
        gpu_temp,
        vram_gb,
        vram_total_gb,
        ram_gb,
        ram_total_gb,
        standby_gb,
        free_gb,
        dpc,
    }))
}

pub fn decode_shell_bootstrap(bytes: &[u8]) -> Result<ShellBootstrap, crate::RpcError> {
    let mut r = WireReader::new(bytes);
    let ok = r.get_bool().map_err(crate::RpcError::Wire)?;
    let binary_media_pipe = r.get_opt_string().map_err(crate::RpcError::Wire)?;
    let library = decode_library_payload_from(&mut r).map_err(crate::RpcError::Wire)?;
    let art_base_bytes = r.get_bytes().map_err(crate::RpcError::Wire)?;
    let settings_bytes = r.get_bytes().map_err(crate::RpcError::Wire)?;
    let music_bytes = r.get_bytes().map_err(crate::RpcError::Wire)?;
    let discord_bytes = r.get_bytes().map_err(crate::RpcError::Wire)?;
    r.finish().map_err(crate::RpcError::Wire)?;

    let art_base: ArtBase = serde_json::from_slice(art_base_bytes)?;
    let settings: LauncherSettings = serde_json::from_slice(settings_bytes)?;
    let music_playback: MusicPlaybackReply = serde_json::from_slice(music_bytes)?;
    let discord_status: DiscordStatus = serde_json::from_slice(discord_bytes)?;

    Ok(ShellBootstrap {
        ok,
        art_base,
        settings,
        library,
        music_playback,
        discord_status,
        binary_media_pipe,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_binary_autopilot_live_present_and_none() {
        let mut none_w = WireWriter::with_capacity(4);
        none_w.put_bool(false);
        assert!(decode_autopilot_live(none_w.as_slice()).unwrap().is_none());

        let mut w = WireWriter::with_capacity(128);
        w.put_bool(true);
        w.put_str("steam:870780");
        w.put_str("Control");
        w.put_f64(120.0);
        w.put_f64(144.5);
        w.put_f64(110.2);
        w.put_f64(95.0);
        w.put_i32(2);
        let mask: u16 = (1 << 0) | (1 << 2) | (1 << 3) | (1 << 10);
        w.put_u16(mask);
        w.put_f64(42.0);
        w.put_f64(98.0);
        w.put_f64(67.0);
        w.put_f64(0.4);

        let live = decode_autopilot_live(w.as_slice()).unwrap().unwrap();
        assert_eq!(live.game, "steam:870780");
        assert_eq!(live.title, "Control");
        assert_eq!(live.fps, 144.5);
        assert_eq!(live.stutters, 2);
        assert_eq!(live.cpu, Some(42.0));
        assert_eq!(live.clock, None);
        assert_eq!(live.gpu, Some(98.0));
        assert_eq!(live.gpu_temp, Some(67.0));
        assert_eq!(live.dpc, Some(0.4));
    }

    #[test]
    fn decodes_binary_game_extras_and_install_progress() {
        let mut w = WireWriter::with_capacity(64);
        w.put_bool(true);
        w.put_opt_u32(Some(14));
        w.put_opt_u32(Some(40));
        w.put_bool(true);
        w.put_f64(71.0);
        w.put_opt_f64(Some(75.0));
        w.put_f64(12.0);
        w.put_opt_str(None);

        let extras = decode_game_extras(w.as_slice()).unwrap();
        assert!(extras.ok);
        assert_eq!(extras.achievements_unlocked, Some(14));
        assert_eq!(extras.achievements_total, Some(40));
        let heat = extras.drive_heat.unwrap();
        assert_eq!(heat.peak, 71.0);
        assert_eq!(heat.warn, Some(75.0));
        assert_eq!(heat.slowed_seconds, 12.0);

        let mut p = WireWriter::with_capacity(64);
        p.put_opt_str(Some("steam:10"));
        p.put_str("downloading");
        p.put_f64(48.5);
        p.put_u64(500_000);
        p.put_u64(1_000_000);
        p.put_u64(50_000);
        p.put_opt_str(Some("Downloading"));
        p.put_u8(0b11);
        let prog = decode_install_progress_frame(p.as_slice()).unwrap();
        assert_eq!(prog.game_id.as_deref(), Some("steam:10"));
        assert_eq!(prog.phase, "downloading");
        assert!(prog.can_cancel);
        assert!(prog.is_active);
    }

    #[test]
    fn decodes_binary_library_and_game_get_reply() {
        let mut g = WireWriter::with_capacity(256);
        g.put_str("steam:870780");
        g.put_str("Control Resonant");
        g.put_str("steam");
        g.put_u32(1);
        g.put_str("steam");
        g.put_u16((1 << 0) | (1 << 1));
        g.put_opt_str(Some("owned"));
        g.put_opt_str(Some("play"));
        g.put_opt_str(Some("control resonant"));
        g.put_opt_str(Some("steam:870780"));
        g.put_opt_str(None);
        g.put_opt_str(Some("https://covers.exo-launcher.local/steam_870780.jpg"));
        g.put_opt_str(Some("steam"));
        g.put_opt_str(None);
        g.put_opt_str(Some("870780"));
        g.put_u64(3);
        g.put_u64(240);
        g.put_opt_u64(Some(42_000_000_000));
        g.put_opt_str(Some("Ready"));
        g.put_opt_str(None);
        g.put_opt_str(None);
        g.put_opt_str(Some("2026-11-24T00:00:00Z"));
        g.put_u32(2);
        g.put_str("Action");
        g.put_str("Singleplayer");
        g.put_opt_str(Some("offline"));
        g.put_u32(0);
        g.put_u32(0);

        let mut lib = WireWriter::with_capacity(512);
        lib.put_u32(1);
        lib.put_opt_str(Some("recent"));
        lib.put_u32(1);
        lib.put_str("steam:870780");
        lib.put_bool(false);
        lib.put_u32(1);
        lib.put_raw(g.as_slice());

        let parsed = decode_library_payload(lib.as_slice()).unwrap();
        assert_eq!(parsed.count, 1);
        assert_eq!(parsed.games.len(), 1);
        assert_eq!(parsed.games[0].id, "steam:870780");
        assert_eq!(parsed.games[0].title, "Control Resonant");
        assert!(parsed.games[0].installed);
        assert_eq!(parsed.games[0].playtime_minutes, 240);
        assert_eq!(parsed.games[0].tags, vec!["Action", "Singleplayer"]);
        assert_eq!(
            parsed.games[0].anti_cheat_class,
            crate::anticheat::AntiCheatClass::OfflineAntiCheat
        );

        let mut reply_w = WireWriter::with_capacity(512);
        reply_w.put_bool(true);
        reply_w.put_bool(true);
        reply_w.put_raw(g.as_slice());
        reply_w.put_opt_str(None);
        let reply = decode_game_get_reply(reply_w.as_slice()).unwrap();
        assert!(reply.ok);
        assert_eq!(reply.game.unwrap().title, "Control Resonant");
    }
}
