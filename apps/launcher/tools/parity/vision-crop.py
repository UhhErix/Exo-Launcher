import json
import os
import sys
from PIL import Image, ImageDraw


def make_crop(react_path, native_path, bbox, out_path, pad=16):
    ref = Image.open(react_path).convert('RGB')
    snap = Image.open(native_path).convert('RGB')
    if snap.size != ref.size:
        snap = snap.resize(ref.size, Image.LANCZOS)
    w, h = ref.size
    bx, by, bw, bh = [int(round(v)) for v in bbox]
    x0 = max(0, bx - pad)
    y0 = max(0, by - pad)
    x1 = min(w, max(x0 + 32, bx + bw + pad))
    y1 = min(h, max(y0 + 32, by + bh + pad))
    cw = x1 - x0
    ch = y1 - y0
    header = 22
    gap = 4
    canvas = Image.new('RGB', (cw * 2 + gap, ch + header), (18, 18, 18))
    canvas.paste(ref.crop((x0, y0, x1, y1)), (0, header))
    canvas.paste(snap.crop((x0, y0, x1, y1)), (cw + gap, header))
    draw = ImageDraw.Draw(canvas)
    draw.text((6, 4), f'REACT ({bx},{by} {bw}x{bh})', fill=(160, 220, 255))
    draw.text((cw + gap + 6, 4), 'NATIVE (RUNE)', fill=(255, 200, 140))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    canvas.save(out_path)


def main():
    manifest_path = sys.argv[1]
    with open(manifest_path, 'r', encoding='utf-8') as f:
        entries = json.load(f)
    for entry in entries:
        make_crop(entry['reactPath'], entry['nativePath'], entry['bbox'], entry['outPath'])


if __name__ == '__main__':
    main()
