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
_lock = threading.Lock()


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
    result['parameters'] = []
    if item.get('isConfigurable'):
        raw = _cached('config-%s-%s.json' % (insertable_id, item['microversionId']),
                      lambda: _get('%s/configuration/insertable/%s?v=%s' % (CATALOG_BASE, insertable_id, item['microversionId']))[0])
        config = json.loads(raw)
        result['parameters'] = [p for p in config.get('parameters', []) if not p.get('isCosmetic')]
        result['choices'] = [_choice(p) for p in result['parameters']]
        records = config.get('records') or []
        result['partNumber'] = next((r.get('partNumber') for r in records if r.get('partNumber')), '')
    return result


def _condition(node):
    """JOCO's own shape for visibility rules: {all|any: [...]} or {id, equals}, values always strings."""
    if not node:
        return None
    if node.get('type') == 'logical':
        children = [c for c in (_condition(child) for child in node.get('children', [])) if c]
        return {'mode': 'all' if node.get('operation') == 'AND' else 'any', 'children': children}
    if node.get('type') == 'equal':
        return {'mode': 'equals', 'id': str(node.get('id')), 'value': str(node.get('value'))}
    return None


def _choice(parameter):
    """What the add-in shows: dropdowns and checkboxes are editable; other kinds keep their default for now."""
    kind = parameter.get('type')
    return {'id': str(parameter['id']), 'name': str(parameter.get('name', parameter['id'])),
            'kind': kind if kind in ('enum', 'boolean') else 'fixed',
            'default': str(parameter.get('default', '')),
            'options': [{'id': str(o['id']), 'name': str(o.get('name', o['id']))} for o in parameter.get('options', [])],
            'visibleWhen': _condition(parameter.get('condition'))}


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

def _visible(parameter, chosen):
    """Evaluates FRCDesignApp's logical/equal visibility conditions against the current choices."""
    def check(node):
        kind = node.get('type')
        if kind == 'logical':
            results = [check(child) for child in node.get('children', [])]
            return all(results) if node.get('operation') == 'AND' else any(results)
        if kind == 'equal':
            return str(chosen.get(node.get('id'))) == str(node.get('value'))
        return True
    condition = parameter.get('condition')
    return True if not condition else check(condition)


def normalize_configuration(parameters, requested):
    """Validated choices for every visible parameter, defaults filled in. Refuses types we can't export correctly."""
    chosen = {}
    for parameter in parameters:
        pid, kind = parameter['id'], parameter.get('type')
        value = requested.get(pid, parameter.get('default'))
        if kind == 'enum':
            if str(value) not in {str(o['id']) for o in parameter.get('options', [])}:
                raise FrcError('Invalid choice for %s.' % parameter.get('name', pid))
            chosen[pid] = str(value)
        elif kind == 'boolean':
            chosen[pid] = 'true' if str(value).lower() in ('true', '1', 'yes') else 'false'
        else:
            # Quantity/string parameters: only their defaults until the add-in has proper inputs for them.
            if pid in requested and str(requested[pid]) != str(parameter.get('default')):
                raise FrcError('%s can only use its default value for now.' % parameter.get('name', pid))
            chosen[pid] = str(parameter.get('default', ''))
    return {pid: value for pid, value in chosen.items()
            if _visible(next(p for p in parameters if p['id'] == pid), chosen)}


def fingerprint(item, configuration):
    text = 'frcdesign|%s|%s|%s' % (item['id'], item['microversionId'],
                                  '|'.join('%s=%s' % pair for pair in sorted(configuration.items())))
    return hashlib.sha256(text.encode()).hexdigest()[:24]


def library_path(item, parameters, configuration, fp):
    """Library\\FRCDesignLib\\<group>\\<name> [choices].SLDPRT — readable, and unique per configuration."""
    labels = []
    for parameter in parameters:
        value = configuration.get(parameter['id'])
        if value is None or str(value) == str(parameter.get('default')):
            continue
        if parameter.get('type') == 'enum':
            labels.append(next((o['name'] for o in parameter['options'] if str(o['id']) == value), value))
        elif parameter.get('type') == 'boolean':
            labels.append(('' if value == 'true' else 'no ') + parameter.get('name', ''))
    clean = lambda text: re.sub(r'\s+', ' ', re.sub(r'[^A-Za-z0-9 _().&+,-]', '-', text)).strip(' .-')[:80]
    name = clean(item['name']) + (' (' + clean(', '.join(labels)) + ')' if labels else '')
    if len(name) > 110:
        name = name[:100].rstrip() + ' ' + fp[:6]
    # Assemblies are flattened on export, so every FRCDesignLib item becomes one part file.
    return 'FRCDesignLib/%s/%s.SLDPRT' % (clean(_group_name(item)), name)


# ---------- Onshape (signed requests) ----------

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
    try:
        with _onshape_opener.open(request, timeout=timeout) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()[:2000], exc.headers


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
    try:
        with _onshape_opener.open(request, timeout=120) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()[:2000], exc.headers


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


def claim(user, insertable_id, requested):
    """
    ready    → the Library already has this exact part and configuration: use libraryPath.
    yours    → this student prepares it: download the export, save natively at libraryPath, submit, then complete().
    busy     → someone else is preparing it right now.
    """
    item = _item(insertable_id)
    info = details(insertable_id)
    configuration = normalize_configuration(info['parameters'], requested or {})
    fp = fingerprint(item, configuration)
    now = time.time()
    registry = _registry()
    entry = registry['imports'].get(fp)
    if entry and entry.get('status') == 'ready' and library_has(entry['libraryPath']):
        return {'status': 'ready', 'fingerprint': fp, 'libraryPath': entry['libraryPath'], 'name': entry['name']}
    if entry and entry.get('status') == 'importing' and entry.get('by') != user and now - entry.get('at', 0) < RESERVATION_MINUTES * 60:
        return {'status': 'busy', 'by': entry['by'], 'name': entry['name']}
    path = library_path(item, info['parameters'], configuration, fp)
    taken = [e for k, e in registry['imports'].items() if k != fp and e.get('libraryPath', '').lower() == path.lower()]
    if taken:
        path = path.rsplit('.', 1)[0] + ' ' + fp[:6] + '.' + path.rsplit('.', 1)[1]
    registry['imports'][fp] = {
        'status': 'importing', 'by': user, 'at': now, 'name': os.path.splitext(os.path.basename(path))[0],
        'insertable': item['id'], 'documentId': item['documentId'], 'versionId': item['versionId'],
        'microversionId': item['microversionId'], 'elementId': item['elementId'], 'elementType': item.get('elementType'),
        'configuration': configuration, 'vendor': ', '.join(item.get('vendors') or []), 'partNumber': info.get('partNumber', ''),
        'group': _group_name(item), 'libraryPath': path}
    _save(registry)
    return {'status': 'yours', 'fingerprint': fp, 'libraryPath': path, 'name': registry['imports'][fp]['name'],
            'download': '/admin/api/frcdesign/download/' + fp}


def download(user, fp):
    """The neutral file for a claimed import: from cache, or one Onshape export within the rate limits."""
    registry = _registry()
    entry = registry['imports'].get(fp)
    if not entry or entry.get('by') != user or entry.get('status') != 'importing':
        raise FrcError('This import is not reserved for you. Click Insert again.', 409)
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, 'export-%s.x_t' % fp)
    if not os.path.exists(path):
        day = time.strftime('%Y-%m-%d', time.gmtime())
        today = [e for e in registry.get('exports', []) if e.get('day') == day]
        if len(today) >= DAILY_EXPORTS:
            raise FrcError('The team has used today\'s FRCDesignLib imports. Try again tomorrow, or ask a mentor.', 429)
        if sum(1 for e in today if e.get('by') == user) >= USER_DAILY_EXPORTS:
            raise FrcError('You\'ve imported a lot of new parts today. Try again tomorrow.', 429)
        item = _item(entry['insertable'])
        export = export_assembly if item.get('elementType') == 'ASSEMBLY' else export_part_studio
        data = export(item, entry['configuration'])
        with open(path + '.tmp', 'wb') as handle:
            handle.write(data)
        os.replace(path + '.tmp', path)
        registry = _registry()
        registry.setdefault('exports', []).append({'day': day, 'by': user, 'fingerprint': fp})
        registry['exports'] = registry['exports'][-500:]
        registry['imports'][fp]['neutralSha256'] = hashlib.sha256(data).hexdigest()
        _save(registry)
    with open(path, 'rb') as handle:
        return handle.read(), entry['name'] + '.x_t'


def complete(user, fp):
    registry = _registry()
    entry = registry['imports'].get(fp)
    if not entry or entry.get('by') != user:
        raise FrcError('This import is not reserved for you.', 409)
    if not library_has(entry['libraryPath']):
        raise FrcError('The Library does not have %s yet. Submit it first.' % entry['libraryPath'], 409)
    entry.update(status='ready', completedAt=time.time())
    _save(registry)
    return {'status': 'ready', 'libraryPath': entry['libraryPath']}


def abandon(user, fp):
    registry = _registry()
    entry = registry['imports'].get(fp)
    if entry and entry.get('by') == user and entry.get('status') == 'importing':
        del registry['imports'][fp]
        _save(registry)
    return {'status': 'abandoned'}


def recent(limit=15):
    entries = [e for e in _registry()['imports'].values() if e.get('status') == 'ready']
    return sorted(entries, key=lambda e: -e.get('completedAt', 0))[:limit]
