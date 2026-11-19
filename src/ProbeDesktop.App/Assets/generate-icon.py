"""重新生成 ProbeDesktop.ico（16/20/24/32/48 五个尺寸，PNG 压缩嵌入）。

几何与 ProbeDesktop-mark.svg 一致（viewBox 0 0 32 32）。每个尺寸都从 16 倍超采样原生重绘后缩小，
不是由单张大图缩放。用法：python generate-icon.py [输出路径]；默认写到本脚本同目录的 ProbeDesktop.ico。
依赖：Pillow。
"""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = (16, 20, 24, 32, 48)
SUPER = 16
BLUE = (0x25, 0x63, 0xEB, 255)
WHITE = (255, 255, 255, 255)
SOFT = (0xC7, 0xD7, 0xFB, 255)
POINTS = ((9, 20), (16.5, 12), (23.5, 20))


def render(size: int) -> Image.Image:
    s = size * SUPER / 32.0
    big = Image.new("RGBA", (size * SUPER, size * SUPER), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)

    def circle(cx, cy, r, fill):
        d.ellipse([(cx - r) * s, (cy - r) * s, (cx + r) * s, (cy + r) * s], fill=fill)

    d.rounded_rectangle([1 * s, 1 * s, 31 * s, 31 * s], radius=8 * s, fill=BLUE)
    width = 2.7
    for (x1, y1), (x2, y2) in zip(POINTS, POINTS[1:]):
        d.line([x1 * s, y1 * s, x2 * s, y2 * s], fill=WHITE, width=round(width * s))
    for x, y in POINTS:  # 圆端与圆角连接
        circle(x, y, width / 2, WHITE)
    circle(9, 20, 3, SOFT)
    circle(23.5, 20, 3, SOFT)
    circle(16.5, 12, 4, WHITE)
    return big.resize((size, size), Image.LANCZOS)


def build() -> bytes:
    images = []
    for size in SIZES:
        buf = io.BytesIO()
        render(size).save(buf, format="PNG")
        images.append((size, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


if __name__ == "__main__":
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name("ProbeDesktop.ico")
    out.write_bytes(build())
    print(f"wrote {out} ({out.stat().st_size} bytes, sizes {SIZES})")
