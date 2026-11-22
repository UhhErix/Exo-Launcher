use rune::{Color, El, Shape, canvas, el};

pub struct QrMatrix {
    pub size: usize,
    pub modules: Vec<bool>,
}

impl QrMatrix {
    pub fn get(&self, x: usize, y: usize) -> bool {
        self.modules[y * self.size + x]
    }
}

struct VersionSpec {
    version: usize,
    data_bytes: usize,
    ec_per_block: usize,
    blocks: usize,
}

const SPECS: &[VersionSpec] = &[
    VersionSpec {
        version: 1,
        data_bytes: 19,
        ec_per_block: 7,
        blocks: 1,
    },
    VersionSpec {
        version: 2,
        data_bytes: 34,
        ec_per_block: 10,
        blocks: 1,
    },
    VersionSpec {
        version: 3,
        data_bytes: 55,
        ec_per_block: 15,
        blocks: 1,
    },
    VersionSpec {
        version: 4,
        data_bytes: 80,
        ec_per_block: 20,
        blocks: 1,
    },
    VersionSpec {
        version: 5,
        data_bytes: 108,
        ec_per_block: 26,
        blocks: 1,
    },
    VersionSpec {
        version: 6,
        data_bytes: 136,
        ec_per_block: 18,
        blocks: 2,
    },
];

pub fn encode_qr(text: &str) -> Option<QrMatrix> {
    let bytes = text.as_bytes();
    let spec = SPECS.iter().find(|s| bytes.len() + 2 <= s.data_bytes)?;
    let size = spec.version * 4 + 17;

    let mut bits = Vec::with_capacity(spec.data_bytes * 8);
    push_bits(&mut bits, 0b0100, 4);
    push_bits(&mut bits, bytes.len() as u32, 8);
    for &b in bytes {
        push_bits(&mut bits, b as u32, 8);
    }
    let cap_bits = spec.data_bytes * 8;
    let term = (cap_bits - bits.len()).min(4);
    push_bits(&mut bits, 0, term);
    while bits.len() % 8 != 0 {
        bits.push(false);
    }

    let mut data = Vec::with_capacity(spec.data_bytes);
    for chunk in bits.chunks(8) {
        let mut byte = 0u8;
        for &bit in chunk {
            byte = (byte << 1) | u8::from(bit);
        }
        data.push(byte);
    }
    let mut pad_idx = 0usize;
    while data.len() < spec.data_bytes {
        data.push(if pad_idx.is_multiple_of(2) {
            0xEC
        } else {
            0x11
        });
        pad_idx += 1;
    }

    let block_data_len = spec.data_bytes / spec.blocks;
    let mut data_blocks = Vec::with_capacity(spec.blocks);
    let mut ec_blocks = Vec::with_capacity(spec.blocks);
    for b in 0..spec.blocks {
        let slice = &data[b * block_data_len..(b + 1) * block_data_len];
        data_blocks.push(slice);
        ec_blocks.push(rs_remainder(slice, spec.ec_per_block));
    }

    let mut codewords = Vec::with_capacity(spec.data_bytes + spec.ec_per_block * spec.blocks);
    for i in 0..block_data_len {
        for blk in &data_blocks {
            codewords.push(blk[i]);
        }
    }
    for i in 0..spec.ec_per_block {
        for blk in &ec_blocks {
            codewords.push(blk[i]);
        }
    }

    let mut modules = vec![false; size * size];
    let mut function = vec![false; size * size];

    place_finder(&mut modules, &mut function, size, 0, 0);
    place_finder(&mut modules, &mut function, size, size - 7, 0);
    place_finder(&mut modules, &mut function, size, 0, size - 7);

    if spec.version >= 2 {
        place_alignment(&mut modules, &mut function, size, size - 7, size - 7);
    }

    for i in 0..size {
        if !function[6 * size + i] {
            modules[6 * size + i] = i % 2 == 0;
            function[6 * size + i] = true;
        }
        if !function[i * size + 6] {
            modules[i * size + 6] = i % 2 == 0;
            function[i * size + 6] = true;
        }
    }

    modules[(size - 8) * size + 8] = true;
    function[(size - 8) * size + 8] = true;

    reserve_format(&mut function, size);
    place_data(&mut modules, &function, size, &codewords);

    let mut best_mask = 0u8;
    let mut best_score = u32::MAX;
    let mut best_grid = modules.clone();

    for mask in 0..8u8 {
        let mut candidate = modules.clone();
        apply_mask(&mut candidate, &function, size, mask);
        write_format(&mut candidate, size, mask);
        let score = penalty_score(&candidate, size);
        if score < best_score {
            best_score = score;
            best_mask = mask;
            best_grid = candidate;
        }
    }
    let _ = best_mask;

    Some(QrMatrix {
        size,
        modules: best_grid,
    })
}

pub fn qr_code_view(data: &str, size: f32) -> El {
    let mut shape = Shape::new();
    if let Some(qr) = encode_qr(data) {
        let quiet = 3usize;
        let total = (qr.size + quiet * 2) as f32;
        shape = shape.view_box(total, total);
        for y in 0..qr.size {
            let mut x = 0usize;
            while x < qr.size {
                if qr.get(x, y) {
                    let start = x;
                    while x < qr.size && qr.get(x, y) {
                        x += 1;
                    }
                    let x0 = (start + quiet) as f32;
                    let x1 = (x + quiet) as f32;
                    let y0 = (y + quiet) as f32;
                    let y1 = y0 + 1.0;
                    shape = shape.fill(vec![[x0, y0], [x1, y0], [x1, y1], [x0, y1]], Color::BLACK);
                } else {
                    x += 1;
                }
            }
        }
    }
    el().size(size, size)
        .radius(14.0)
        .clip_rounded(14.0)
        .fill(Color::WHITE)
        .child(canvas(shape).size(size, size))
}

fn push_bits(out: &mut Vec<bool>, val: u32, count: usize) {
    for i in (0..count).rev() {
        out.push(((val >> i) & 1) != 0);
    }
}

fn gf_mul(mut a: u8, mut b: u8) -> u8 {
    let mut r = 0u8;
    for _ in 0..8 {
        if (b & 1) != 0 {
            r ^= a;
        }
        let hi = (a & 0x80) != 0;
        a <<= 1;
        if hi {
            a ^= 0x1D;
        }
        b >>= 1;
    }
    r
}

fn rs_remainder(data: &[u8], ec_len: usize) -> Vec<u8> {
    let mut poly = vec![0u8; ec_len];
    poly[ec_len - 1] = 1;
    let mut root = 1u8;
    for _ in 0..ec_len {
        for j in 0..ec_len {
            poly[j] = gf_mul(poly[j], root);
            if j + 1 < ec_len {
                poly[j] ^= poly[j + 1];
            }
        }
        root = gf_mul(root, 2);
    }
    let mut rem = vec![0u8; ec_len];
    for &b in data {
        let factor = b ^ rem[0];
        rem.rotate_left(1);
        rem[ec_len - 1] = 0;
        for j in 0..ec_len {
            rem[j] ^= gf_mul(poly[j], factor);
        }
    }
    rem
}

fn place_finder(modules: &mut [bool], function: &mut [bool], size: usize, x0: usize, y0: usize) {
    for dy in -1i32..=7 {
        for dx in -1i32..=7 {
            let x = x0 as i32 + dx;
            let y = y0 as i32 + dy;
            if x < 0 || y < 0 || x >= size as i32 || y >= size as i32 {
                continue;
            }
            let dark = (0..=6).contains(&dx)
                && (0..=6).contains(&dy)
                && (dx == 0
                    || dx == 6
                    || dy == 0
                    || dy == 6
                    || ((2..=4).contains(&dx) && (2..=4).contains(&dy)));
            let idx = y as usize * size + x as usize;
            modules[idx] = dark;
            function[idx] = true;
        }
    }
}

fn place_alignment(modules: &mut [bool], function: &mut [bool], size: usize, cx: usize, cy: usize) {
    for dy in -2i32..=2 {
        for dx in -2i32..=2 {
            let x = (cx as i32 + dx) as usize;
            let y = (cy as i32 + dy) as usize;
            let dist = dx.abs().max(dy.abs());
            let idx = y * size + x;
            modules[idx] = dist != 1;
            function[idx] = true;
        }
    }
}

fn reserve_format(function: &mut [bool], size: usize) {
    for i in 0..=8 {
        function[8 * size + i] = true;
        function[i * size + 8] = true;
    }
    for i in 0..8 {
        function[8 * size + (size - 1 - i)] = true;
        function[(size - 1 - i) * size + 8] = true;
    }
}

fn place_data(modules: &mut [bool], function: &[bool], size: usize, codewords: &[u8]) {
    let mut bit_idx = 0usize;
    let total_bits = codewords.len() * 8;
    let mut col = size as i32 - 1;
    let mut upward = true;
    while col >= 1 {
        if col == 6 {
            col -= 1;
        }
        for step in 0..size {
            let y = if upward { size - 1 - step } else { step };
            for dx in 0..2 {
                let x = (col - dx) as usize;
                let idx = y * size + x;
                if !function[idx] {
                    let bit = if bit_idx < total_bits {
                        ((codewords[bit_idx >> 3] >> (7 - (bit_idx & 7))) & 1) != 0
                    } else {
                        false
                    };
                    modules[idx] = bit;
                    bit_idx += 1;
                }
            }
        }
        upward = !upward;
        col -= 2;
    }
}

fn apply_mask(modules: &mut [bool], function: &[bool], size: usize, mask: u8) {
    for y in 0..size {
        for x in 0..size {
            let idx = y * size + x;
            if function[idx] {
                continue;
            }
            let invert = match mask {
                0 => (x + y) % 2 == 0,
                1 => y % 2 == 0,
                2 => x % 3 == 0,
                3 => (x + y) % 3 == 0,
                4 => (y / 2 + x / 3) % 2 == 0,
                5 => (x * y) % 2 + (x * y) % 3 == 0,
                6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
            };
            if invert {
                modules[idx] = !modules[idx];
            }
        }
    }
}

pub(crate) fn format_bits_ec_l(mask: u8) -> u16 {
    let data = (0b01u16 << 3) | (mask as u16 & 0b111);
    let mut rem = data << 10;
    for i in (0..5).rev() {
        if (rem & (1 << (i + 10))) != 0 {
            rem ^= 0x537 << i;
        }
    }
    ((data << 10) | rem) ^ 0x5412
}

fn write_format(modules: &mut [bool], size: usize, mask: u8) {
    let bits = format_bits_ec_l(mask);
    let bit = |i: usize| ((bits >> i) & 1) != 0;

    for i in 0..6 {
        modules[8 * size + i] = bit(14 - i);
    }
    modules[8 * size + 7] = bit(8);
    modules[8 * size + 8] = bit(7);
    modules[7 * size + 8] = bit(6);
    for i in 0..6 {
        modules[(5 - i) * size + 8] = bit(5 - i);
    }

    for i in 0..7 {
        modules[(size - 1 - i) * size + 8] = bit(14 - i);
    }
    for i in 0..8 {
        modules[8 * size + (size - 8 + i)] = bit(7 - i);
    }
}

fn penalty_score(modules: &[bool], size: usize) -> u32 {
    let mut score = 0u32;
    let mut dark = 0u32;
    for y in 0..size {
        let mut run_len = 1u32;
        let mut prev = modules[y * size];
        if prev {
            dark += 1;
        }
        for x in 1..size {
            let cur = modules[y * size + x];
            if cur {
                dark += 1;
            }
            if cur == prev {
                run_len += 1;
            } else {
                if run_len >= 5 {
                    score += run_len - 2;
                }
                run_len = 1;
                prev = cur;
            }
        }
        if run_len >= 5 {
            score += run_len - 2;
        }
    }
    for x in 0..size {
        let mut run_len = 1u32;
        let mut prev = modules[x];
        for y in 1..size {
            let cur = modules[y * size + x];
            if cur == prev {
                run_len += 1;
            } else {
                if run_len >= 5 {
                    score += run_len - 2;
                }
                run_len = 1;
                prev = cur;
            }
        }
        if run_len >= 5 {
            score += run_len - 2;
        }
    }
    for y in 0..size - 1 {
        for x in 0..size - 1 {
            let a = modules[y * size + x];
            if a == modules[y * size + x + 1]
                && a == modules[(y + 1) * size + x]
                && a == modules[(y + 1) * size + x + 1]
            {
                score += 3;
            }
        }
    }
    let total = (size * size) as i32;
    let pct = (dark as i32 * 100) / total;
    score += ((pct - 50).unsigned_abs() / 5) * 10;
    score
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_bits_match_iso_table() {
        assert_eq!(format_bits_ec_l(0), 0x77C4);
        assert_eq!(format_bits_ec_l(1), 0x72F3);
    }

    #[test]
    fn encodes_discord_remote_auth_url() {
        let qr = encode_qr("https://discord.com/ra/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-ABCDEF")
            .expect("version 4 QR should fit Discord RA URL");
        assert_eq!(qr.size, 33);
        assert!(qr.get(0, 0));
        assert!(qr.get(6, 0));
        assert!(qr.get(3, 3));
        assert!(!qr.get(1, 1));
        assert!(qr.get(qr.size - 1, 0));
        assert!(qr.get(0, qr.size - 1));
    }
}
