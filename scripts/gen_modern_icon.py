#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成现代化平面风 WPF 应用图标 AppIcon.ico。

设计（CONFIG 可改）：
- 斜向渐变圆角方底（靛蓝 #4F46E5 -> 青 #06B6D4），现代科技感。
- 白色「对焦框」四角 L 形括号（viewfinder / 检测聚焦语义）。
- 中心白色准星环 + 中心点（AI 视觉检测 / 对准语义）。
- 矢量逐尺寸重绘（非栅格下采样），16/20/24/32 标题栏仍锐利。
- 输出 PNG-in-ICO 容器（每帧独立 PNG 编码），规避 Pillow ICO 单帧坑。

用法：
    python gen_modern_icon.py --out Assets/AppIcon.ico
    python gen_modern_icon.py --out preview.png --png 256
"""
import argparse
import io
import math
import os
import struct
from PIL import Image, ImageDraw

# ==== CONFIG（按需改）====
CONFIG = {
    "sizes": [16, 20, 24, 32, 40, 48, 64, 96, 128, 256],

    # 斜向渐变圆角方底
    "bg_tl": (79, 70, 229),    # #4F46E5 靛蓝（左上）
    "bg_br": (6, 182, 212),    # #06B6D4 青（右下）
    "corner_ratio": 0.22,      # 圆角半径 = S * 0.22

    # 对焦框四角括号（白色）
    "bracket_color": (255, 255, 255),
    "bracket_margin_ratio": 0.16,   # 角到边的间距 = S * 0.16
    "bracket_len_ratio": 0.30,      # 每边长度 = S * 0.30
    "bracket_width_ratio": 0.075,   # 线宽 = S * 0.075

    # 中心准星环 + 中心点
    "reticle_color": (255, 255, 255),
    "reticle_radius_ratio": 0.17,   # R = S * 0.17
    "reticle_width_ratio": 0.055,   # 环粗 = S * 0.055
    "dot_radius_ratio": 0.045,      # 中心点半径 = S * 0.045
}


def lerp(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))


def round_line(d, p1, p2, width, color):
    """画一端带圆头的线段（避免小尺寸下端头被切）。"""
    d.line([p1, p2], fill=color, width=width)
    r = max(1, width // 2)
    for (px, py) in (p1, p2):
        d.ellipse([px - r, py - r, px + r, py + r], fill=color)


def draw_icon(S, cfg):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 1) 斜向渐变圆角方底
    bg = Image.new("RGB", (S, S))
    bd = ImageDraw.Draw(bg)
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2 * (S - 1)) if S > 1 else 0.0
            bd.point((x, y), fill=lerp(cfg["bg_tl"], cfg["bg_br"], t))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, S - 1, S - 1],
        radius=max(1, int(S * cfg["corner_ratio"])), fill=255)
    bg_rgba = bg.convert("RGBA")
    bg_rgba.putalpha(mask)
    img = Image.alpha_composite(img, bg_rgba)
    d = ImageDraw.Draw(img)

    # 2) 对焦框四角括号
    m = S * cfg["bracket_margin_ratio"]
    ln = S * cfg["bracket_len_ratio"]
    bw = max(1, int(S * cfg["bracket_width_ratio"]))
    col = cfg["bracket_color"]
    # 左上
    round_line(d, (m, m), (m + ln, m), bw, col)
    round_line(d, (m, m), (m, m + ln), bw, col)
    # 右上
    round_line(d, (S - m, m), (S - m - ln, m), bw, col)
    round_line(d, (S - m, m), (S - m, m + ln), bw, col)
    # 左下
    round_line(d, (m, S - m), (m + ln, S - m), bw, col)
    round_line(d, (m, S - m), (m, S - m - ln), bw, col)
    # 右下
    round_line(d, (S - m, S - m), (S - m - ln, S - m), bw, col)
    round_line(d, (S - m, S - m), (S - m, S - m - ln), bw, col)

    # 3) 中心准星环 + 中心点
    cx = cy = S / 2.0
    R = S * cfg["reticle_radius_ratio"]
    rw = max(1, int(S * cfg["reticle_width_ratio"]))
    d.ellipse([cx - R, cy - R, cx + R, cy + R],
              outline=cfg["reticle_color"], width=rw)
    dr = max(1, int(S * cfg["dot_radius_ratio"]))
    d.ellipse([cx - dr, cy - dr, cx + dr, cy + dr], fill=cfg["reticle_color"])

    return img


def encode_png(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def build_ico(frames):
    """frames: list of (size, png_bytes)。组装 PNG-in-ICO 容器。"""
    entries = []
    data_offset = 6 + 16 * len(frames)
    body = bytearray()
    for (size, png) in frames:
        b = size if size < 256 else 0  # 256 用 0 表示
        entries.append(struct.pack("<BBBBHHII",
                                    b, b, 0, 0,     # w,h,colorcount,reserved
                                    1, 32,          # planes, bitcount
                                    len(png), data_offset + len(body)))
        body += png
    header = struct.pack("<HHH", 0, 1, len(frames))
    return header + b"".join(entries) + bytes(body)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="Assets/AppIcon.ico")
    ap.add_argument("--sizes", default=",".join(str(s) for s in CONFIG["sizes"]))
    ap.add_argument("--png", type=int, default=0,
                    help="若 >0，仅输出该尺寸 PNG 预览（忽略 --out 的 ico）")
    args = ap.parse_args()

    sizes = [int(s) for s in args.sizes.split(",") if s]

    if args.png:
        img = draw_icon(args.png, CONFIG)
        img.save(args.out, format="PNG")
        print(f"preview png saved: {args.out} ({args.png}x{args.png})")
        return

    frames = [(s, encode_png(draw_icon(s, CONFIG))) for s in sizes]
    ico = build_ico(frames)
    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(ico)
    print(f"saved: {out} sizes: {sizes} bytes: {len(ico)}")


if __name__ == "__main__":
    main()
