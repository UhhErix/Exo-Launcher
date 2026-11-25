import gzip
import io
import math
import struct
import sys
import tarfile


def read_varint(buf, i):
    val = 0
    shift = 0
    while True:
        c = buf[i]
        i += 1
        val |= (c & 0x7F) << shift
        if not (c & 0x80):
            return val, i
        shift += 7


def write_varint(v):
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def iter_fields(buf):
    i = 0
    n = len(buf)
    while i < n:
        start = i
        tag, i = read_varint(buf, i)
        fn, wt = tag >> 3, tag & 7
        if wt == 0:
            val, i = read_varint(buf, i)
            yield fn, wt, val, buf[start:i]
        elif wt == 1:
            val = buf[i : i + 8]
            i += 8
            yield fn, wt, val, buf[start:i]
        elif wt == 2:
            ln, i = read_varint(buf, i)
            val = buf[i : i + ln]
            i += ln
            yield fn, wt, val, buf[start:i]
        elif wt == 5:
            val = buf[i : i + 4]
            i += 4
            yield fn, wt, val, buf[start:i]
        else:
            raise ValueError(f"Unsupported protobuf wire type {wt}")


def pack_tensor_fp16(tb):
    fields = list(iter_fields(tb))
    dt = None
    raw_idx = None
    for idx, (fn, wt, val, _raw) in enumerate(fields):
        if fn == 2 and wt == 0:
            dt = val
        elif fn == 9 and wt == 2:
            raw_idx = idx
    if dt != 1 or raw_idx is None:
        return tb, 0
    data = fields[raw_idx][2]
    if len(data) < 64 or len(data) % 4 != 0:
        return tb, 0
    floats = struct.unpack(f"<{len(data) // 4}f", data)
    if any(not math.isfinite(x) or abs(x) > 65000.0 for x in floats):
        return tb, 0
    f16_bytes = struct.pack(f"<{len(floats)}e", *floats)
    out = bytearray()
    for idx, (fn, wt, _val, raw) in enumerate(fields):
        if fn == 2 and wt == 0:
            out += write_varint((2 << 3) | 0) + write_varint(10)
        elif idx == raw_idx:
            out += write_varint((9 << 3) | 2) + write_varint(len(f16_bytes)) + f16_bytes
        else:
            out += raw
    return bytes(out), len(data) - len(f16_bytes)


def pack_attr(ab):
    out = bytearray()
    saved = 0
    for fn, wt, val, raw in iter_fields(ab):
        if fn == 5 and wt == 2:
            nt, sv = pack_tensor_fp16(val)
            saved += sv
            out += write_varint((5 << 3) | 2) + write_varint(len(nt)) + nt
        else:
            out += raw
    return bytes(out), saved


def pack_node(nb):
    out = bytearray()
    saved = 0
    for fn, wt, val, raw in iter_fields(nb):
        if fn == 5 and wt == 2:
            na, sv = pack_attr(val)
            saved += sv
            out += write_varint((5 << 3) | 2) + write_varint(len(na)) + na
        else:
            out += raw
    return bytes(out), saved


def pack_onnx(ob):
    out = bytearray()
    saved_tot = 0
    for fn, wt, val, raw in iter_fields(ob):
        if fn == 7 and wt == 2:
            gout = bytearray()
            for gfn, gwt, gval, graw in iter_fields(val):
                if gfn == 5 and gwt == 2:
                    nt, sv = pack_tensor_fp16(gval)
                    saved_tot += sv
                    gout += write_varint((5 << 3) | 2) + write_varint(len(nt)) + nt
                elif gfn == 1 and gwt == 2:
                    nn, sv = pack_node(gval)
                    saved_tot += sv
                    gout += write_varint((1 << 3) | 2) + write_varint(len(nn)) + nn
                else:
                    gout += graw
            out += write_varint((7 << 3) | 2) + write_varint(len(gout)) + gout
        else:
            out += raw
    return bytes(out), saved_tot


def main():
    src_path, dst_path = sys.argv[1], sys.argv[2]
    buf = io.BytesIO()
    with tarfile.open(src_path, "r:gz") as tf, tarfile.open(fileobj=buf, mode="w") as out_tf:
        for member in tf.getmembers():
            data = tf.extractfile(member).read()
            if member.name.endswith(".onnx"):
                data, _ = pack_onnx(data)
            info = tarfile.TarInfo(member.name)
            info.size = len(data)
            out_tf.addfile(info, io.BytesIO(data))
    gz = gzip.compress(buf.getvalue(), compresslevel=9, mtime=0)
    with open(dst_path, "wb") as f:
        f.write(gz)
    print(f"pack-fp16: {len(gz) / 1048576:.2f} MiB -> {dst_path}")


if __name__ == "__main__":
    main()
