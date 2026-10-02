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


def sprocket():
    image, d = canvas()
    # Pointed teeth with round roller seats, and a chain link at the top.
    points = []
    for k in range(16):
        a = 2 * math.pi * k / 16
        for frac, r in ((0.0, 92), (0.5, 120)):
            t = a + frac * 2 * math.pi / 16
            points.append((128 + r * math.cos(t), 140 + r * math.sin(t)))
    d.polygon(points, fill=BLUE, outline=NAVY, width=W - 2)
    d.ellipse((80, 92, 176, 188), outline=LIGHT, width=10)
    d.polygon(hexagon(128, 140, 26), fill=WHITE, outline=NAVY, width=W - 4)
    return image


def pulley():
    image, d = canvas()
    # Side view: flanges and the toothed middle.
    d.rounded_rectangle((40, 20, 76, 236), 10, fill=LIGHT, outline=NAVY, width=W - 4)
    d.rounded_rectangle((180, 20, 216, 236), 10, fill=LIGHT, outline=NAVY, width=W - 4)
    d.rectangle((76, 40, 180, 216), fill=BLUE, outline=NAVY, width=W - 4)
    for y in range(52, 212, 26):
        d.line((76, y, 180, y), fill=NAVY, width=7)
    return image


def shaft():
    image, d = canvas()
    # A hex shaft with turned ends, at an angle.
    d.polygon([(40, 186), (176, 50), (208, 82), (72, 218)], fill=BLUE, outline=NAVY, width=W - 2)
    d.line((56, 202, 192, 66), fill=LIGHT, width=8)
    d.polygon([(14, 212), (40, 186), (72, 218), (46, 244)], fill=GRAY, outline=NAVY, width=8)
    d.polygon([(176, 50), (204, 22), (236, 54), (208, 82)], fill=GRAY, outline=NAVY, width=8)
    return image


def planetary():
    image, d = canvas()
    d.ellipse((10, 10, 246, 246), fill=GRAY, outline=NAVY, width=W)
    d.ellipse((34, 34, 222, 222), fill=WHITE, outline=NAVY, width=8)
    d.polygon(gear_points(128, 128, 10, 30, 42), fill=BLUE, outline=NAVY, width=6)
    for k in range(3):
        a = -math.pi / 2 + 2 * math.pi * k / 3
        cx, cy = 128 + 66 * math.cos(a), 128 + 66 * math.sin(a)
        d.polygon(gear_points(cx, cy, 9, 18, 28), fill=LIGHT, outline=NAVY, width=6)
    return image


def gear_ratio():
    image, d = canvas()
    d.polygon(gear_points(92, 150, 16, 64, 86), fill=BLUE, outline=NAVY, width=W - 4)
    d.polygon(gear_points(196, 76, 9, 30, 46), fill=LIGHT, outline=NAVY, width=W - 4)
    d.ellipse((76, 134, 108, 166), fill=WHITE, outline=NAVY, width=6)
    d.ellipse((184, 64, 208, 88), fill=WHITE, outline=NAVY, width=6)
    return image


def connector():
    image, d = canvas()
    # A plug with two pins and a wire leaving it.
    d.rounded_rectangle((28, 70, 160, 186), 18, fill=BLUE, outline=NAVY, width=W)
    for y in (100, 156):
        d.rectangle((160, y - 10, 228, y + 10), fill=GRAY, outline=NAVY, width=6)
    d.line((28, 128, 4, 128), fill=NAVY, width=W)
    return image


def wire():
    image, d = canvas()
    # A routed wire between two points, curving.
    d.line([(30, 214), (60, 150), (110, 140), (150, 110), (196, 100), (226, 42)], fill=NAVY, width=26, joint='curve')
    d.line([(30, 214), (60, 150), (110, 140), (150, 110), (196, 100), (226, 42)], fill=BLUE, width=12, joint='curve')
    for x, y in ((30, 214), (226, 42)):
        d.ellipse((x - 20, y - 20, x + 20, y + 20), fill=LIGHT, outline=NAVY, width=8)
    return image


def zip_tie():
    image, d = canvas()
    d.ellipse((50, 50, 206, 206), outline=NAVY, width=W + 8)
    d.ellipse((58, 58, 198, 198), outline=GRAY, width=12)
    d.rounded_rectangle((156, 104, 236, 152), 10, fill=BLUE, outline=NAVY, width=8)
    d.line((112, 20, 112, 236), fill=BLUE, width=22)
    d.line((140, 20, 140, 236), fill=LIGHT, width=22)
    return image


def harness():
    image, d = canvas()
    # Three wires gathered into one bundle.
    for y, color in ((70, BLUE), (128, LIGHT), (186, GRAY)):
        d.line([(10, y), (90, y), (140, 128)], fill=NAVY, width=24, joint='curve')
        d.line([(10, y), (90, y), (140, 128)], fill=color, width=12, joint='curve')
    d.rounded_rectangle((128, 96, 246, 160), 30, fill=BLUE, outline=NAVY, width=W)
    return image


def wiring_report():
    image, d = canvas()
    d.rounded_rectangle((32, 14, 224, 242), 16, fill=WHITE, outline=NAVY, width=W)
    for i, y in enumerate((58, 100, 142, 184)):
        d.rectangle((58, y - 10, 82, y + 10), fill=BLUE if i % 2 == 0 else LIGHT, outline=NAVY, width=4)
        d.line((98, y, 198, y), fill=NAVY, width=10)
    return image


def mounting_pattern():
    image, d = canvas()
    # A motor face: bolt circle and a center bore on a plate.
    d.rounded_rectangle((14, 14, 242, 242), 24, fill=GRAY, outline=NAVY, width=W)
    d.ellipse((60, 60, 196, 196), outline=LIGHT, width=8)
    d.ellipse((96, 96, 160, 160), fill=WHITE, outline=NAVY, width=W - 2)
    for k in range(4):
        a = math.pi / 4 + math.pi / 2 * k
        x, y = 128 + 68 * math.cos(a), 128 + 68 * math.sin(a)
        d.ellipse((x - 14, y - 14, x + 14, y + 14), fill=BLUE, outline=NAVY, width=6)
    return image


ICONS = {'sprocket': sprocket, 'pulley': pulley, 'shaft': shaft, 'planetary': planetary, 'gear-ratio': gear_ratio, 'connector': connector,
         'wire': wire, 'zip-tie': zip_tie, 'harness': harness, 'wiring-report': wiring_report, 'mounting-pattern': mounting_pattern,
         'spur-gear': gear, 'bearing-hole': bearing_hole, 'stock-part': stock_part, 'lighten-plate': lighten,
         'hole-pattern': hole_pattern, 'belt-chain': belt, 'tube-profiles': tube_profiles}

if __name__ == '__main__':
    for name, draw in ICONS.items():
        draw().save(OUT / (name + '.png'))
    print('Drew', len(ICONS), 'tool icons in', OUT)
