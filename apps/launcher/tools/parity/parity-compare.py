import json
import sys
from PIL import Image, ImageChops, ImageDraw


def compare_images(ref_path, snap_path, out_path):
    ref = Image.open(ref_path).convert('RGB')
    snap = Image.open(snap_path).convert('RGB')
    size_ref = list(ref.size)
    size_snap = list(snap.size)
    size_match = snap.size == ref.size
    if not size_match:
        print(f'size differs: ref {ref.size} snap {snap.size}; snap resized to ref for comparison')
        snap = snap.resize(ref.size, Image.LANCZOS)

    w, h = ref.size
    diff = ImageChops.difference(ref, snap).convert('L')
    heat = Image.merge('RGB', (diff.point(lambda v: min(255, v * 3)), diff.point(lambda v: 0), diff.point(lambda v: 0)))

    sheet = Image.new('RGB', (w * 2, h * 2), 'black')
    sheet.paste(ref, (0, 0))
    sheet.paste(snap, (w, 0))
    sheet.paste(Image.blend(ref, snap, 0.5), (0, h))
    sheet.paste(heat, (w, h))
    draw = ImageDraw.Draw(sheet)
    for x, y, label in (
        (8, 8, 'REFERENCE (WebView2)'),
        (w + 8, 8, 'RUNE'),
        (8, h + 8, '50% OVERLAY'),
        (w + 8, h + 8, 'DIFF (red = mismatch)'),
    ):
        draw.rectangle((x - 4, y - 4, x + 230, y + 18), fill='black')
        draw.text((x, y), label, fill='yellow')

    cols, rows = 8, 6
    cw, ch = w // cols, h // rows
    grid = []
    for r in range(rows):
        line = []
        for c in range(cols):
            box = diff.crop((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch))
            hist = box.histogram()
            mean = sum(i * n for i, n in enumerate(hist)) / max(1, sum(hist))
            line.append(mean)
            draw.rectangle((w + c * cw, h + r * ch, w + (c + 1) * cw, h + (r + 1) * ch), outline=(60, 60, 60))
            draw.text((w + c * cw + 4, h + r * ch + 22), f'{mean:.0f}', fill='white')
        grid.append(line)
    sheet.save(out_path)

    total = sum(sum(l) for l in grid) / (cols * rows)
    cells = sorted(((grid[r][c], r, c) for r in range(rows) for c in range(cols)), reverse=True)
    worst = [
        {
            'row': r,
            'col': c,
            'x0': c * cw,
            'x1': (c + 1) * cw,
            'y0': r * ch,
            'y1': (r + 1) * ch,
            'score': round(score, 2),
        }
        for score, r, c in cells[:6]
    ]
    return {
        'ref': ref_path,
        'snap': snap_path,
        'out': out_path,
        'sizeRef': size_ref,
        'sizeSnap': size_snap,
        'sizeMatch': size_match,
        'meanAbsDiff': round(total, 2),
        'maxCellDiff': round(cells[0][0], 2) if cells else 0.0,
        'worstCells': worst,
    }


def main():
    args = sys.argv[1:]
    json_out = None
    if '--json' in args:
        idx = args.index('--json')
        json_out = args[idx + 1]
        args = args[:idx] + args[idx + 2:]

    ref_path, snap_path, out_path = args[0], args[1], args[2]
    result = compare_images(ref_path, snap_path, out_path)
    if json_out == '-':
        print(json.dumps(result))
    else:
        if json_out:
            with open(json_out, 'w', encoding='utf-8') as f:
                json.dump(result, f, indent=2)
        print(f"compare -> {result['out']}")
        print(f"mean abs diff {result['meanAbsDiff']:.1f} (0 = identical)")
        print('worst cells (row,col 0-based, x,y px, score):')
        for cell in result['worstCells']:
            print(f"  ({cell['row']},{cell['col']}) at x={cell['x0']}-{cell['x1']} y={cell['y0']}-{cell['y1']}: {cell['score']:.0f}")


if __name__ == '__main__':
    main()
