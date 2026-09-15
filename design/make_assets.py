"""Regenerates the IMP Weld Photos design assets from the Kosovnice sources.

Run from anywhere:  python make_assets.py
Needs Pillow. Reads the IMP logo and the Zvari card photo from the Kosovnice
repo, writes logo/, icon/, patterns/, images/ and samples/ next to this file.
"""
import os
import shutil
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps

HERE = os.path.dirname(os.path.abspath(__file__))
KOS = r'C:\VS_Projects\IMP_BOMs\IMP_Kosovnice'
SRC_LOGO = os.path.join(KOS, 'src', 'assets', 'imp-logo.png')
SRC_WELD = os.path.join(KOS, 'src', 'assets', 'card-zvari.jpg')

ACCENT = (0x2E, 0x7D, 0x32)
WHITE = (255, 255, 255)


def out(*parts):
    path = os.path.join(HERE, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return path


# ----------------------------------------------------------------- logos
def make_logos():
    shutil.copyfile(SRC_LOGO, out('logo', 'imp-logo.png'))
    logo = Image.open(SRC_LOGO).convert('RGBA')
    r, g, b, a = logo.split()
    if a.getextrema() == (255, 255):
        # No transparency: treat near-white as background.
        gray = ImageOps.grayscale(logo)
        a = gray.point(lambda v: 0 if v > 245 else 255)
    white = Image.new('RGBA', logo.size, WHITE + (0,))
    white.putalpha(a)
    white.save(out('logo', 'imp-logo-white.png'))
    print('logos', logo.size)


# ------------------------------------------------------------------ icon
def make_icon():
    S = 2048  # supersampled canvas, downscaled for every size
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = S / 512.0

    def sc(*v):
        return [round(x * k) for x in v]

    # Tile: same proportions as the Kosovnice app tile (rx 112 on 512).
    d.rounded_rectangle(sc(0, 0, 512, 512), radius=round(112 * k), fill=ACCENT + (255,))

    # Camera body and viewfinder hump.
    d.rounded_rectangle(sc(92, 170, 420, 404), radius=round(46 * k), fill=WHITE + (255,))
    d.rounded_rectangle(sc(190, 130, 322, 196), radius=round(22 * k), fill=WHITE + (255,))

    # Lens: accent ring cut into the body, white centre.
    cx, cy = 256, 290
    d.ellipse(sc(cx - 92, cy - 92, cx + 92, cy + 92), fill=ACCENT + (255,))
    d.ellipse(sc(cx - 58, cy - 58, cx + 58, cy + 58), fill=WHITE + (255,))
    d.ellipse(sc(cx - 24, cy - 24, cx + 24, cy + 24), fill=ACCENT + (255,))

    # Weld spark, top right: a four-point star.
    sx, sy, big, small = 400, 112, 44, 11
    d.polygon(sc(sx, sy - big, sx + small, sy - small, sx + big, sy, sx + small, sy + small,
                 sx, sy + big, sx - small, sy + small, sx - big, sy, sx - small, sy - small),
              fill=WHITE + (255,))

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024]
    for px in sizes:
        img.resize((px, px), Image.LANCZOS).save(out('icon', 'icon-%d.png' % px))
    ico_sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)]
    img.resize((256, 256), Image.LANCZOS).save(out('icon', 'IMPWeldPhotos.ico'), sizes=ico_sizes)
    print('icon', len(sizes), 'pngs + ico')


# --------------------------------------------------------------- pattern
def make_pattern():
    tile = Image.new('RGBA', (20, 20), (0, 0, 0, 0))
    d = ImageDraw.Draw(tile)
    line = WHITE + (round(0.09 * 255),)
    d.line([(0, 0), (19, 0)], fill=line)
    d.line([(0, 0), (0, 19)], fill=line)
    tile.save(out('patterns', 'header-grid.png'))
    print('pattern')


# ----------------------------------------------------------------- photos
def make_photos():
    shutil.copyfile(SRC_WELD, out('images', 'card-zvari.jpg'))
    photo = Image.open(SRC_WELD).convert('RGB')
    gray = ImageOps.autocontrast(ImageOps.grayscale(photo), cutoff=1)
    dark = tuple(round(c * 0.28) for c in ACCENT)
    light = (0xDD, 0xEF, 0xDE)
    ImageOps.colorize(gray, black=dark, white=light, mid=ACCENT).save(
        out('images', 'weld-duotone.jpg'), quality=90)
    print('photos', photo.size)


def load_font(px):
    for name in ('seguisb.ttf', 'segoeuib.ttf', 'arialbd.ttf'):
        path = os.path.join(r'C:\Windows\Fonts', name)
        if os.path.exists(path):
            return ImageFont.truetype(path, px), name
    return ImageFont.load_default(), 'default'


def make_stamp_preview():
    photo = Image.open(SRC_WELD).convert('RGB')
    # Upscale the small card photo so the preview reads like a camera frame.
    W = 1280
    H = round(photo.height * W / photo.width)
    photo = photo.resize((W, H), Image.LANCZOS)
    text = '2000487-W2a-1'

    size = round(H * 0.06)
    font, name = load_font(size)
    while True:
        box = ImageDraw.Draw(photo).textbbox((0, 0), text, font=font)
        tw, th = box[2] - box[0], box[3] - box[1]
        if tw <= W * 0.9 or size <= 8:
            break
        size -= 2
        font, name = load_font(size)

    x = (W - tw) / 2 - box[0]
    y = H * 0.86 - th / 2 - box[1]

    shadow = Image.new('L', (W, H), 0)
    off = max(1, round(size * 0.06))
    ImageDraw.Draw(shadow).text((x + off, y + off), text, font=font, fill=round(255 * 0.6))
    shadow = shadow.filter(ImageFilter.GaussianBlur(max(1, round(size * 0.08))))
    photo.paste(Image.new('RGB', (W, H), (0, 0, 0)), (0, 0), shadow)

    ImageDraw.Draw(photo).text((x, y), text, font=font, fill=WHITE)
    photo.save(out('samples', 'stamp-preview.jpg'), quality=92)
    print('stamp preview', (W, H), 'font', name, size)


def make_header_preview():
    """Reference render of the header (accent, fading grid, white logo, title)."""
    W, H = 1200, 52
    hdr = Image.new('RGBA', (W, H), ACCENT + (255,))
    tile = Image.open(out('patterns', 'header-grid.png')).convert('RGBA')
    grid = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    for gx in range(0, W, 20):
        for gy in range(0, H, 20):
            grid.alpha_composite(tile, (gx, gy))
    # Fade left -> right, gone by 75 % of the width (as .app-header::before).
    mask = Image.new('L', (W, H))
    md = ImageDraw.Draw(mask)
    for mx in range(W):
        md.line([(mx, 0), (mx, H)], fill=max(0, round(255 * (1 - mx / (W * 0.75)))))
    grid.putalpha(Image.composite(grid.getchannel('A'), Image.new('L', (W, H), 0), mask))
    hdr.alpha_composite(grid)

    logo = Image.open(out('logo', 'imp-logo-white.png')).convert('RGBA')
    lh = 30
    lw = round(logo.width * lh / logo.height)
    small = logo.resize((lw, lh), Image.LANCZOS)
    small.putalpha(small.getchannel('A').point(lambda v: round(v * 0.85)))
    hdr.alpha_composite(small, (12, (H - lh) // 2))

    font = ImageFont.load_default()
    for name in ('BarlowCondensed-Bold.ttf', 'segoeuib.ttf', 'arialbd.ttf'):
        path = os.path.join(r'C:\Windows\Fonts', name)
        if os.path.exists(path):
            font = ImageFont.truetype(path, 22)
            break
    d = ImageDraw.Draw(hdr)
    title = 'IMP WELD PHOTOS'
    bx = d.textbbox((0, 0), title, font=font)
    d.text((12 + lw + 10 - bx[0], (H - (bx[3] - bx[1])) // 2 - bx[1]), title,
           font=font, fill=WHITE)
    hdr.convert('RGB').save(out('samples', 'header-preview.png'))
    print('header preview')


if __name__ == '__main__':
    make_logos()
    make_icon()
    make_pattern()
    make_photos()
    make_stamp_preview()
    make_header_preview()
    print('done ->', HERE)
