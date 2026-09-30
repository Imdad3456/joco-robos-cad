"""FRCDesignLib adapter: search the public FRCDesignLib catalog and export parts through Onshape.

Students' add-ins only talk to this server. The Onshape API key lives in the container's
environment (from ~/server/joco-cad/secrets/onshape.env on the Deck) and is never returned,
logged, or written anywhere else. Exports are cached and rate limited because Onshape
meters API calls (2,500 per year on free/education plans).
"""
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from email.utils import formatdate

# Overridable so tests can use a fake catalog instead of the live site.
CATALOG_BASE = os.environ.get('JOCO_FRC_CATALOG', 'https://app.frcdesign.org/api')
LIBRARY_ID = 'frc-design-lib'
ONSHAPE = 'https://cad.onshape.com'
CACHE = os.environ.get('JOCO_FRC_CACHE', '/var/lib/svn/.frcdesign')  # Dot folder: not a repository, not backed up.
REGISTRY = os.path.join(os.environ.get('JOCO_CONFIG', '/etc/joco'), 'frcdesign.json')  # Backed up with the config.
USER_AGENT = 'JOCO-ROBOS-CAD (FRC 5919; +https://github.com/Imdad3456/joco-robos-cad)'
CATALOG_TTL = 3600
DAILY_EXPORTS = int(os.environ.get('JOCO_FRC_DAILY_EXPORTS', '40'))
USER_DAILY_EXPORTS = int(os.environ.get('JOCO_FRC_USER_DAILY_EXPORTS', '12'))
RESERVATION_MINUTES = 20
# Onshape meters successful API calls per year (2,500 on Free/EDU). Stop a little early to keep a margin.
ANNUAL_CALLS = int(os.environ.get('JOCO_ONSHAPE_ANNUAL_CALLS', '2300'))
CALLS_PER_EXPORT = {'PARTSTUDIO': 2, 'ASSEMBLY': 10}  # Worst case, checked before starting an export.
_lock = threading.Lock()
_registry_lock = threading.RLock()  # Every read-modify-write of frcdesign.json.
_export_locks = {}  # One lock per fingerprint: the same part is never exported twice at once.


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    # A followed redirect would reuse a signature made for the old URL; export_part_studio re-signs instead.
    def redirect_request(self, *args):
        return None


_onshape_opener = urllib.request.build_opener(_NoRedirect)
_catalog = {'loaded': 0, 'version': None, 'data': None}


class FrcError(Exception):
    """Shown to the student: short and free of Onshape details."""
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


def _get(url, timeout=30):
    request = urllib.request.Request(url, headers={'User-Agent': USER_AGENT, 'Accept': 'application/json, image/*'})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read(), response.headers.get('Content-Type', '')


# ---------- catalog ----------

def catalog():
    """The FRCDesignLib catalog, refreshed at most hourly and only when its version changes."""
    with _lock:
        if _catalog['data'] is not None and time.time() - _catalog['loaded'] < CATALOG_TTL:
            return _catalog['data']
        os.makedirs(CACHE, exist_ok=True)
        path = os.path.join(CACHE, 'catalog.json')
        try:
            version = json.loads(_get(CATALOG_BASE + '/library-version/library/' + LIBRARY_ID)[0])['version']
            if version != _catalog['version']:
                raw = _get('%s/library-data/library/%s?v=%s' % (CATALOG_BASE, LIBRARY_ID, version), 60)[0]
                data = json.loads(raw)
                if 'insertables' not in data or 'groups' not in data:
                    raise ValueError('unexpected catalog shape')
                with open(path + '.tmp', 'wb') as handle:
                    handle.write(raw)
                os.replace(path + '.tmp', path)
                _catalog.update(version=version, data=data)
        except (OSError, ValueError, KeyError) as exc:
            if _catalog['data'] is None and os.path.exists(path):
                with open(path) as handle:
                    _catalog['data'] = json.load(handle)  # Last good copy when FRCDesign is down.
            if _catalog['data'] is None:
                raise FrcError('FRCDesignLib could not be reached. Try again later.', 503) from exc
        _catalog['loaded'] = time.time()
        return _catalog['data']


def _item(insertable_id):
    item = catalog()['insertables'].get(insertable_id)
    if not item or not item.get('isVisible', True):
        raise FrcError('That part is not in FRCDesignLib.', 404)
    return item


def _group_name(item):
    return (catalog()['groups'].get(item.get('groupId')) or {}).get('name', 'Other')


def summary(item):
    return {'id': item['id'], 'name': item['name'], 'vendor': ', '.join(item.get('vendors') or []),
            'group': _group_name(item), 'configurable': bool(item.get('isConfigurable')),
            'kind': 'assembly' if item.get('elementType') == 'ASSEMBLY' else 'part'}


def search(query, limit=40):
    words = [w for w in re.split(r'\W+', (query or '').lower()) if w]
    results = []
    for item in catalog()['insertables'].values():
        if not item.get('isVisible', True):
            continue
        name, vendors, group = item['name'].lower(), ' '.join(item.get('vendors') or []).lower(), _group_name(item).lower()
        text = ' '.join((name, vendors, group))
        if words and not all(w in text for w in words):
            continue
        # Name matches first, then vendor/group; shorter names are usually the base part.
        score = sum(3 if w in name else 1 for w in words) - len(name) / 1000.0
        results.append((score, item))
    results.sort(key=lambda pair: (-pair[0], pair[1]['name']))
    return [summary(item) for _, item in results[:limit]]


def details(insertable_id):
    item = _item(insertable_id)
    result = summary(item)
    # Fetched for every item (cached): non-configurable parts have no parameters but still carry their part number.
    raw = _cached('config-%s-%s.json' % (insertable_id, item['microversionId']),
                  lambda: _get('%s/configuration/insertable/%s?v=%s' % (CATALOG_BASE, insertable_id, item['microversionId']))[0])
    config = json.loads(raw)
    # "Cosmetic" in FRCDesignLib means it doesn't change the part number, not that it doesn't matter: a tube's length is cosmetic.
    result['parameters'] = list(config.get('parameters', [])) if item.get('isConfigurable') else []
    result['choices'] = [_choice(p) for p in result['parameters']]
    records = config.get('records') or []
    result['partNumber'] = next((r.get('partNumber') for r in records if r.get('partNumber')), '')
    return result


def _condition(node):
    """JOCO's shape for FRCDesignApp visibility rules; values always strings. None means always shown."""
    if not node:
        return None
    kind = node.get('type')
    if kind == 'logical':
        children = [c for c in (_condition(child) for child in node.get('children', [])) if c]
        if not children:
            return None  # FRCDesignApp: an empty rule never hides anything.
        return {'mode': 'all' if node.get('operation') == 'AND' else 'any', 'children': children}
    if kind == 'equal':
        return {'mode': 'equals', 'id': str(node.get('id')), 'value': str(node.get('value'))}
    if kind == 'range':
        return {'mode': 'range', 'id': str(node.get('id')), 'start': str(node.get('start')), 'end': str(node.get('end'))}
    return None  # ALWAYS_SHOWN and anything newer.


def _option_rules(parameter):
    """Per-option rules: [{options: [ids], visibleWhen: condition}]. Options no rule names are always offered."""
    ids = [str(o['id']) for o in parameter.get('options', [])]
    rules = []
    for rule in parameter.get('optionConditions') or []:
        condition = _condition(rule.get('condition'))
        if condition is None:
            continue
        if rule.get('type') == 'list':
            controlled = [str(x) for x in rule.get('controlledOptions', [])]
        elif rule.get('type') == 'range' and str(rule.get('start')) in ids and str(rule.get('end')) in ids:
            controlled = ids[ids.index(str(rule['start'])):ids.index(str(rule['end'])) + 1]
        else:
            continue
        rules.append({'options': controlled, 'visibleWhen': condition})
    return rules


# Number options come in the parameter's own unit (inch for FRCDesignLib); Onshape gets lengths in meters,
# written the way FRCDesignLib writes defaults ("0.0254 m").
METERS_PER = {'inch': 0.0254, 'in': 0.0254, 'foot': 0.3048, 'ft': 0.3048, 'millimeter': 0.001, 'mm': 0.001,
              'centimeter': 0.01, 'cm': 0.01, 'meter': 1.0, 'm': 1.0}
SHORT_UNIT = {'inch': 'in', 'foot': 'ft', 'millimeter': 'mm', 'centimeter': 'cm', 'meter': 'm'}


def _plain(number, digits=6):
    text = ('%.*f' % (digits, number)).rstrip('0').rstrip('.')
    return '0' if text in ('', '-0') else text


def _number(parameter):
    """(kind, meters per unit) for an editable number option, or None to keep it at its default."""
    if parameter.get('type') != 'quantity' or not isinstance(parameter.get('defaultValue'), (int, float)):
        return None
    kind = parameter.get('quantityType')
    if kind == 'LENGTH' and parameter.get('unit') in METERS_PER and str(parameter.get('default', '')).endswith(' m'):
        return 'length', METERS_PER[parameter['unit']]
    if kind in ('INTEGER', 'REAL'):
        return kind.lower(), None
    return None


def _number_value(parameter, text):
    """The Onshape value for a number typed in the parameter's unit; the default keeps FRCDesignLib's exact text."""
    kind, factor = _number(parameter)
    name = parameter.get('name', parameter['id'])
    try:
        value = float(str(text).strip().replace(',', '.'))
    except ValueError:
        raise FrcError('%s must be a number.' % name)
    if value != value or value in (float('inf'), float('-inf')):
        raise FrcError('%s must be a number.' % name)
    low, high = parameter.get('min'), parameter.get('max')
    unit = ' ' + SHORT_UNIT.get(parameter.get('unit'), parameter.get('unit') or '') if kind == 'length' else ''
    if (isinstance(low, (int, float)) and value < low - 1e-9) or (isinstance(high, (int, float)) and value > high + 1e-9):
        raise FrcError('%s must be between %s and %s%s.' % (name, _plain(low), _plain(high), unit))
    if kind == 'integer' and value != int(value):
        raise FrcError('%s must be a whole number.' % name)
    if abs(value - parameter['defaultValue']) < 1e-9:
        return str(parameter.get('default'))
    if kind == 'length':
        return _plain(value * factor, 10) + ' m'
    return str(int(value)) if kind == 'integer' else _plain(value)


def _number_label(parameter, value):
    """A configuration value back in the parameter's unit, for file names: 2.5in."""
    kind, factor = _number(parameter)
    if kind == 'length':
        return _plain(float(str(value).split()[0]) / factor, 4) + SHORT_UNIT.get(parameter['unit'], parameter['unit'])
    return str(value)


def _choice(parameter):
    """What the add-in shows: dropdowns, checkboxes, and numbers are editable; other kinds keep their default for now."""
    kind = parameter.get('type')
    number = _number(parameter)
    choice = {'id': str(parameter['id']), 'name': str(parameter.get('name', parameter['id'])),
              'kind': kind if kind in ('enum', 'boolean') else 'number' if number else 'fixed',
              'default': str(parameter.get('default', '')),
              'options': [{'id': str(o['id']), 'name': str(o.get('name', o['id']))} for o in parameter.get('options', [])],
              'visibleWhen': _condition(parameter.get('condition')),
              'optionRules': _option_rules(parameter)}
    if number:
        # The add-in works in the parameter's unit; the server converts.
        choice.update({'default': _plain(parameter['defaultValue']), 'integer': number[0] == 'integer',
                       'unit': SHORT_UNIT.get(parameter.get('unit'), parameter.get('unit') or '') if number[0] == 'length' else '',
                       'min': _plain(parameter['min']) if isinstance(parameter.get('min'), (int, float)) else '',
                       'max': _plain(parameter['max']) if isinstance(parameter.get('max'), (int, float)) else ''})
    return choice


def thumbnail(insertable_id, size='300x300'):
    item = _item(insertable_id)
    url = item.get('largeThumbnailUrl' if size == '300x300' else 'smallThumbnailUrl')
    if not url:
        raise FrcError('No picture.', 404)
    data = _cached('thumb-%s-%s-%s' % (item['elementId'], item['microversionId'], size),
                   lambda: _get(CATALOG_BASE.rsplit('/api', 1)[0] + url)[0])
    return data, 'image/gif' if data[:3] == b'GIF' else 'image/png' if data[:4] == b'\x89PNG' else 'image/jpeg'


def _cached(name, fetch):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, re.sub(r'[^A-Za-z0-9._-]', '_', name))
    if os.path.exists(path):
        with open(path, 'rb') as handle:
            return handle.read()
    try:
        data = fetch()
    except (OSError, urllib.error.HTTPError) as exc:
        raise FrcError('FRCDesignLib could not be reached. Try again later.', 503) from exc
    with open(path + '.tmp', 'wb') as handle:
        handle.write(data)
    os.replace(path + '.tmp', path)
    return data


# ---------- configurations ----------

def holds(condition, chosen, choices):
    """Same rules as FRCDesignApp's evaluateCondition, on JOCO-shaped conditions."""
    if condition is None:
        return True
    mode = condition['mode']
    if mode == 'all':
        return all(holds(c, chosen, choices) for c in condition['children'])
    if mode == 'any':
        return any(holds(c, chosen, choices) for c in condition['children'])
    if mode == 'equals':
        return chosen.get(condition['id']) == condition['value']
    if mode == 'range':
        target = next((c for c in choices if c['id'] == condition['id'] and c['kind'] == 'enum'), None)
        if target is None:
            return True
        ids = [o['id'] for o in target['options']]
        if condition['start'] not in ids or condition['end'] not in ids:
            return True
        return chosen.get(condition['id']) in ids[ids.index(condition['start']):ids.index(condition['end']) + 1]
    return True


def visible_options(choice, chosen, choices):
    controlled, shown = set(), set()
    for rule in choice.get('optionRules', []):
        ok = holds(rule['visibleWhen'], chosen, choices)
        for option in rule['options']:
            controlled.add(option)
            if ok:
                shown.add(option)
    return [o for o in choice['options'] if o['id'] not in controlled or o['id'] in shown]


def normalize_configuration(parameters, requested):
    """
    The configuration FRCDesignLib itself would export: each visible parameter in order, a visible option
    (the requested one, else the default, else the first, like FRCDesignApp's resolveSelectedOption),
    hidden parameters left out. Values the add-in can't edit yet must stay at their default.
    """
    choices = [_choice(p) for p in parameters]
    raw = {str(p['id']): p for p in parameters}
    chosen = {}
    for _ in range(len(choices) + 1):  # Visibility can depend on later choices; settle to a fixed point.
        previous = dict(chosen)
        chosen = {}
        for choice in choices:
            kind, cid = choice['kind'], choice['id']
            value = str(requested.get(cid, previous.get(cid, choice['default'])))
            if kind == 'enum':
                options = [o['id'] for o in visible_options(choice, dict(previous, **chosen), choices)]
                if not options:
                    continue
                if cid in requested and str(requested[cid]) not in [o['id'] for o in choice['options']]:
                    raise FrcError('Invalid choice for %s.' % choice['name'])
                chosen[cid] = value if value in options else (choice['default'] if choice['default'] in options else options[0])
            elif kind == 'boolean':
                chosen[cid] = 'true' if value.lower() in ('true', '1', 'yes') else 'false'
            elif kind == 'number':
                chosen[cid] = _number_value(raw[cid], requested[cid]) if cid in requested else str(raw[cid].get('default'))
            else:
                if cid in requested and str(requested[cid]) != choice['default']:
                    raise FrcError('%s can only use its default value for now.' % choice['name'])
                chosen[cid] = choice['default']
        chosen = {cid: v for cid, v in chosen.items()
                  if holds(next(c for c in choices if c['id'] == cid)['visibleWhen'], chosen, choices)}
        # A cosmetic option at its default isn't sent, exactly as before they were editable, so earlier imports keep their fingerprints.
        chosen = {cid: v for cid, v in chosen.items() if not (raw[cid].get('isCosmetic') and v == str(raw[cid].get('default')))}
        if chosen == previous:
            break
    return chosen


def fingerprint(item, configuration):
    text = 'frcdesign|%s|%s|%s' % (item['id'], item['microversionId'],
                                  '|'.join('%s=%s' % pair for pair in sorted(configuration.items())))
    return hashlib.sha256(text.encode()).hexdigest()[:24]


def library_path(item, parameters, configuration, fp):
    """Library/FRCDesignLib/<group>/<name> [choices].SLDPRT — readable, unique per configuration, and short
    enough that the robot copy stays well under Windows' 260-character path limit."""
    labels = []
    for parameter in parameters:
        value = configuration.get(parameter['id'])
        if value is None or str(value) == str(parameter.get('default')):
            continue
        if parameter.get('type') == 'enum':
            labels.append(next((o['name'] for o in parameter['options'] if str(o['id']) == value), value))
        elif parameter.get('type') == 'boolean':
            labels.append(('' if value == 'true' else 'no ') + parameter.get('name', ''))
        elif _number(parameter):
            labels.append('%s %s' % (parameter.get('name', ''), _number_label(parameter, value)))
    clean = lambda text, limit, keep='': re.sub(r'\s+', ' ', re.sub(r'[^A-Za-z0-9 _().&+,%s-]' % keep, '-', text)).strip(' .-')[:limit].strip()
    name = clean(item['name'], 60) + (' (' + clean(', '.join(labels), 40, '.') + ')' if labels else '')
    if len(name) > 72:
        name = name[:64].rstrip() + ' ' + fp[:6]
    # Assemblies are flattened on export, so every FRCDesignLib item becomes one part file.
    return 'FRCDesignLib/%s/%s.SLDPRT' % (clean(_group_name(item), 40), name)


def _keys():
    access, secret = os.environ.get('ONSHAPE_ACCESS_KEY', ''), os.environ.get('ONSHAPE_SECRET_KEY', '')
    if not access or not secret:
        raise FrcError('FRCDesignLib import is not set up on the server yet. Ask a mentor.', 503)
    return access, secret


def onshape(method, path, query=None, accept='application/json', timeout=120, base=ONSHAPE):
    """One Onshape API request with HMAC request signing. Returns (status, body, headers)."""
    access, secret = _keys()
    query_string = urllib.parse.urlencode(query or {}, quote_via=urllib.parse.quote)
    nonce = secrets.token_hex(12)
    date = formatdate(usegmt=True)
    content_type = 'application/json'
    string = (method + '\n' + nonce + '\n' + date + '\n' + content_type + '\n' + path + '\n' + query_string + '\n').lower()
    signature = base64.b64encode(hmac.new(secret.encode(), string.encode(), hashlib.sha256).digest()).decode()
    request = urllib.request.Request(base + path + ('?' + query_string if query_string else ''), method=method, headers={
        'Date': date, 'On-Nonce': nonce, 'Content-Type': content_type, 'Accept': accept, 'User-Agent': USER_AGENT,
        'Authorization': 'On %s:HmacSHA256:%s' % (access, signature)})
    return _send(request, timeout)


def _send(request, timeout):
    try:
        with _onshape_opener.open(request, timeout=timeout) as response:
            status, body, headers = response.status, response.read(), response.headers
    except urllib.error.HTTPError as exc:
        status, body, headers = exc.code, exc.read()[:2000], exc.headers
    if 200 <= status < 400:
        _count_call()  # Onshape bills 2xx and 3xx responses.
    return status, body, headers


def _count_call():
    with _registry_lock:
        registry = _registry()
        calls = registry.setdefault('onshapeCalls', {})
        year, day = time.strftime('%Y', time.gmtime()), time.strftime('%Y-%m-%d', time.gmtime())
        calls[year] = calls.get(year, 0) + 1
        days = calls.setdefault('days', {})
        days[day] = days.get(day, 0) + 1
        for old in sorted(days)[:-60]:
            del days[old]
        _save(registry)


def calls_this_year():
    return _registry().get('onshapeCalls', {}).get(time.strftime('%Y', time.gmtime()), 0)


def configuration_string(configuration):
    return ';'.join('%s=%s' % (pid, value) for pid, value in sorted(configuration.items()))


def export_part_studio(item, configuration):
    """Parasolid for a Part Studio in one synchronous call; SOLIDWORKS imports it natively."""
    path = '/api/v10/partstudios/d/%s/v/%s/e/%s/parasolid' % (item['documentId'], item['versionId'], item['elementId'])
    query = {'configuration': configuration_string(configuration)} if configuration else {}
    status, body, headers = onshape('GET', path, query, accept='application/octet-stream', timeout=180)
    for _ in range(3):
        # Onshape redirects exports to a regional server (for example cad-usw2.onshape.com); only follow Onshape hosts.
        if status in (301, 302, 303, 307) and headers.get('Location'):
            location = urllib.parse.urlsplit(headers['Location'])
            if location.scheme != 'https' or not (location.hostname or '').endswith('.onshape.com'):
                raise FrcError('Could not prepare %s (unexpected redirect). Nothing was inserted.' % item['name'], 502)
            status, body, headers = onshape('GET', location.path, dict(urllib.parse.parse_qsl(location.query, keep_blank_values=True)),
                                            accept='application/octet-stream', timeout=180, base='https://' + location.netloc)
    if status == 429:
        raise FrcError('Onshape is busy right now. Try again in a few minutes.', 503)
    if status != 200 or not body:
        raise FrcError('Could not prepare %s (Onshape %d). Nothing was inserted.' % (item['name'], status), 502)
    return body


def export_assembly(item, configuration):
    """An assembly flattened into one multi-body Parasolid: one SOLIDWORKS part, no generic sub-part names."""
    body = json.dumps({'formatName': 'PARASOLID', 'flattenAssemblies': True, 'storeInDocument': False,
                       'configuration': configuration_string(configuration)}).encode()
    status, raw, _ = onshape_json('POST', '/api/v10/assemblies/d/%s/v/%s/e/%s/translations' % (
        item['documentId'], item['versionId'], item['elementId']), body)
    if status != 200:
        raise FrcError('Could not prepare %s (Onshape %d). Nothing was inserted.' % (item['name'], status), 502)
    translation = json.loads(raw)
    # Each successful poll counts against Onshape's yearly allowance, so wait between checks.
    for delay in (4, 6, 8, 10, 15, 20, 30, 30):
        if translation.get('requestState') != 'ACTIVE':
            break
        time.sleep(delay)
        status, raw, _ = onshape('GET', '/api/v10/translations/' + translation['id'])
        if status != 200:
            raise FrcError('Could not prepare %s (Onshape %d). Nothing was inserted.' % (item['name'], status), 502)
        translation = json.loads(raw)
    if translation.get('requestState') != 'DONE' or not translation.get('resultExternalDataIds'):
        raise FrcError('Onshape could not prepare %s (%s). Nothing was inserted.' % (
            item['name'], translation.get('failureReason') or translation.get('requestState', 'timed out')), 502)
    status, data, headers = onshape('GET', '/api/v10/documents/d/%s/externaldata/%s' % (
        translation.get('resultDocumentId') or item['documentId'], translation['resultExternalDataIds'][0]), accept='application/octet-stream', timeout=180)
    if status != 200 or not data:
        raise FrcError('Could not download %s (Onshape %d). Nothing was inserted.' % (item['name'], status), 502)
    return data


def onshape_json(method, path, body):
    """Signed request with a JSON body."""
    access, secret = _keys()
    nonce, date, content_type = secrets.token_hex(12), formatdate(usegmt=True), 'application/json'
    string = (method + '\n' + nonce + '\n' + date + '\n' + content_type + '\n' + path + '\n\n').lower()
    signature = base64.b64encode(hmac.new(secret.encode(), string.encode(), hashlib.sha256).digest()).decode()
    request = urllib.request.Request(ONSHAPE + path, data=body, method=method, headers={
        'Date': date, 'On-Nonce': nonce, 'Content-Type': content_type, 'Accept': 'application/json', 'User-Agent': USER_AGENT,
        'Authorization': 'On %s:HmacSHA256:%s' % (access, signature)})
    return _send(request, 120)


# ---------- export cache, reservations, and rate limits ----------

def _registry():
    try:
        with open(REGISTRY) as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return {'imports': {}, 'exports': []}


def _save(registry):
    with open(REGISTRY + '.tmp', 'w') as handle:
        json.dump(registry, handle, indent=1)
    os.replace(REGISTRY + '.tmp', REGISTRY)


def library_has(path):
    """True if the Library repository already contains this file (committed)."""
    from subprocess import run as sh
    result = sh(['svnlook', 'filesize', os.path.join(os.environ.get('JOCO_REPOS', '/var/lib/svn'), 'Library'), path], capture_output=True)
    return result.returncode == 0


def _entry(fp, token):
    """The reservation this caller holds; the token ties it to one claim on one computer."""
    entry = _registry()['imports'].get(fp)
    if not entry or entry.get('status') != 'importing' or not token or not hmac.compare_digest(entry.get('token', ''), token):
        raise FrcError('This import is no longer reserved for you. Click Insert again.', 409)
    return entry


def claim(user, insertable_id, requested, client_id=''):
    """
    ready    → the Library already has this exact part and configuration: use libraryPath.
    yours    → this computer prepares it (download with the token, save natively at libraryPath, submit, complete).
    busy     → someone (or this account on another computer) is preparing it right now.
    """
    item = _item(insertable_id)
    info = details(insertable_id)
    configuration = normalize_configuration(info['parameters'], requested or {})
    fp = fingerprint(item, configuration)
    now = time.time()
    with _registry_lock:
        registry = _registry()
        entry = registry['imports'].get(fp)
        # Self-healing: if the file reached the Library (even when "complete" never arrived), it's ready.
        if entry and library_has(entry['libraryPath']):
            if entry.get('status') != 'ready':
                entry.update(status='ready', completedAt=entry.get('completedAt') or now)
                entry.pop('token', None)
                _save(registry)
            return {'status': 'ready', 'fingerprint': fp, 'libraryPath': entry['libraryPath'], 'name': entry['name']}
        fresh = entry and entry.get('status') == 'importing' and now - entry.get('at', 0) < RESERVATION_MINUTES * 60
        if fresh and (entry.get('by') != user or entry.get('client') != client_id):
            by = entry['by'] + (' on another computer' if entry.get('by') == user else '')
            return {'status': 'busy', 'by': by, 'name': entry['name']}
        path = library_path(item, info['parameters'], configuration, fp)
        taken = [e for k, e in registry['imports'].items() if k != fp and e.get('libraryPath', '').lower() == path.lower()]
        if taken:
            path = path.rsplit('.', 1)[0] + ' ' + fp[:6] + '.' + path.rsplit('.', 1)[1]
        if not entry and library_has(path):
            # Registry lost but the file is there (for example restored from a backup).
            registry['imports'][fp] = {'status': 'ready', 'libraryPath': path, 'name': os.path.splitext(os.path.basename(path))[0],
                                       'insertable': item['id'], 'configuration': configuration, 'completedAt': now}
            _save(registry)
            return {'status': 'ready', 'fingerprint': fp, 'libraryPath': path, 'name': registry['imports'][fp]['name']}
        token = secrets.token_hex(16)
        registry['imports'][fp] = {
            'status': 'importing', 'by': user, 'client': client_id, 'token': token, 'at': now,
            'name': os.path.splitext(os.path.basename(path))[0],
            'insertable': item['id'], 'documentId': item['documentId'], 'versionId': item['versionId'],
            'microversionId': item['microversionId'], 'elementId': item['elementId'], 'elementType': item.get('elementType'),
            'configuration': configuration, 'vendor': ', '.join(item.get('vendors') or []), 'partNumber': info.get('partNumber', ''),
            'group': _group_name(item), 'libraryPath': path}
        _save(registry)
        return {'status': 'yours', 'fingerprint': fp, 'token': token, 'libraryPath': path, 'name': registry['imports'][fp]['name']}


def download(user, fp, token):
    """The neutral file for a reserved import: from cache, or one Onshape export within the limits."""
    entry = _entry(fp, token)
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, 'export-%s.x_t' % fp)
    with _registry_lock:
        export_lock = _export_locks.setdefault(fp, threading.Lock())
    with export_lock:  # A retry waits for a running export of the same part, then reuses its file.
        if not os.path.exists(path):
            _export(user, fp, entry, path)
    with open(path, 'rb') as handle:
        return handle.read(), entry['name'] + '.x_t'


def _export(user, fp, entry, path):
    """One Onshape export within the daily and yearly limits; callers hold the fingerprint's export lock."""
    with _registry_lock:
        registry = _registry()
        day = time.strftime('%Y-%m-%d', time.gmtime())
        today = [e for e in registry.get('exports', []) if e.get('day') == day]
        if len(today) >= DAILY_EXPORTS:
            raise FrcError('The team has used today\'s FRCDesignLib imports. Try again tomorrow, or ask a mentor.', 429)
        if sum(1 for e in today if e.get('by') == user) >= USER_DAILY_EXPORTS:
            raise FrcError('You\'ve imported a lot of new parts today. Try again tomorrow.', 429)
        if calls_this_year() + CALLS_PER_EXPORT.get(entry.get('elementType'), 10) > ANNUAL_CALLS:
            raise FrcError('The team\'s Onshape allowance for this year is almost used up, so new FRCDesignLib parts can\'t be '
                           'imported. Parts already in the team Library still work. Ask a mentor.', 429)
    item = _item(entry['insertable'])
    export = export_assembly if item.get('elementType') == 'ASSEMBLY' else export_part_studio
    data = export(item, entry['configuration'])  # Slow: outside the registry lock.
    fd, temp = tempfile.mkstemp(dir=CACHE, prefix='export-', suffix='.part')  # Unique per export.
    with os.fdopen(fd, 'wb') as handle:
        handle.write(data)
    os.replace(temp, path)
    with _registry_lock:
        registry = _registry()
        registry.setdefault('exports', []).append({'day': time.strftime('%Y-%m-%d', time.gmtime()), 'by': user, 'fingerprint': fp})
        registry['exports'] = registry['exports'][-500:]
        if fp in registry['imports']:
            registry['imports'][fp]['neutralSha256'] = hashlib.sha256(data).hexdigest()
        _save(registry)


def complete(user, fp, token=''):
    with _registry_lock:
        registry = _registry()
        entry = registry['imports'].get(fp)
        if entry and entry.get('status') == 'ready':
            return {'status': 'ready', 'libraryPath': entry['libraryPath']}  # Already healed by a later claim.
        _entry(fp, token)
        if not library_has(entry['libraryPath']):
            raise FrcError('The Library does not have %s yet. Submit it first.' % entry['libraryPath'], 409)
        entry.update(status='ready', completedAt=time.time())
        entry.pop('token', None)
        _save(registry)
        return {'status': 'ready', 'libraryPath': entry['libraryPath']}


def abandon(user, fp, token=''):
    with _registry_lock:
        registry = _registry()
        entry = registry['imports'].get(fp)
        if entry and entry.get('status') == 'importing' and token and hmac.compare_digest(entry.get('token', ''), token):
            del registry['imports'][fp]
            _save(registry)
    return {'status': 'abandoned'}


def recent(limit=15):
    entries = [e for e in _registry()['imports'].values() if e.get('status') == 'ready']
    return sorted(entries, key=lambda e: -e.get('completedAt', 0))[:limit]
