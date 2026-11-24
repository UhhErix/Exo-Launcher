use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

pub const RING_SLOTS: usize = 64;
pub const CORRELATION_WINDOW_US: u64 = 2_000;

struct Slot {
    seq: AtomicU64,
    timestamp_us: AtomicU64,
    packed_event: AtomicU64,
    device_handle: AtomicUsize,
}

impl Slot {
    const fn empty() -> Self {
        Self {
            seq: AtomicU64::new(0),
            timestamp_us: AtomicU64::new(0),
            packed_event: AtomicU64::new(0),
            device_handle: AtomicUsize::new(0),
        }
    }
}

pub struct HookCorrelator {
    slots: [Slot; RING_SLOTS],
    write_idx: AtomicUsize,
}

impl Default for HookCorrelator {
    fn default() -> Self {
        Self::new()
    }
}

impl HookCorrelator {
    pub const fn new() -> Self {
        Self {
            slots: [const { Slot::empty() }; RING_SLOTS],
            write_idx: AtomicUsize::new(0),
        }
    }

    pub fn pack_event(is_mouse: bool, code: u16, is_down: bool) -> u64 {
        (u64::from(is_mouse) << 24) | (u64::from(code) << 8) | u64::from(is_down)
    }

    pub fn record_raw_input(&self, timestamp_us: u64, packed_event: u64, device_handle: usize) {
        let idx = self.write_idx.fetch_add(1, Ordering::AcqRel);
        let slot = &self.slots[idx % RING_SLOTS];
        let next_seq = (idx as u64).wrapping_add(1);
        slot.timestamp_us.store(timestamp_us, Ordering::Relaxed);
        slot.packed_event.store(packed_event, Ordering::Relaxed);
        slot.device_handle.store(device_handle, Ordering::Relaxed);
        slot.seq.store(next_seq, Ordering::Release);
    }

    pub fn correlate(&self, hook_timestamp_us: u64, packed_event: u64) -> Option<usize> {
        let mut best: Option<(u64, usize)> = None;
        for slot in &self.slots {
            let seq = slot.seq.load(Ordering::Acquire);
            if seq == 0 {
                continue;
            }
            if slot.packed_event.load(Ordering::Relaxed) != packed_event {
                continue;
            }
            let ts = slot.timestamp_us.load(Ordering::Relaxed);
            let delta = hook_timestamp_us.abs_diff(ts);
            if delta <= CORRELATION_WINDOW_US {
                let handle = slot.device_handle.load(Ordering::Relaxed);
                if best.is_none_or(|(best_delta, _)| delta < best_delta) {
                    best = Some((delta, handle));
                }
            }
        }
        best.map(|(_, h)| h)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn correlates_ll_hook_with_raw_input_within_2ms_window() {
        let corr = HookCorrelator::new();
        let ev = HookCorrelator::pack_event(false, 0x12, true);
        corr.record_raw_input(10_000, ev, 0xBEEF);
        corr.record_raw_input(10_500, ev, 0xCAFE);

        assert_eq!(corr.correlate(10_600, ev), Some(0xCAFE));
        assert_eq!(corr.correlate(15_000, ev), None);
    }
}
