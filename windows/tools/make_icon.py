"""生成程序图标 windows/app.ico（16～256 共 7 种尺寸）。

用法：python windows/tools/make_icon.py [输出的 ico] [预览 png]
需要 Pillow。

图形先在 2048×2048 上画，再给每个尺寸单独用 LANCZOS 缩小，小尺寸不会是从 256 二次缩出来的糊图。
白色图形上下按外框放到底板正中（上下留白相等），不靠手调坐标；
这时圆圈的分量在上，重心会略高于正中，看起来正好居中而不压在底部。
"""
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw

S = 2048
K = S / 1024  # 下面的尺寸按 1024 的画布写
WHITE = (255, 255, 255, 255)


def tile():
    """橙色渐变圆角方块。"""
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    top, bot = (240, 160, 80), (196, 82, 38)
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40 * K, 40 * K, S - 40 * K, S - 40 * K], radius=220 * K, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


def glyph():
    """白色图形：带箭头的刷新圆圈、中间向下的下载箭头、底下一条横线。位置随便放，之后整体居中。"""
    layer = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(layer)
    cx, cy, r, w = 512 * K, 512 * K, 290 * K, 74 * K
    # 刷新圆圈：左边留缺口，缺口上端是箭头
    d.arc([cx - r, cy - r, cx + r, cy + r], start=200, end=500, fill=255, width=round(w))
    a = math.radians(200)
    ex, ey = cx + r * math.cos(a), cy + r * math.sin(a)
    d.polygon([(ex - 120 * K, ey - 10 * K), (ex + 70 * K, ey - 40 * K), (ex - 10 * K, ey + 150 * K)], fill=255)
    # 向下的下载箭头
    d.rectangle([cx - 38 * K, cy - 150 * K, cx + 38 * K, cy + 60 * K], fill=255)
    d.polygon([(cx - 130 * K, cy + 30 * K), (cx + 130 * K, cy + 30 * K), (cx, cy + 170 * K)], fill=255)
    # 底下的横线：和圆圈之间留一点空，比圆圈窄，不压住下面
    gap = 44 * K
    y0 = cy + r + gap
    d.rounded_rectangle([cx - 200 * K, y0, cx + 200 * K, y0 + 60 * K], radius=30 * K, fill=255)
    return layer


def centered(layer):
    """上下：外框移到画布正中。左右：下载箭头和横线的竖直对称轴本来就画在正中，
    不按外框算（左边的箭头尖会把外框撑宽，按外框居中整个圆圈就被推到右边了）。"""
    _, top, _, bottom = layer.getbbox()
    return ImageChops.offset(layer, 0, round((S - top - bottom) / 2))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_ico = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "app.ico")
    preview = sys.argv[2] if len(sys.argv) > 2 else None

    img = tile()
    mask = centered(glyph())
    img.paste(Image.new("RGBA", (S, S), WHITE), (0, 0), mask)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [img.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[-1].save(out_ico, sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    if preview:
        frames[-1].save(preview)
    print("已生成", os.path.abspath(out_ico))


if __name__ == "__main__":
    main()
