"""Builds the add-in's icon files from src/JocoRobos.Cad/Icons/source/*.png (256 px, transparent).

SOLIDWORKS wants one horizontal strip per toolbar size, with icons in the order of each command's
image index (see ToolbarIcons in Addin.cs). Run after changing a source icon:  python3 tools/make-icons.py
"""
from pathlib import Path
from PIL import Image

ICONS = Path(__file__).resolve().parent.parent / 'src' / 'JocoRobos.Cad' / 'Icons'
ORDER = ['open-robot', 'update', 'edit', 'submit', 'insert-library',  # Image index 0..4: toolbar and panel.
         'sign-in', 'test-connection', 'change-password', 'release-edit', 'choose-robot',  # 5..9: Tools menu.
         'open-old-robot', 'set-aside', 'restore-deleted', 'insert-external', 'import-references',  # 10..14
         'upgrade-files', 'repair-references', 'install-update',  # 15..17
         'spur-gear', 'bearing-hole', 'stock-part', 'lighten-plate', 'hole-pattern', 'belt-chain', 'tube-profiles',  # 18..24: CAD Hub tab tools
         'sprocket', 'pulley', 'shaft', 'planetary', 'gear-ratio',  # 25..29: powertrain
         'connector', 'wire', 'zip-tie', 'harness', 'wiring-report', 'mounting-pattern']  # 30..35: electrical, mounting patterns
SIZES = [20, 32, 40, 64, 96, 128]

sources = [Image.open(ICONS / 'source' / (name + '.png')).convert('RGBA') for name in ORDER]
app = Image.open(ICONS / 'source' / 'app.png').convert('RGBA')
for size in SIZES:
    strip = Image.new('RGBA', (size * len(ORDER), size), (0, 0, 0, 0))
    for index, icon in enumerate(sources):
        strip.paste(icon.resize((size, size), Image.LANCZOS), (index * size, 0))
    strip.save(ICONS / ('toolbar_%d.png' % size))
    app.resize((size, size), Image.LANCZOS).save(ICONS / ('main_%d.png' % size))  # Tab and task pane icon: the CAD Hub logo.
for name, icon in zip(ORDER, sources):
    icon.resize((24, 24), Image.LANCZOS).save(ICONS / ('button_%s.png' % name))  # Task pane buttons.
print('Wrote', len(list(ICONS.glob('*.png'))), 'icon files to', ICONS)
