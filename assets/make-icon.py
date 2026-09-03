# make-icon.py — focus-desktop 图标 v7b：Kevin 选定的铅笔 SVG（iconfont 素材）
# 源资产：assets/pencil-icon.svg（铅笔+橡皮，浅灰圆底/红橡皮/浅蓝笔身/黄笔尖）
# 渲染：cairosvg 256px PNG → 手工多帧 ICO（16/24/32/48/64/128/256 全尺寸）
# v1.0.4 修复：Pillow save(sizes=...) 只写单帧（16px）→ 桌面大图标回退空白文档图标。
#   手工 ICO 封包：ICONDIR + 每帧 PNG（Vista+ 原生支持），读回 n_frames 验证 7 帧。
# 依赖：pip install cairosvg pillow
# 重生成：python assets/make-icon.py
import io
from struct import pack

import cairosvg
from PIL import Image

SRC = 'assets/pencil-icon.svg'
SIZES = [16, 24, 32, 48, 64, 128, 256]


def build_ico(png_path: str, ico_path: str):
    img = Image.open(png_path).convert('RGBA')
    headers, images = [], []
    offset = 6 + 16 * len(SIZES)
    for s in SIZES:
        frame = img.resize((s, s), Image.LANCZOS)
        buf = io.BytesIO()
        frame.save(buf, format='PNG')
        data = buf.getvalue()
        # ICONDIRENTRY: width(1, 0=256) height(1) colors(1) reserved(1) planes(2) bpp(2) bytes(4) offset(4)
        headers.append(pack('<BBBBHHII', s % 256, 0, 0, 0, 1, 32, len(data), offset))
        images.append(data)
        offset += len(data)
    with open(ico_path, 'wb') as f:
        f.write(pack('<HHH', 0, 1, len(SIZES)))   # ICONDIR: reserved=0, type=1(icon), count
        for h in headers:
            f.write(h)
        for d in images:
            f.write(d)


if __name__ == '__main__':
    cairosvg.svg2png(url=SRC, write_to='assets/focus-icon-256.png',
                     output_width=256, output_height=256)
    ico_path = 'assets/focus.ico'
    build_ico('assets/focus-icon-256.png', ico_path)

    # 验证：ICONDIR 解析（Pillow 默认只暴露最大帧——二进制层校验才是真相）
    import struct
    raw = open(ico_path, 'rb').read()
    _, typ, cnt = struct.unpack('<HHH', raw[:6])
    assert typ == 1 and cnt == len(SIZES), f'bad ICO header: type={typ} count={cnt}'
    for i in range(cnt):
        w, _, _, _, _, _, size, off = struct.unpack('<BBBBHHII', raw[6 + 16 * i:6 + 16 * i + 16])
        assert raw[off:off + 8] == b'\x89PNG\r\n\x1a\n', f'frame {i} not PNG'
    print(f'ICO verified: {cnt} PNG frames (16..256)')
    # 16px 实尺放大预览（vision 自检用）
    ico = Image.open(ico_path)
    ico.seek(0)
    ico.resize((160, 160), Image.NEAREST).save('assets/focus-icon-16-preview.png')
    print('icon v7b written: focus.ico (7 frames) / focus-icon-256.png / 16px preview')
