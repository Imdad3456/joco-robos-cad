"""Draws the source icons for CAD Hub's modeling tools (Icons/source/<name>.png, 256 px) in one simple style that reads
at toolbar size: navy outlines, blue and light-gray fills. Then run make-icons.py to build the toolbar strips."""
import math
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / 'src' / 'JocoRobos.Cad' / 'Icons' / 'source'
NAVY, BLUE, LIGHT, GRAY, WHITE = (24, 44, 84, 255), (52, 130, 240, 255), (150, 200, 255, 255), (214, 221, 230, 255), (255, 255, 255, 255)
W = 14  # outline width


def canvas():
    image = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
    return image, ImageDraw.Draw(image)


def gear_points(cx, cy, teeth, r_root, r_tip):
    points = []
    for k in range(teeth):
        a = 2 * math.pi * k / teeth
        step = 2 * math.pi / teeth
        for frac, r in ((0.0, r_root), (0.15, r_tip), (0.45, r_tip), (0.6, r_root)):
            t = a + frac * step
            points.append((cx + r * math.cos(t), cy + r * math.sin(t)))
    return points


def hexagon(cx, cy, r):
    return [(cx + r * math.cos(math.pi / 3 * i), cy + r * math.sin(math.pi / 3 * i)) for i in range(6)]


def gear():
    image, d = canvas()
    d.polygon(gear_points(128, 128, 14, 88, 116), fill=BLUE, outline=NAVY, width=W)
    d.ellipse((78, 78, 178, 178), outline=LIGHT, width=10)
    d.polygon(hexagon(128, 128, 30), fill=WHITE, outline=NAVY, width=W - 4)
    return image


def bearing_hole():
    image, d = canvas()
    d.rounded_rectangle((16, 40, 240, 216), 28, fill=GRAY, outline=NAVY, width=W)
    d.ellipse((60, 60, 196, 196), fill=BLUE, outline=NAVY, width=W)
    d.ellipse((92, 92, 164, 164), fill=LIGHT, outline=NAVY, width=W - 4)
    d.polygon(hexagon(128, 128, 22), fill=WHITE, outline=NAVY, width=8)
    return image


def stock_part():
    image, d = canvas()
    # A box tube in perspective with its hole row.
    d.polygon([(30, 110), (180, 40), (236, 70), (86, 140)], fill=LIGHT, outline=NAVY, width=W)
    d.polygon([(30, 110), (86, 140), (86, 220), (30, 190)], fill=BLUE, outline=NAVY, width=W)
    d.polygon([(86, 140), (236, 70), (236, 150), (86, 220)], fill=GRAY, outline=NAVY, width=W)
    d.rectangle((44, 128, 72, 176), fill=WHITE, outline=NAVY, width=6)
    for i in range(4):
        x, y = 112 + i * 32, 158 - i * 15
        d.ellipse((x - 8, y - 8, x + 8, y + 8), fill=NAVY)
    return image


def lighten():
    image, d = canvas()
    d.rounded_rectangle((18, 30, 238, 226), 24, fill=GRAY, outline=NAVY, width=W)
    for tri in ([(46, 62), (118, 62), (46, 128)], [(138, 62), (210, 62), (210, 128)], [(46, 196), (118, 196), (46, 146)], [(210, 196), (138, 196), (210, 146)], [(70, 128), (186, 128), (128, 78)], [(70, 136), (186, 136), (128, 180)]):
        d.polygon(tri, fill=BLUE, outline=NAVY, width=8)
    for x, y in ((38, 52), (218, 52), (38, 206), (218, 206)):
        d.ellipse((x - 9, y - 9, x + 9, y + 9), fill=WHITE, outline=NAVY, width=5)
    return image


def hole_pattern():
    image, d = canvas()
    d.rounded_rectangle((12, 84, 244, 172), 14, fill=GRAY, outline=NAVY, width=W)
    for i in range(6):
        x = 38 + i * 36
        d.ellipse((x - 11, 117, x + 11, 139), fill=BLUE, outline=NAVY, width=6)
    d.line((38, 196, 74, 196), fill=NAVY, width=8)
    d.line((38, 186, 38, 206), fill=NAVY, width=8)
    d.line((74, 186, 74, 206), fill=NAVY, width=8)
    return image


def belt():
    image, d = canvas()
    # Belt around two pulleys of different sizes.
    d.polygon([(70, 40), (190, 78), (190, 178), (70, 216)], fill=LIGHT)
    d.line((70, 40, 190, 78), fill=NAVY, width=W)
    d.line((70, 216, 190, 178), fill=NAVY, width=W)
    d.ellipse((8, 40, 132, 216), fill=BLUE, outline=NAVY, width=W)
    d.ellipse((140, 78, 240, 178), fill=BLUE, outline=NAVY, width=W)
    for cx, cy, r in ((70, 128, 18), (190, 128, 14)):
        d.polygon(hexagon(cx, cy, r), fill=WHITE, outline=NAVY, width=6)
    return image


def tube_profiles():
    image, d = canvas()
    d.line((24, 220, 120, 70), fill=NAVY, width=10)
    d.line((120, 70, 232, 70), fill=NAVY, width=10)
    for x, y in ((24, 220), (120, 70), (232, 70)):
        d.ellipse((x - 12, y - 12, x + 12, y + 12), fill=BLUE, outline=NAVY, width=5)
    d.rectangle((120, 128, 232, 212), fill=BLUE, outline=NAVY, width=W)
    d.rectangle((140, 148, 212, 192), fill=WHITE, outline=NAVY, width=8)
    return image


ICONS = {'spur-gear': gear, 'bearing-hole': bearing_hole, 'stock-part': stock_part, 'lighten-plate': lighten,
         'hole-pattern': hole_pattern, 'belt-chain': belt, 'tube-profiles': tube_profiles}

if __name__ == '__main__':
    for name, draw in ICONS.items():
        draw().save(OUT / (name + '.png'))
    print('Drew', len(ICONS), 'tool icons in', OUT)
