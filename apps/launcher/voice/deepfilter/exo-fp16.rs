fn read_varint(buf: &[u8], i: &mut usize) -> Option<u64> {
    let mut val = 0u64;
    let mut shift = 0u32;
    while *i < buf.len() {
        let b = buf[*i];
        *i += 1;
        val |= ((b & 0x7f) as u64) << shift;
        if (b & 0x80) == 0 {
            return Some(val);
        }
        shift += 7;
        if shift >= 64 {
            return None;
        }
    }
    None
}

fn write_varint(mut v: u64, out: &mut Vec<u8>) {
    loop {
        let b = (v & 0x7f) as u8;
        v >>= 7;
        if v != 0 {
            out.push(b | 0x80);
        } else {
            out.push(b);
            break;
        }
    }
}

fn f16_to_f32(h: u16) -> f32 {
    let sign = ((h as u32) & 0x8000) << 16;
    let exp = ((h >> 10) & 0x1f) as u32;
    let mant = (h & 0x03ff) as u32;
    let bits = if exp == 0 {
        if mant == 0 {
            sign
        } else {
            let mut m = mant;
            let mut e = 127u32 - 14;
            while (m & 0x0400) == 0 {
                m <<= 1;
                e -= 1;
            }
            m &= 0x03ff;
            sign | (e << 23) | (m << 13)
        }
    } else if exp == 31 {
        sign | 0x7f80_0000 | (mant << 13)
    } else {
        sign | ((exp + (127 - 15)) << 23) | (mant << 13)
    };
    f32::from_bits(bits)
}

fn skip_field(buf: &[u8], wt: u64, i: &mut usize) -> Option<()> {
    match wt {
        0 => {
            read_varint(buf, i)?;
            Some(())
        }
        1 => {
            *i = (*i).checked_add(8)?;
            (*i <= buf.len()).then_some(())
        }
        2 => {
            let ln = read_varint(buf, i)? as usize;
            *i = (*i).checked_add(ln)?;
            (*i <= buf.len()).then_some(())
        }
        5 => {
            *i = (*i).checked_add(4)?;
            (*i <= buf.len()).then_some(())
        }
        _ => None,
    }
}

fn unpack_tensor_fp16(tb: &[u8]) -> Option<Vec<u8>> {
    let mut i = 0usize;
    let mut dt = 0u64;
    let mut raw_span: Option<(usize, usize)> = None;
    let mut fields: Vec<(u64, u64, usize, usize)> = Vec::new();
    while i < tb.len() {
        let start = i;
        let tag = read_varint(tb, &mut i)?;
        let fn_num = tag >> 3;
        let wt = tag & 7;
        if fn_num == 2 && wt == 0 {
            dt = read_varint(tb, &mut i)?;
        } else if fn_num == 9 && wt == 2 {
            let ln = read_varint(tb, &mut i)? as usize;
            let val_start = i;
            i = i.checked_add(ln)?;
            if i > tb.len() {
                return None;
            }
            raw_span = Some((val_start, i));
        } else {
            skip_field(tb, wt, &mut i)?;
        }
        fields.push((fn_num, wt, start, i));
    }
    if dt != 10 {
        return None;
    }
    let (rs, re) = raw_span?;
    let f16_bytes = &tb[rs..re];
    if f16_bytes.len() % 2 != 0 {
        return None;
    }
    let mut f32_bytes = Vec::with_capacity(f16_bytes.len() * 2);
    for chunk in f16_bytes.chunks_exact(2) {
        let h = u16::from_le_bytes([chunk[0], chunk[1]]);
        f32_bytes.extend_from_slice(&f16_to_f32(h).to_le_bytes());
    }
    let mut out = Vec::with_capacity(tb.len() + f16_bytes.len() + 8);
    for (fn_num, wt, start, end) in fields {
        if fn_num == 2 && wt == 0 {
            write_varint((2 << 3) | 0, &mut out);
            write_varint(1, &mut out);
        } else if fn_num == 9 && wt == 2 {
            write_varint((9 << 3) | 2, &mut out);
            write_varint(f32_bytes.len() as u64, &mut out);
            out.extend_from_slice(&f32_bytes);
        } else {
            out.extend_from_slice(&tb[start..end]);
        }
    }
    Some(out)
}

fn unpack_attr_fp16(ab: &[u8]) -> Option<Vec<u8>> {
    let mut i = 0usize;
    let mut changed = false;
    let mut out = Vec::with_capacity(ab.len());
    while i < ab.len() {
        let start = i;
        let tag = read_varint(ab, &mut i)?;
        let fn_num = tag >> 3;
        let wt = tag & 7;
        if fn_num == 5 && wt == 2 {
            let ln = read_varint(ab, &mut i)? as usize;
            let val_end = i.checked_add(ln)?;
            if val_end > ab.len() {
                return None;
            }
            if let Some(nt) = unpack_tensor_fp16(&ab[i..val_end]) {
                write_varint((5 << 3) | 2, &mut out);
                write_varint(nt.len() as u64, &mut out);
                out.extend_from_slice(&nt);
                changed = true;
            } else {
                out.extend_from_slice(&ab[start..val_end]);
            }
            i = val_end;
        } else {
            skip_field(ab, wt, &mut i)?;
            out.extend_from_slice(&ab[start..i]);
        }
    }
    changed.then_some(out)
}

fn unpack_node_fp16(nb: &[u8]) -> Option<Vec<u8>> {
    let mut i = 0usize;
    let mut changed = false;
    let mut out = Vec::with_capacity(nb.len());
    while i < nb.len() {
        let start = i;
        let tag = read_varint(nb, &mut i)?;
        let fn_num = tag >> 3;
        let wt = tag & 7;
        if fn_num == 5 && wt == 2 {
            let ln = read_varint(nb, &mut i)? as usize;
            let val_end = i.checked_add(ln)?;
            if val_end > nb.len() {
                return None;
            }
            if let Some(na) = unpack_attr_fp16(&nb[i..val_end]) {
                write_varint((5 << 3) | 2, &mut out);
                write_varint(na.len() as u64, &mut out);
                out.extend_from_slice(&na);
                changed = true;
            } else {
                out.extend_from_slice(&nb[start..val_end]);
            }
            i = val_end;
        } else {
            skip_field(nb, wt, &mut i)?;
            out.extend_from_slice(&nb[start..i]);
        }
    }
    changed.then_some(out)
}

fn unpack_graph_fp16(gb: &[u8]) -> Option<Vec<u8>> {
    let mut i = 0usize;
    let mut changed = false;
    let mut out = Vec::with_capacity(gb.len() * 2);
    while i < gb.len() {
        let start = i;
        let tag = read_varint(gb, &mut i)?;
        let fn_num = tag >> 3;
        let wt = tag & 7;
        if fn_num == 5 && wt == 2 {
            let ln = read_varint(gb, &mut i)? as usize;
            let val_end = i.checked_add(ln)?;
            if val_end > gb.len() {
                return None;
            }
            if let Some(nt) = unpack_tensor_fp16(&gb[i..val_end]) {
                write_varint((5 << 3) | 2, &mut out);
                write_varint(nt.len() as u64, &mut out);
                out.extend_from_slice(&nt);
                changed = true;
            } else {
                out.extend_from_slice(&gb[start..val_end]);
            }
            i = val_end;
        } else if fn_num == 1 && wt == 2 {
            let ln = read_varint(gb, &mut i)? as usize;
            let val_end = i.checked_add(ln)?;
            if val_end > gb.len() {
                return None;
            }
            if let Some(nn) = unpack_node_fp16(&gb[i..val_end]) {
                write_varint((1 << 3) | 2, &mut out);
                write_varint(nn.len() as u64, &mut out);
                out.extend_from_slice(&nn);
                changed = true;
            } else {
                out.extend_from_slice(&gb[start..val_end]);
            }
            i = val_end;
        } else {
            skip_field(gb, wt, &mut i)?;
            out.extend_from_slice(&gb[start..i]);
        }
    }
    changed.then_some(out)
}

fn exo_unpack_fp16_onnx(ob: Vec<u8>) -> Vec<u8> {
    let mut i = 0usize;
    let mut changed = false;
    let mut out = Vec::with_capacity(ob.len() * 2);
    while i < ob.len() {
        let start = i;
        let Some(tag) = read_varint(&ob, &mut i) else {
            return ob;
        };
        let fn_num = tag >> 3;
        let wt = tag & 7;
        if fn_num == 7 && wt == 2 {
            let Some(ln) = read_varint(&ob, &mut i) else {
                return ob;
            };
            let Some(val_end) = i.checked_add(ln as usize) else {
                return ob;
            };
            if val_end > ob.len() {
                return ob;
            }
            if let Some(ng) = unpack_graph_fp16(&ob[i..val_end]) {
                write_varint((7 << 3) | 2, &mut out);
                write_varint(ng.len() as u64, &mut out);
                out.extend_from_slice(&ng);
                changed = true;
            } else {
                out.extend_from_slice(&ob[start..val_end]);
            }
            i = val_end;
        } else {
            if skip_field(&ob, wt, &mut i).is_none() {
                return ob;
            }
            out.extend_from_slice(&ob[start..i]);
        }
    }
    if changed { out } else { ob }
}
