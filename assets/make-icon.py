# make-icon.py — focus-desktop 图标生成器 v3：手握笔写字
# 设计：深色圆角底 + 金色斜笔（45°朝左下书写）+ 浅色握笔的手（掌+三指扣笔杆）+ 笔尖下金色书写线
# 重生成：python assets/make-icon.py（输出 assets/focus.ico + assets/focus-icon-256.png）
from PIL import Image, ImageDraw
import math

S = 4  # 超采样倍数（抗锯齿）

GOLD = (245, 197, 66, 255)
GOLD_HI = (255, 224, 110, 255)
NIB = (62, 47, 14, 255)
LIGHT = (221, 226, 236, 255)      # 手（中性浅色，深底高对比）
OUTLINE = (38, 41, 47, 255)       # 手指描边（与底色拉开层次）

def make_icon(size):
    px = size * S
    img = Image.new('RGBA', (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = px / 256  # 256 坐标系缩放

    # 底：圆角方块（主题深色）
    m, r = int(8 * k), int(56 * k)
    d.rounded_rectangle([m, m, px - m, px - m], radius=r, fill=(35, 38, 44, 255))

    def rot_rect(cx, cy, w, h, ang_deg, fill):
        a = math.radians(ang_deg)
        pts = []
        for sx, sy in [(-w/2, -h/2), (w/2, -h/2), (w/2, h/2), (-w/2, h/2)]:
            x = cx + sx * math.cos(a) - sy * math.sin(a)
            y = cy + sx * math.sin(a) + sy * math.cos(a)
            pts.append((x, y))
        d.polygon(pts, fill=fill)

    def capsule(cx, cy, ang_deg, length, width, fill):
        """圆头粗线（line + 两端圆），用于手指。"""
        a = math.radians(ang_deg)
        ux, uy = math.cos(a), math.sin(a)
        x1, y1 = cx - ux * length / 2, cy - uy * length / 2
        x2, y2 = cx + ux * length / 2, cy + uy * length / 2
        d.line([x1, y1, x2, y2], fill=fill, width=int(width))
        rr = width / 2
        for (ex, ey) in [(x1, y1), (x2, y2)]:
            d.ellipse([ex - rr, ey - rr, ex + rr, ey + rr], fill=fill)

    # ---- 笔（-45°：右上笔尾 → 左下笔尖）----
    rot_rect(118 * k, 118 * k, 190 * k, 40 * k, -45, GOLD)          # 笔杆
    rot_rect(140 * k, 96 * k, 76 * k, 10 * k, -45, GOLD_HI)         # 杆上高光
    rot_rect(44 * k, 192 * k, 30 * k, 40 * k, -45, NIB)             # 笔尖（深色 nib）

    # ---- 手：掌（浅色椭圆，压住笔杆中下段）----
    cx, cy, rx, ry = 152 * k, 152 * k, 56 * k, 47 * k
    d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=LIGHT)

    # ---- 三指扣笔杆（垂直于笔的方向 = +45°；先描边后本色）----
    for (fx, fy) in [(104, 104), (126, 126), (148, 148)]:
        capsule(fx * k, fy * k, 45, 84 * k, 27 * k, OUTLINE)   # 深描边
        capsule(fx * k, fy * k, 45, 80 * k, 20 * k, LIGHT)     # 浅指节

    # ---- 书写线（笔尖下方金色短横线 = 正在写）----
    capsule(74 * k, 208 * k, 0, 86 * k, 11 * k, GOLD)

    return img.resize((size, size), Image.LANCZOS)

if __name__ == '__main__':
    sizes = [16, 32, 48, 64, 128, 256]
    imgs = [make_icon(s) for s in sizes]
    imgs[0].save('assets/focus.ico', format='ICO',
                 sizes=[(s, s) for s in sizes], append_images=imgs[1:])
    imgs[-1].save('assets/focus-icon-256.png')
    # 16px 实尺放大预览（vision 自检用）
    imgs[0].resize((160, 160), Image.NEAREST).save('assets/focus-icon-16-preview.png')
    print('icon v3 written: focus.ico / focus-icon-256.png / 16px preview')
