"""Builds the add-in's icon files from src/JocoRobos.Cad/Icons/source/*.png (256 px, transparent).

SOLIDWORKS wants one horizontal strip per toolbar size, with icons in the order of each command's
image index (see ToolbarIcons in Addin.cs). Run after changing a source icon:  python3 tools/make-icons.py
"""
from pathlib import Path
from PIL import Image

ICONS = Path(__file__).resolve().parent.parent / 'src' / 'JocoRobos.Cad' / 'Icons'
ORDER = ['open-robot', 'update', 'edit', 'submit', 'insert-library']  # Image index 0..4.
SIZES = [20, 32, 40, 64, 96, 128]

sources = [Image.open(ICONS / 'source' / (name + '.png')).convert('RGBA') for name in ORDER]
for size in SIZES:
    strip = Image.new('RGBA', (size * len(ORDER), size), (0, 0, 0, 0))
    for index, icon in enumerate(sources):
        strip.paste(icon.resize((size, size), Image.LANCZOS), (index * size, 0))
    strip.save(ICONS / ('toolbar_%d.png' % size))
    sources[0].resize((size, size), Image.LANCZOS).save(ICONS / ('main_%d.png' % size))  # Tab and task pane icon.
for name, icon in zip(ORDER, sources):
    icon.resize((24, 24), Image.LANCZOS).save(ICONS / ('button_%s.png' % name))  # Task pane buttons.
print('Wrote', len(list(ICONS.glob('*.png'))), 'icon files to', ICONS)
