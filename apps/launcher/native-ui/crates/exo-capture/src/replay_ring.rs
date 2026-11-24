use std::collections::VecDeque;
use std::sync::Mutex;

use crate::policy::effective_replay_budget_bytes;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackKind {
    Video,
    AudioGame,
    AudioMic,
    AudioVoice,
}

impl TrackKind {
    pub fn track_index(self) -> usize {
        match self {
            Self::Video => 0,
            Self::AudioGame => 1,
            Self::AudioMic => 2,
            Self::AudioVoice => 3,
        }
    }

    pub fn is_video(self) -> bool {
        matches!(self, Self::Video)
    }
}

#[derive(Debug, Clone)]
pub struct EncodedPacket {
    pub track: TrackKind,
    pub data: Vec<u8>,
    pub timestamp_100ns: i64,
    pub duration_100ns: i64,
    pub is_keyframe: bool,
}

struct RingInner {
    packets: VecDeque<EncodedPacket>,
    bytes: u64,
    seconds: u32,
    max_bytes: u64,
}

pub struct ReplayRingBuffer {
    inner: Mutex<RingInner>,
}

impl ReplayRingBuffer {
    pub fn new(seconds: u32, requested_max_bytes: u64, total_system_ram_bytes: u64) -> Self {
        let cap = effective_replay_budget_bytes(requested_max_bytes, total_system_ram_bytes);
        Self {
            inner: Mutex::new(RingInner {
                packets: VecDeque::with_capacity(1024),
                bytes: 0,
                seconds: seconds.clamp(5, 600),
                max_bytes: cap,
            }),
        }
    }

    pub fn reconfigure(&self, seconds: u32, requested_max_bytes: u64, total_system_ram_bytes: u64) {
        let cap = effective_replay_budget_bytes(requested_max_bytes, total_system_ram_bytes);
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.seconds = seconds.clamp(5, 600);
        inner.max_bytes = cap;
        Self::trim_locked(&mut inner);
    }

    pub fn push(&self, packet: EncodedPacket) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        if inner.max_bytes == 0 {
            return;
        }
        if inner.packets.is_empty() && !(packet.track.is_video() && packet.is_keyframe) {
            return;
        }
        let pkt_len = packet.data.len() as u64;
        if pkt_len > inner.max_bytes {
            return;
        }
        let is_video_key = packet.track.is_video() && packet.is_keyframe;
        inner.bytes = inner.bytes.saturating_add(pkt_len);
        inner.packets.push_back(packet);
        if is_video_key || inner.bytes > inner.max_bytes {
            Self::trim_locked(&mut inner);
        }
    }

    fn trim_locked(inner: &mut RingInner) {
        loop {
            if inner.packets.is_empty() {
                break;
            }
            let Some(next_key_idx) = Self::find_next_video_keyframe(&inner.packets, 1) else {
                if inner.bytes > inner.max_bytes {
                    inner.packets.clear();
                    inner.bytes = 0;
                }
                break;
            };
            let newest_video = Self::newest_video_time(&inner.packets);
            let next_key_time = inner.packets[next_key_idx].timestamp_100ns;
            let span_limit = (inner.seconds as i64) * 10_000_000;
            let too_long = newest_video.saturating_sub(next_key_time) >= span_limit;
            let too_big = inner.bytes > inner.max_bytes;
            if !too_long && !too_big {
                break;
            }
            for _ in 0..next_key_idx {
                if let Some(dropped) = inner.packets.pop_front() {
                    inner.bytes = inner.bytes.saturating_sub(dropped.data.len() as u64);
                }
            }
        }
    }

    fn find_next_video_keyframe(packets: &VecDeque<EncodedPacket>, start: usize) -> Option<usize> {
        for (idx, p) in packets.iter().enumerate().skip(start) {
            if p.track.is_video() && p.is_keyframe {
                return Some(idx);
            }
        }
        None
    }

    fn newest_video_time(packets: &VecDeque<EncodedPacket>) -> i64 {
        for p in packets.iter().rev() {
            if p.track.is_video() {
                return p.timestamp_100ns;
            }
        }
        0
    }

    pub fn bytes(&self) -> u64 {
        self.inner.lock().unwrap_or_else(|e| e.into_inner()).bytes
    }

    pub fn max_bytes(&self) -> u64 {
        self.inner
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .max_bytes
    }

    pub fn retained_ms(&self) -> u64 {
        let inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        let Some(first) = inner.packets.front() else {
            return 0;
        };
        let newest = Self::newest_video_time(&inner.packets);
        newest.saturating_sub(first.timestamp_100ns).max(0) as u64 / 10_000
    }

    pub fn take_last_seconds(&self, seconds: Option<u32>) -> Vec<EncodedPacket> {
        let inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        if inner.packets.is_empty() {
            return Vec::new();
        }
        let mut from = 0usize;
        if let Some(secs) = seconds {
            let newest = Self::newest_video_time(&inner.packets);
            let cutoff = newest.saturating_sub((secs as i64) * 10_000_000);
            for (idx, p) in inner.packets.iter().enumerate() {
                if p.track.is_video() && p.is_keyframe {
                    if p.timestamp_100ns > cutoff {
                        break;
                    }
                    from = idx;
                }
            }
        }
        inner.packets.iter().skip(from).cloned().collect()
    }

    pub fn clear(&self) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.packets.clear();
        inner.bytes = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn video_pkt(ms: i64, key: bool, size: usize) -> EncodedPacket {
        EncodedPacket {
            track: TrackKind::Video,
            data: vec![0xAB; size],
            timestamp_100ns: ms * 10_000,
            duration_100ns: 166_667,
            is_keyframe: key,
        }
    }

    fn audio_pkt(track: TrackKind, ms: i64, size: usize) -> EncodedPacket {
        EncodedPacket {
            track,
            data: vec![0xCD; size],
            timestamp_100ns: ms * 10_000,
            duration_100ns: 213_333,
            is_keyframe: true,
        }
    }

    #[test]
    fn discards_leading_non_keyframes_and_evicts_whole_gops() {
        let ring = ReplayRingBuffer::new(5, 64 * 1024 * 1024, 16 * 1024 * 1024 * 1024);
        ring.push(video_pkt(0, false, 1024));
        ring.push(audio_pkt(TrackKind::AudioGame, 20, 256));
        assert_eq!(ring.bytes(), 0);

        for sec in 0..8 {
            ring.push(video_pkt(sec * 1000, true, 4096));
            ring.push(video_pkt(sec * 1000 + 500, false, 1024));
            ring.push(audio_pkt(TrackKind::AudioGame, sec * 1000 + 250, 256));
        }

        let taken = ring.take_last_seconds(None);
        assert!(!taken.is_empty());
        assert!(taken[0].track.is_video() && taken[0].is_keyframe);
        assert!(ring.retained_ms() >= 5_000);
        assert!(ring.retained_ms() <= 6_500);
    }

    #[test]
    fn enforces_memory_budget_cap_by_dropping_oldest_gops() {
        let ram = 1024 * 1024 * 1024u64;
        let ring = ReplayRingBuffer::new(300, 512 * 1024 * 1024, ram);
        let expected_cap = (ram * 15) / 100;
        assert_eq!(ring.max_bytes(), expected_cap);

        let chunk = 16 * 1024 * 1024usize;
        for sec in 0..16 {
            ring.push(video_pkt(sec * 1000, true, chunk));
        }
        assert!(ring.bytes() <= expected_cap);
        let taken = ring.take_last_seconds(None);
        assert!(!taken.is_empty());
        assert!(taken[0].is_keyframe);
    }
}
