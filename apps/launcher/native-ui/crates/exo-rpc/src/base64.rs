const ENC_TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

pub fn encode(input: &[u8]) -> String {
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = if chunk.len() > 1 { chunk[1] as u32 } else { 0 };
        let b2 = if chunk.len() > 2 { chunk[2] as u32 } else { 0 };
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(ENC_TABLE[((n >> 18) & 63) as usize] as char);
        out.push(ENC_TABLE[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 {
            ENC_TABLE[((n >> 6) & 63) as usize] as char
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            ENC_TABLE[(n & 63) as usize] as char
        } else {
            '='
        });
    }
    out
}

pub fn decode(input: &str) -> Option<Vec<u8>> {
    let raw = input.as_bytes();
    let stripped: Vec<u8>;
    let bytes: &[u8] = if raw.iter().any(u8::is_ascii_whitespace) {
        stripped = raw
            .iter()
            .copied()
            .filter(|b| !b.is_ascii_whitespace())
            .collect();
        &stripped
    } else {
        raw
    };
    if !bytes.len().is_multiple_of(4) {
        return None;
    }
    let mut out = Vec::with_capacity((bytes.len() / 4) * 3);
    for chunk in bytes.as_chunks::<4>().0 {
        let v0 = dec_val(chunk[0])?;
        let v1 = dec_val(chunk[1])?;
        out.push((v0 << 2) | (v1 >> 4));
        if chunk[2] != b'=' {
            let v2 = dec_val(chunk[2])?;
            out.push((v1 << 4) | (v2 >> 2));
            if chunk[3] != b'=' {
                let v3 = dec_val(chunk[3])?;
                out.push((v2 << 6) | v3);
            }
        }
    }
    Some(out)
}

fn dec_val(b: u8) -> Option<u8> {
    match b {
        b'A'..=b'Z' => Some(b - b'A'),
        b'a'..=b'z' => Some(b - b'a' + 26),
        b'0'..=b'9' => Some(b - b'0' + 52),
        b'+' => Some(62),
        b'/' => Some(63),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_binary_and_text_payloads() {
        for sample in [
            b"".as_slice(),
            b"f",
            b"fo",
            b"foo",
            b"Exo Rune DX12!",
            &[0, 1, 254, 255],
        ] {
            let enc = encode(sample);
            let dec = decode(&enc).expect("valid base64");
            assert_eq!(dec, sample);
        }
        assert_eq!(encode(b"foobar"), "Zm9vYmFy");
    }
}
