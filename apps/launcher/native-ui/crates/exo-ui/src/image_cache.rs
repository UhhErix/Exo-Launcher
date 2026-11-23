use rune::{Image, ImageCache};
use std::collections::HashMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CacheRoom {
    Global,
    Library,
    Store,
    Discord,
    Music,
    Pc,
}

pub fn scale_bucket(scale: f32) -> u8 {
    if !scale.is_finite() || scale <= 1.1 {
        0
    } else if scale <= 1.6 {
        1
    } else {
        2
    }
}

pub fn budget_for_scale(scale: f32) -> (usize, usize) {
    match scale_bucket(scale) {
        0 => (96 << 20, 320),
        1 => (144 << 20, 384),
        _ => (192 << 20, 448),
    }
}

pub fn bucket_dim(max_dim: f32) -> f32 {
    if max_dim <= 72.0 {
        64.0
    } else if max_dim <= 140.0 {
        128.0
    } else if max_dim <= 400.0 {
        360.0
    } else if max_dim <= 720.0 {
        640.0
    } else {
        1440.0
    }
}

fn estimate_pending_bytes(dim_pt: f32, scale: f32) -> usize {
    let side = (dim_pt * scale.clamp(1.0, 2.5)).ceil().clamp(32.0, 2048.0) as usize;
    side.saturating_mul(side).saturating_mul(16) / 3
}

struct CacheEntry {
    image: Option<Image>,
    room: CacheRoom,
    last_used: u64,
    bytes: usize,
    pending_est: usize,
}

pub struct ImageLruCache {
    entries: HashMap<String, CacheEntry>,
    total_bytes: usize,
    max_bytes: usize,
    max_entries: usize,
    tick: u64,
}

impl Default for ImageLruCache {
    fn default() -> Self {
        let (max_bytes, max_entries) = budget_for_scale(1.0);
        Self::new(max_bytes, max_entries)
    }
}

impl ImageLruCache {
    pub fn new(max_bytes: usize, max_entries: usize) -> Self {
        Self {
            entries: HashMap::new(),
            total_bytes: 0,
            max_bytes: max_bytes.max(1 << 20),
            max_entries: max_entries.max(8),
            tick: 0,
        }
    }

    pub fn set_budget(&mut self, max_bytes: usize, max_entries: usize) {
        self.max_bytes = max_bytes.max(1 << 20);
        self.max_entries = max_entries.max(8);
        self.evict_to_budget(None);
    }

    pub fn set_budget_for_scale(&mut self, scale: f32) -> usize {
        let (max_bytes, max_entries) = budget_for_scale(scale);
        self.set_budget(max_bytes, max_entries);
        max_bytes
    }

    pub fn max_bytes(&self) -> usize {
        self.max_bytes
    }

    #[cfg(test)]
    pub fn max_entries(&self) -> usize {
        self.max_entries
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[cfg(test)]
    pub fn total_bytes(&self) -> usize {
        self.total_bytes
    }

    #[cfg(test)]
    pub fn room_count(&self, room: CacheRoom) -> usize {
        self.entries.values().filter(|e| e.room == room).count()
    }

    pub fn is_failed(&self, key: &str) -> bool {
        matches!(self.entries.get(key), Some(e) if e.image.is_none())
    }

    fn next_tick(&mut self) -> u64 {
        self.tick = self.tick.wrapping_add(1);
        self.tick
    }

    pub fn get(&mut self, key: &str, cache: Option<&ImageCache>) -> Option<Option<Image>> {
        let tick = self.next_tick();
        let mut needs_evict = false;
        let out = {
            let entry = self.entries.get_mut(key)?;
            entry.last_used = tick;
            if let Some(ref img) = entry.image {
                if let Some(ic) = cache {
                    ic.touch(img);
                }
                let actual = img.decoded_bytes();
                let next_bytes = if actual > 0 {
                    actual
                } else if img.error().is_some() {
                    0
                } else {
                    entry.pending_est
                };
                if next_bytes != entry.bytes {
                    self.total_bytes = self
                        .total_bytes
                        .saturating_sub(entry.bytes)
                        .saturating_add(next_bytes);
                    entry.bytes = next_bytes;
                    needs_evict = self.total_bytes > self.max_bytes;
                }
            }
            entry.image.clone()
        };
        if needs_evict {
            self.evict_to_budget(Some(key));
        }
        Some(out)
    }

    pub fn insert(
        &mut self,
        key: String,
        image: Option<Image>,
        room: CacheRoom,
        dim_pt: f32,
        scale: f32,
    ) {
        let tick = self.next_tick();
        let pending_est = if image.is_some() {
            estimate_pending_bytes(dim_pt, scale)
        } else {
            0
        };
        let bytes = match image.as_ref() {
            Some(img) => {
                let actual = img.decoded_bytes();
                if actual > 0 {
                    actual
                } else if img.error().is_some() {
                    0
                } else {
                    pending_est
                }
            }
            None => 0,
        };
        if let Some(old) = self.entries.insert(
            key.clone(),
            CacheEntry {
                image,
                room,
                last_used: tick,
                bytes,
                pending_est,
            },
        ) {
            self.total_bytes = self.total_bytes.saturating_sub(old.bytes);
        }
        self.total_bytes = self.total_bytes.saturating_add(bytes);
        self.evict_to_budget(Some(&key));
    }

    pub fn refresh_sizes_and_evict(&mut self) {
        let mut total = 0usize;
        for entry in self.entries.values_mut() {
            if let Some(ref img) = entry.image {
                let actual = img.decoded_bytes();
                entry.bytes = if actual > 0 {
                    actual
                } else if img.error().is_some() {
                    0
                } else {
                    entry.pending_est
                };
            } else {
                entry.bytes = 0;
            }
            total = total.saturating_add(entry.bytes);
        }
        self.total_bytes = total;
        self.evict_to_budget(None);
    }

    pub fn evict_room(&mut self, room: CacheRoom) -> usize {
        let before = self.entries.len();
        self.entries.retain(|_, e| e.room != room);
        self.recompute_bytes();
        before.saturating_sub(self.entries.len())
    }

    pub fn clear(&mut self) {
        self.entries.clear();
        self.total_bytes = 0;
    }

    fn recompute_bytes(&mut self) {
        self.total_bytes = self.entries.values().map(|e| e.bytes).sum();
    }

    fn evict_to_budget(&mut self, protect: Option<&str>) {
        while (self.total_bytes > self.max_bytes || self.entries.len() > self.max_entries)
            && !self.entries.is_empty()
        {
            let victim = self
                .entries
                .iter()
                .filter(|(k, e)| {
                    (protect.is_none_or(|p| k.as_str() != p) || self.entries.len() == 1)
                        && (e.room != CacheRoom::Global || self.entries.len() <= 2)
                })
                .min_by_key(|(_, e)| e.last_used)
                .or_else(|| {
                    self.entries
                        .iter()
                        .filter(|(k, _)| {
                            protect.is_none_or(|p| k.as_str() != p) || self.entries.len() == 1
                        })
                        .min_by_key(|(_, e)| e.last_used)
                })
                .map(|(k, _)| k.clone());
            let Some(k) = victim else {
                break;
            };
            if let Some(old) = self.entries.remove(&k) {
                self.total_bytes = self.total_bytes.saturating_sub(old.bytes);
            }
        }
    }
}
