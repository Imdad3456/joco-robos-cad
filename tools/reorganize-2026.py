import os, re, shutil, csv, collections
SRC, DST = 'tree', 'reorganized/2026-Robot'
# Whole folders that are already coherent: move as units (their internal references stay intact).
FOLDERS = {
    'Climber in the box': '40_Climber/Climber in a Box',
    'Ractheting system': '40_Climber/Ratchet',
    'Coil climb': '40_Climber/Coil climb',
    'Coil drive stuff': '10_Drivetrain/Coil drive',
    'Gearbox': '90_COTS/AndyMark/3 Stage HD CIM Sport Gearbox',
    'Electronics': '50_Electrical/Electronics',
    'motor covers': '50_Electrical/Motor covers',
}
# Loose files: first matching rule wins. Vendor assemblies keep their own parts next to them.
RULES = [
    (r'^2026ASSembly4\.SLDASM$', '00_Master', 'Robot.SLDASM'),
    (r'^GamePiece2026', '00_Master', None),
    (r'^(Basic26x26swerve|Simplified MAXSwerve|MirrorSimplified MAXSwerve)', '10_Drivetrain/Swerve', None),
    (r'^(24in2x1|Tube 2x1|FRontPanel|SidePanel|sidebracket|new siding|Bumpers)', '10_Drivetrain/Frame', None),
    (r'^(am-5780|am-5796|Stealth Wheel)', '30_Shooter/Launcher in a Box', None),
    (r'^ELEvatorSLideASSembly', '60_Elevator', None),
    (r'^(battery holder|batterymount3dmount|underplate electronics)', '50_Electrical', None),
    (r'^(120A Main Breaker)', '90_COTS/Electrical/120A Main Breaker', None),
    (r'^(Robot Signal Light)', '90_COTS/Electrical/Robot Signal Light', None),
    (r'^(roboRIO)', '90_COTS/Electrical/roboRIO', None),
    (r'^(TFM-105)', '90_COTS/Electrical/Samtec TFM-105', None),
    (r'^(0\.8BTB|EVQP7|USB4110|SFSX-RL)', '90_COTS/Electrical/Connectors', None),
    (r'^(NEO-Vortex|NEOmotor)', '90_COTS/REV/Motors', None),
    (r'^REV-11-2159', '90_COTS/REV/REV-11-2159', None),
    (r'^REV-21-1652', '90_COTS/REV/REV-21-1652', None),
    (r'^REV-21-2120', '90_COTS/REV/REV-21-2120', None),
    (r'^REV-', '90_COTS/REV', None),
    (r'^ISDF-05-D-M', '90_COTS/Electrical/ISDF-05-D-M', None),
    (r'^(WCP-0940|STB0440|Mirror1_1)', '90_COTS/WCP/WCP-0940', None),
    (r'^WCP-0897', '90_COTS/WCP/WCP-0897', None),
    (r'^WCP-', '90_COTS/WCP', None),
    (r'^(am-1526|am-1238a|500Hex Collar Clamp Body)', '90_COTS/AndyMark/500Hex Collar Clamp', None),
    (r'^(4inWheel|4in Wheel Intergreated Hub|am-3945)', '90_COTS/AndyMark/Wheels', None),
    (r'^am-', '90_COTS/AndyMark', None),
    (r'^(Churro|7IN Churro)', '90_COTS/Hardware/Churro', None),
    (r'(6801ZZ|F6803ZZ|FR8ZZ|Bearing_17mm|radial_bearing)', '90_COTS/Hardware/Bearings', None),
    (r'(Button Head Cap Screws|shcs|SHCS|Screw_|FHTS|Retaining Ring|Washer)', '90_COTS/Hardware/Fasteners', None),
]
os.makedirs(DST)
moves = []
for root, _, names in os.walk(SRC):
    for name in sorted(names):
        rel = os.path.relpath(os.path.join(root, name), SRC).replace(os.sep, '/')
        top = rel.split('/')[0]
        if top in FOLDERS and '/' in rel:
            target = FOLDERS[top] + '/' + rel.split('/', 1)[1]
        else:
            for pattern, folder, rename in RULES:
                if re.search(pattern, name):
                    target = folder + '/' + (rename or name)
                    break
            else:
                target = '80_Unsorted/' + name
        moves.append((rel, target))
targets = collections.Counter(t.lower().rsplit('/', 1)[-1] for _, t in moves)
assert all(v == 1 for v in targets.values()), 'duplicate names after reorganizing'
assert len({t.lower() for _, t in moves}) == len(moves)
for src, target in moves:
    os.makedirs(os.path.dirname(os.path.join(DST, target)), exist_ok=True)
    shutil.copy2(os.path.join(SRC, src), os.path.join(DST, target))
with open('reorganized/MOVES.csv', 'w', newline='') as handle:
    writer = csv.writer(handle); writer.writerow(['old path (2026RebuiltRobotCad)', 'new path (2026-Robot)'])
    writer.writerows(moves)
counts = collections.Counter(t.split('/')[0] + ('/' + t.split('/')[1] if t.count('/') > 1 else '') for _, t in moves)
for folder, n in sorted(counts.items()): print('%4d  %s' % (n, folder))
print('total', len(moves), '| unsorted:', [t.split('/')[-1] for _, t in moves if t.startswith('80_')])
