# Pure checks of FRCDesignLib configuration rules (no network, no container): python3 server/tests/frcdesign_test.py
# Mirrors FRCDesignApp's evaluateCondition / getVisibleOptions / resolveSelectedOption.
import os, sys, tempfile
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))
os.environ.setdefault('JOCO_CONFIG', tempfile.mkdtemp())
import frcdesign as f

n = 0
def check(condition, message):
    global n
    if not condition:
        raise SystemExit('FAIL: ' + message)
    n += 1

size = {'id': 'Size', 'name': 'Size', 'type': 'enum', 'default': 'S',
        'options': [{'id': 'S', 'name': 'Small'}, {'id': 'M', 'name': 'Medium'}, {'id': 'L', 'name': 'Large'}, {'id': 'XL', 'name': 'Extra'}]}
bore = {'id': 'Bore', 'name': 'Bore', 'type': 'enum', 'default': 'Round',
        'options': [{'id': 'Round', 'name': 'Round'}, {'id': 'Hex', 'name': 'Hex'}, {'id': 'Big', 'name': 'Big hex'}],
        # "Big hex" only for L..XL; "Hex" only when Size is M.
        'optionConditions': [{'type': 'range', 'start': 'Big', 'end': 'Big', 'condition': {'type': 'range', 'id': 'Size', 'start': 'L', 'end': 'XL'}},
                             {'type': 'list', 'controlledOptions': ['Hex'], 'condition': {'type': 'equal', 'id': 'Size', 'value': 'M'}}]}
flange = {'id': 'Flange', 'name': 'Flange', 'type': 'boolean', 'default': 'true',
          'condition': {'type': 'logical', 'operation': 'OR', 'children': [{'type': 'equal', 'id': 'Size', 'value': 'M'}, {'type': 'range', 'id': 'Size', 'start': 'L', 'end': 'XL'}]}}
width = {'id': 'Width', 'name': 'Width', 'type': 'quantity', 'default': '0.5 in'}
params = [size, bore, flange, width]
choices = [f._choice(p) for p in params]

check([o['id'] for o in f.visible_options(choices[1], {'Size': 'S'}, choices)] == ['Round'], 'small: only round bore')
check([o['id'] for o in f.visible_options(choices[1], {'Size': 'M'}, choices)] == ['Round', 'Hex'], 'medium: hex appears')
check([o['id'] for o in f.visible_options(choices[1], {'Size': 'XL'}, choices)] == ['Round', 'Big'], 'range rule: big hex for L..XL')
check(f.normalize_configuration(params, {}) == {'Size': 'S', 'Bore': 'Round', 'Width': '0.5 in'}, 'defaults; flange hidden for S')
check(f.normalize_configuration(params, {'Size': 'L', 'Bore': 'Big'}) == {'Size': 'L', 'Bore': 'Big', 'Flange': 'true', 'Width': '0.5 in'}, 'big hex allowed for L')
check(f.normalize_configuration(params, {'Size': 'S', 'Bore': 'Big'})['Bore'] == 'Round', 'hidden option falls back to default')
check(f.normalize_configuration(params, {'Size': 'M', 'Flange': 'false'})['Flange'] == 'false', 'visible boolean honored')
for bad in ({'Size': 'Nope'}, {'Width': '2 in'}):
    try:
        f.normalize_configuration(params, bad); check(False, 'accepted ' + str(bad))
    except f.FrcError:
        check(True, '')
check(f._condition({'type': 'logical', 'operation': 'OR', 'children': []}) is None, 'empty OR never hides (FRCDesignApp rule)')
check(f._condition({'type': 'alwaysShown'}) is None, 'always shown')
item = {'id': 'x', 'name': 'A' * 200, 'groupId': 'g', 'microversionId': 'm'}
f._catalog.update(data={'groups': {'g': {'name': 'G' * 100}}, 'insertables': {}}, loaded=1e18)
path = f.library_path(item, params, {'Size': 'XL', 'Bore': 'Big'}, 'abcdef123456')
check(len(path) <= 13 + 40 + 1 + 72 + 7 and path.endswith('.SLDPRT'), 'library path stays short: %d chars' % len(path))
# Two downloads of the same part at once (a retry while the first export is still running): one Onshape export.
import threading, time as _time
f.CACHE = tempfile.mkdtemp()
calls = []
def slow_export(item, configuration):
    calls.append(1); _time.sleep(0.3); return b'PARASOLID-data'
f.export_part_studio = slow_export
f._catalog.update(data={'groups': {'g': {'name': 'G'}}, 'insertables': {'p1': {'id': 'p1', 'name': 'Part', 'elementType': 'PARTSTUDIO', 'microversionId': 'm'}}}, loaded=1e18)
f._save({'imports': {'fp1': {'status': 'importing', 'token': 't', 'insertable': 'p1', 'configuration': {}, 'name': 'Part', 'elementType': 'PARTSTUDIO'}}, 'exports': []})
results = []
threads = [threading.Thread(target=lambda: results.append(f.download('sam', 'fp1', 't'))) for _ in range(3)]
[t.start() for t in threads]; [t.join() for t in threads]
check(len(calls) == 1 and len(results) == 3 and all(r[0] == b'PARASOLID-data' for r in results), 'same part exported once for concurrent downloads (%d exports)' % len(calls))
check(not [x for x in os.listdir(f.CACHE) if x.endswith('.part') or x.endswith('.tmp')], 'no temporary files left behind')
print('PASS: %d FRCDesignLib configuration and export checks' % n)
