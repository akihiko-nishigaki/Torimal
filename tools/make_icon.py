"""アプリアイコン (assets/app.ico) を生成する。

標準ライブラリのみを使用（Pillow 不要）。PNG を手組みし、
Vista 以降がサポートする PNG 圧縮エントリとして ICO にまとめる。

使い方:  python -I tools/make_icon.py src/Torimaru/assets/app.ico
"""

import math
import struct
import sys
import zlib

# 配色（スクリーンキャプチャらしい「青地 + 白の選択枠 + 赤の録点」）
BG = (31, 111, 235, 255)      # 背景の青
BG_DARK = (21, 83, 180, 255)  # 下方向のグラデーション
FRAME = (255, 255, 255, 255)  # 選択枠の白
ACCENT = (229, 57, 53, 255)   # アクセントの赤


def _rounded_alpha(x, y, size, radius):
    """角丸四角形の内側判定（簡易アンチエイリアス付き、0.0〜1.0 を返す）。"""
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    dist = math.hypot(x - cx, y - cy)
    if dist <= radius - 0.7:
        return 1.0
    if dist >= radius + 0.7:
        return 0.0
    return (radius + 0.7 - dist) / 1.4


def _blend(dst, src, alpha):
    if alpha <= 0:
        return dst
    if alpha >= 1 and src[3] == 255:
        return src
    a = alpha * (src[3] / 255.0)
    return (
        round(dst[0] * (1 - a) + src[0] * a),
        round(dst[1] * (1 - a) + src[1] * a),
        round(dst[2] * (1 - a) + src[2] * a),
        max(dst[3], round(255 * a)),
    )


def render(size):
    """1 枚のアイコン画像を RGBA のピクセル配列として描く。"""
    radius = size * 0.22
    # 選択枠の配置（内側に余白を取る）
    inset = size * 0.26
    thickness = max(1.0, size * 0.075)
    arm = size * 0.16  # コーナーブラケットの腕の長さ

    left, top = inset, inset
    right, bottom = size - inset, size - inset

    pixels = []
    for y in range(size):
        row = []
        for x in range(size):
            px, py = x + 0.5, y + 0.5
            color = (0, 0, 0, 0)

            # 背景（上から下へわずかにグラデーション）
            shape = _rounded_alpha(px, py, size, radius)
            if shape > 0:
                ratio = py / size
                bg = tuple(
                    round(BG[i] * (1 - ratio) + BG_DARK[i] * ratio) for i in range(3)
                ) + (255,)
                color = _blend(color, bg, shape)

            # 選択枠のコーナーブラケット 4 隅
            on_frame = False
            for fx, fy, sx, sy in (
                (left, top, 1, 1),
                (right, top, -1, 1),
                (left, bottom, 1, -1),
                (right, bottom, -1, -1),
            ):
                # 横棒
                hx0, hx1 = sorted((fx, fx + sx * arm))
                hy0, hy1 = sorted((fy, fy + sy * thickness))
                if hx0 <= px <= hx1 and hy0 <= py <= hy1:
                    on_frame = True
                    break
                # 縦棒
                vx0, vx1 = sorted((fx, fx + sx * thickness))
                vy0, vy1 = sorted((fy, fy + sy * arm))
                if vx0 <= px <= vx1 and vy0 <= py <= vy1:
                    on_frame = True
                    break

            if on_frame:
                color = _blend(color, FRAME, 1.0)

            # 中央のアクセント点
            dot_r = size * 0.085
            d = math.hypot(px - size / 2, py - size / 2)
            if d <= dot_r + 0.7:
                a = 1.0 if d <= dot_r - 0.7 else (dot_r + 0.7 - d) / 1.4
                color = _blend(color, ACCENT, max(0.0, min(1.0, a)))

            row.append(color)
        pixels.append(row)
    return pixels


def _chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def encode_png(pixels):
    size = len(pixels)
    raw = bytearray()
    for row in pixels:
        raw.append(0)  # フィルタ: None
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", ihdr)
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _chunk(b"IEND", b"")
    )


def build_ico(sizes, out_path):
    images = [(s, encode_png(render(s))) for s in sizes]

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)

    entries = bytearray()
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    with open(out_path, "wb") as f:
        f.write(header)
        f.write(entries)
        for _, data in images:
            f.write(data)

    return offset


if __name__ == "__main__":
    target = sys.argv[1]
    total = build_ico([16, 24, 32, 48, 64, 256], target)
    print("wrote %s (%d bytes)" % (target, total))
