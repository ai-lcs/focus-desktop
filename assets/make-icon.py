# make-icon.py — focus-desktop 图标 v7：Kevin 选定的铅笔 SVG（iconfont 素材）
# 源资产：assets/pencil-icon.svg（铅笔+橡皮，浅灰圆底/红橡皮/浅蓝笔身/黄笔尖）
# 渲染：cairosvg 256px PNG → PIL 多尺寸 ICO（16/32/48/64/128/256）
# 依赖：pip install cairosvg pillow
# 重生成：python assets/make-icon.py（输出 assets/focus.ico + focus-icon-256.png + 16px 预览）
import cairosvg
from PIL import Image

SRC = 'assets/pencil-icon.svg'

if __name__ == '__main__':
    # 256px 高清基准（超采样由矢量天然保证，无需位图超采样）
    cairosvg.svg2png(url=SRC, write_to='assets/focus-icon-256.png',
                     output_width=256, output_height=256)

    img256 = Image.open('assets/focus-icon-256.png').convert('RGBA')
    sizes = [16, 32, 48, 64, 128, 256]
    imgs = [img256.resize((s, s), Image.LANCZOS) for s in sizes]
    imgs[0].save('assets/focus.ico', format='ICO',
                 sizes=[(s, s) for s in sizes], append_images=imgs[1:])
    # 16px 实尺放大预览（vision 自检用）
    imgs[0].resize((160, 160), Image.NEAREST).save('assets/focus-icon-16-preview.png')
    print('icon v7 written from pencil-icon.svg: focus.ico / focus-icon-256.png / 16px preview')
