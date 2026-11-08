"""Private Yandex Smart Home provider; stdlib only. No secrets in source."""
from collections import deque
from contextvars import ContextVar
from functools import wraps
import threading
import ipaddress
import queue
import base64
import hashlib
import hmac
import json
import logging
import os
import re
import secrets
import socket
import time
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode

CAPABILITY = 'devices.capabilities.on_off'
DEVICE_ID = 'wakemini-pc'
RESTART_DEVICE_ID = 'wakemini-restart'
SLEEP_DEVICE_ID = 'wakemini-sleep'
APP_DEVICE_ID = 'wakemini-app'
CHAT_DEVICE_ID = 'wakemini-chatgpt'
COMMANDS = {
    CHAT_DEVICE_ID: 'CHATGPT',
    RESTART_DEVICE_ID: 'RESTART',
    SLEEP_DEVICE_ID: 'SLEEP',
    APP_DEVICE_ID: 'LAUNCH',
    'wakemini-profile-work': 'PROFILE_WORK',
    'wakemini-profile-game': 'PROFILE_GAME',
    'wakemini-profile-cinema': 'PROFILE_CINEMA',
}
LOG = logging.getLogger(__name__)
_AUTH_CACHE = {}
_AUTH_CACHE_TTL_SECONDS = 20
_DNS_CACHE = {}
_DNS_LOCK = threading.Lock()
_DNS_SLOTS = threading.BoundedSemaphore(6)
_DNS_BUDGET = 0.65


def _dns_https(host, endpoint):
    request = Request(endpoint + '?' + urlencode({'name': host, 'type': 'A'}),
                      headers={'Accept': 'application/dns-json'})
    with urlopen(request, timeout=0.5) as response:
        raw = response.read(16385)
    if len(raw) > 16384:
        raise ValueError('DNS response too large')
    data = json.loads(raw)
    if data.get('Status') != 0 or data.get('TC'):
        raise ValueError('DNS response unsuccessful')
    answers = data.get('Answer', [])
    for record in answers:
        if record.get('type') == 1:
            address = ipaddress.IPv4Address(record['data'])
            if not address.is_global:
                continue
            # Respect CNAME TTL too, never retain a DDNS address beyond 30s.
            ttl = max(0, min(30, *(int(item.get('TTL', 0)) for item in answers)))
            return str(address), ttl
    raise ValueError('No public IPv4 address')


def resolve_pc(host):
    try:
        return str(ipaddress.IPv4Address(host))
    except ipaddress.AddressValueError:
        pass
    # Configured host only. Never forward the connection key or OAuth token.
    if not re.fullmatch(r'(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\.?', host):
        raise DeviceError('DEVICE_UNREACHABLE', 'WM155 DNS_CONFIG: неверный формат PC_HOST.')
    host = host.lower().rstrip('.')
    with _DNS_LOCK:
        cached = _DNS_CACHE.get(host)
        if cached and cached[0] > time.monotonic():
            return cached[1]
        _DNS_CACHE.pop(host, None)
    results = queue.Queue()
    deadline = time.monotonic() + _DNS_BUDGET

    def launch(function):
        if not _DNS_SLOTS.acquire(blocking=False):
            return False
        def run():
            try:
                results.put(('ok', function()))
            except Exception:
                results.put(('failed', None))
            finally:
                _DNS_SLOTS.release()
        threading.Thread(target=run, daemon=True).start()
        return True

    pending = int(launch(lambda: (str(ipaddress.IPv4Address(socket.gethostbyname(host))), 0)))
    # Give normal DNS 80ms; a stalled resolver must not block the command.
    if pending:
        try:
            status, result = results.get(timeout=0.08)
            pending -= 1
            if status == 'ok':
                return result[0]
        except queue.Empty:
            pass
    for endpoint in ('https://dns.google/resolve', 'https://cloudflare-dns.com/dns-query'):
        pending += int(launch(lambda endpoint=endpoint: _dns_https(host, endpoint)))
    while pending:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        try:
            status, result = results.get(timeout=remaining)
        except queue.Empty:
            break
        pending -= 1
        if status == 'ok':
            address, ttl = result
            if ttl > 0:
                with _DNS_LOCK:
                    _DNS_CACHE[host] = (time.monotonic() + ttl, address)
                    while len(_DNS_CACHE) > 32:
                        _DNS_CACHE.pop(next(iter(_DNS_CACHE)))
            return address
    raise DeviceError('DEVICE_UNREACHABLE', 'WM155 DNS: основной и резервные DNS не определили адрес ПК.')


class IdentityUnavailable(PermissionError):
    pass


class DeviceError(Exception):
    def __init__(self, code, message):
        self.code, self.message = code, message


def settings():
    required = ['PC_HOST', 'PC_MAC', 'AGENT_KEY', 'YANDEX_LOGIN', 'OAUTH_CLIENT_ID']
    values = {name: os.environ[name].strip() for name in required}
    mac = re.sub('[:-]', '', values['PC_MAC'])
    if not re.fullmatch('[0-9a-fA-F]{12}', mac):
        raise ValueError('Invalid PC_MAC')
    if not re.fullmatch('[0-9a-fA-F]{64}', values['AGENT_KEY']):
        raise ValueError('Invalid AGENT_KEY')
    port = int(os.environ.get('PC_PORT', '3000'))
    if not 1 <= port <= 65535:
        raise ValueError('Invalid PC_PORT')
    return values | {'mac': bytes.fromhex(mac), 'key': bytes.fromhex(values['AGENT_KEY']), 'port': port}


def authorize(header, cfg):
    if not isinstance(header, str) or not header.startswith('Bearer '):
        raise PermissionError('Account linking required')
    token = header[7:]
    if not token or len(token) > 2048 or any(c.isspace() for c in token):
        raise PermissionError('Invalid authorization')

    # Cache only a successful identity match for this exact token and account
    # for a short time; retain its hash, never the OAuth token itself.
    cache_key = (hashlib.sha256(token.encode('utf-8')).digest(),
                 cfg['YANDEX_LOGIN'].casefold(), cfg['OAUTH_CLIENT_ID'])
    now = time.monotonic()
    cached = _AUTH_CACHE.get(cache_key)
    if cached and cached[0] > now:
        return cached[1]

    request = Request('https://login.yandex.ru/info?format=json',
                      headers={'Authorization': 'OAuth ' + token})
    # Retry only the read-only identity lookup, within the previous one-second
    # network budget. Never retry invalid credentials or use an expired cache.
    deadline = time.monotonic() + 1.0
    for attempt in range(2):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise IdentityUnavailable('Authorization unavailable')
        try:
            with urlopen(request, timeout=min(0.6, remaining)) as response:
                user = json.load(response)
            break
        except HTTPError as exc:
            retryable = exc.code in (429, 500, 502, 503, 504)
        except (URLError, TimeoutError, ConnectionError):
            retryable = True
        except Exception:
            raise IdentityUnavailable('Authorization unavailable') from None
        LOG.warning('identity_lookup_failed attempt=%s retryable=%s', attempt + 1, retryable)
        if not retryable:
            raise PermissionError('Account not authorized') from None
        if attempt == 1:
            raise IdentityUnavailable('Authorization unavailable') from None
    if str(user.get('login', '')).casefold() != cfg['YANDEX_LOGIN'].casefold() or user.get('client_id') != cfg['OAUTH_CLIENT_ID']:
        raise PermissionError('Account not allowed')
    user_id = str(user['id'])

    for key, (expires, _) in list(_AUTH_CACHE.items()):
        if expires <= now:
            _AUTH_CACHE.pop(key, None)
    _AUTH_CACHE[cache_key] = (time.monotonic() + _AUTH_CACHE_TTL_SECONDS, user_id)
    while len(_AUTH_CACHE) > 64:
        _AUTH_CACHE.pop(next(iter(_AUTH_CACHE)))
    return user_id

def sign(key, message):
    return hmac.new(key, message.encode('ascii'), hashlib.sha256).hexdigest()


def validate_reply(packet, verb, nonce, key):
    try:
        fields = packet.decode('ascii').split('|')
        return (len(fields) == 4 and fields[:3] == ['WM1', verb, nonce]
                and hmac.compare_digest(fields[3], sign(key, verb + '|' + nonce)))
    except (UnicodeError, ValueError):
        return False


def power_on(cfg):
    packet = bytes([255]) * 6 + cfg['mac'] * 16
    # Resolve once, never accept a caller-supplied host or MAC.
    ip = resolve_pc(cfg['PC_HOST'])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        for _ in range(3):
            sock.sendto(packet, (ip, cfg['port']))


def connect_agent(cfg, ip):
    # A short retry for the idempotent PING absorbs a lost first UDP packet.
    # Each attempt uses its own nonce and socket. Command ACK recovery below
    # reuses identical bytes and endpoint, as required by the helper's ledger.
    for _ in range(2):
        nonce = secrets.token_hex(16)
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(0.45)
        try:
            sock.connect((ip, cfg['port']))
            sock.send(('WM1|PING|' + nonce).encode('ascii'))
            reply = sock.recv(256)
        except (socket.timeout, ConnectionRefusedError):
            sock.close()
            continue
        except Exception:
            sock.close()
            raise
        if not validate_reply(reply, 'ONLINE', nonce, cfg['key']):
            sock.close()
            raise DeviceError('INTERNAL_ERROR', 'Ответ Windows-помощника не прошёл проверку.')
        return sock, nonce
    raise DeviceError('DEVICE_UNREACHABLE', 'Windows-помощник не ответил после двух проверок связи.')


def agent_command(cfg, command):
    if command not in ('SHUTDOWN', 'RESTART', 'SLEEP', 'LAUNCH', 'CHATGPT', 'CHAT_END', 'PROFILE_WORK', 'PROFILE_GAME', 'PROFILE_CINEMA'):
        raise ValueError('Unsupported agent command')
    try:
        ip = resolve_pc(cfg['PC_HOST'])
    except OSError:
        raise DeviceError('DEVICE_UNREACHABLE', 'WM155 DNS: не удалось определить адрес ПК.') from None
    try:
        sock, nonce = connect_agent(cfg, ip)
    except DeviceError as exc:
        raise DeviceError(exc.code, 'WM155 PING: ' + exc.message) from None
    except OSError:
        raise DeviceError('DEVICE_UNREACHABLE', 'WM155 PING_IO: сетевая ошибка при проверке помощника.') from None
    with sock:
        sock.settimeout(0.35)
        message = command + '|' + nonce
        packet = 'WM1|' + message + '|' + sign(cfg['key'], message)
        reply = None
        for attempt in range(2):
            try:
                sock.send(packet.encode('ascii'))
            except OSError:
                raise DeviceError('DEVICE_UNREACHABLE', 'WM155 SEND: не удалось отправить команду ПК.') from None
            deadline = time.monotonic() + 0.35
            try:
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise socket.timeout()
                    sock.settimeout(remaining)
                    candidate = sock.recv(256)
                    # UDP can duplicate/delay the probe response. It is not a
                    # command ACK, but must not hide the real ACK behind it.
                    if validate_reply(candidate, 'ONLINE', nonce, cfg['key']):
                        continue
                    reply = candidate
                    break
                break
            except (socket.timeout, ConnectionRefusedError):
                LOG.warning('agent_ack_timeout command=%s attempt=%s', command, attempt + 1)
            except OSError:
                raise DeviceError('DEVICE_UNREACHABLE', 'WM155 ACK_IO: сетевая ошибка при ожидании подтверждения.') from None
        if reply is None:
            raise DeviceError('DEVICE_UNREACHABLE', 'WM155 ACK_TIMEOUT: ПК ответил на PING, но не подтвердил команду. Она могла выполниться.')
        if validate_reply(reply, command + '_ACCEPTED', nonce, cfg['key']):
            return
        if (command in ('LAUNCH', 'CHATGPT', 'CHAT_END') or command.startswith('PROFILE_')) and validate_reply(reply, command + '_NOT_CONFIGURED', nonce, cfg['key']):
            raise DeviceError('INVALID_ACTION', 'Сначала настройте соответствующее действие в Wake Mini на компьютере.')
        if command == 'CHATGPT' and validate_reply(reply, 'CHATGPT_BUSY', nonce, cfg['key']):
            raise DeviceError('INVALID_ACTION', 'Подготовка разговора или управление телевизором уже выполняется.')
        if (command == 'LAUNCH' or command.startswith('PROFILE_')) and validate_reply(reply, command + '_FAILED', nonce, cfg['key']):
            raise DeviceError('INTERNAL_ERROR', 'Windows не удалось запустить выбранное приложение.')
        raise DeviceError('INTERNAL_ERROR', 'Подтверждение команды не прошло проверку.')


def power_off(cfg):
    agent_command(cfg, 'SHUTDOWN')


def power_state(cfg):
    # The signed challenge response confirms the helper is awake. The query
    # handler reports no response as "off" so Alice keeps the wake command usable.
    ip = resolve_pc(cfg['PC_HOST'])
    sock, _ = connect_agent(cfg, ip)
    sock.close()
    return True

def dispatch(event):
    if not isinstance(event, dict):
        raise ValueError('Invalid event')
    cfg = settings()
    headers = event.get('headers', {})
    user_id = authorize(headers.get('authorization', ''), cfg)
    kind = event.get('request_type')
    request_id = str(headers.get('request_id', ''))[:128]
    LOG.info('smart_home type=%s request_id=%s', str(kind)[:20], json.dumps(request_id))
    answer = {'request_id': request_id}
    if kind == 'discovery':
        info = {'manufacturer': 'Wake Mini', 'model': 'PC power', 'sw_version': '1.5.5'}
        answer['payload'] = {'user_id': user_id, 'devices': [
            {'id': DEVICE_ID, 'name': 'Компьютер', 'type': 'devices.types.other',
             'description': 'Включение, выключение и проверка связи с Windows',
             'capabilities': [{'type': CAPABILITY, 'retrievable': True, 'reportable': False}],
             'device_info': info},
            {'id': RESTART_DEVICE_ID, 'name': 'Перезагрузка компьютера', 'type': 'devices.types.other',
             'description': 'Однократная команда перезагрузки Windows',
             'capabilities': [{'type': CAPABILITY, 'retrievable': False, 'reportable': False}],
             'device_info': info},
            {'id': SLEEP_DEVICE_ID, 'name': 'Спящий режим компьютера', 'type': 'devices.types.other',
             'description': 'Однократная команда перевода Windows в спящий режим',
             'capabilities': [{'type': CAPABILITY, 'retrievable': False, 'reportable': False}],
             'device_info': info},
            {'id': APP_DEVICE_ID, 'name': 'Приложение на компьютере', 'type': 'devices.types.other',
             'description': 'Запускает выбранное пользователем приложение Windows',
             'capabilities': [{'type': CAPABILITY, 'retrievable': False, 'reportable': False}],
             'device_info': info},
        ]}
        for profile, title in [('work', 'Рабочий режим компьютера'), ('game', 'Игровой режим компьютера'), ('cinema', 'Кино на компьютере')]:
            answer['payload']['devices'].append({
                'id': 'wakemini-profile-' + profile, 'name': title, 'type': 'devices.types.other',
                'description': 'Запуск локального профиля Windows',
                'capabilities': [{'type': CAPABILITY, 'retrievable': False, 'reportable': False}],
                'device_info': info})
        answer['payload']['devices'].append({
            'id': CHAT_DEVICE_ID, 'name': 'Разговор с ChatGPT', 'type': 'devices.types.other',
            'description': 'Подготовка LG, наушников и Chrome. Ход выполнения — в помощнике Windows; запуск голоса на странице вручную.',
            'capabilities': [{'type': CAPABILITY, 'retrievable': False, 'reportable': False}],
            'device_info': info})
        return answer
    if kind == 'unlink':
        return answer
    if kind not in ('action', 'query'):
        raise ValueError('Unsupported request_type')
    devices = event.get('payload', {}).get('devices', [])
    if not isinstance(devices, list) or not devices or len(devices) > 20:
        raise ValueError('Invalid device list')
    if kind == 'query':
        results = []
        for device in devices:
            device_id = device.get('id')
            result = {'id': device_id}
            if device_id not in (DEVICE_ID, *COMMANDS):
                result.update(error_code='DEVICE_NOT_FOUND', error_message='Устройство не найдено.')
            elif device_id != DEVICE_ID:
                result['capabilities'] = []
            else:
                try:
                    result['capabilities'] = [{'type': CAPABILITY, 'state': {'instance': 'on', 'value': power_state(cfg)}}]
                except DeviceError as exc:
                    if exc.code == 'DEVICE_UNREACHABLE':
                        result['capabilities'] = [{'type': CAPABILITY, 'state': {'instance': 'on', 'value': False}}]
                    else:
                        result.update(error_code=exc.code, error_message=exc.message)
                except OSError:
                    result['capabilities'] = [{'type': CAPABILITY, 'state': {'instance': 'on', 'value': False}}]
            results.append(result)
        answer['payload'] = {'devices': results}
        return answer
    if len(devices) != 1:
        raise ValueError('Actions must target exactly one device')
    device = devices[0]
    result = {'id': device.get('id')}
    device_id = device.get('id')
    if device_id not in (DEVICE_ID, *COMMANDS):
        result.update(error_code='DEVICE_NOT_FOUND', error_message='Устройство не найдено.')
    else:
        caps = device.get('capabilities', [])
        valid = (isinstance(caps, list) and len(caps) == 1 and caps[0].get('type') == CAPABILITY
                 and caps[0].get('state', {}).get('instance') == 'on'
                 and type(caps[0].get('state', {}).get('value')) is bool)
        if not valid:
            result['action_result'] = {'status': 'ERROR', 'error_code': 'INVALID_ACTION'}
        else:
            value = caps[0]['state']['value']
            action_result = {'status': 'DONE'}
            try:
                if device_id == DEVICE_ID:
                    (power_on if value else power_off)(cfg)
                elif device_id == CHAT_DEVICE_ID:
                    agent_command(cfg, 'CHATGPT' if value else 'CHAT_END')
                elif value:
                    agent_command(cfg, COMMANDS[device_id])
                else:
                    raise DeviceError('INVALID_ACTION', 'Для этой команды поддерживается только запуск.')
            except DeviceError as exc:
                LOG.warning('agent_action_error command_device=%s code=%s', device_id, exc.code)
                action_result = {'status': 'ERROR', 'error_code': exc.code, 'error_message': exc.message}
            except OSError:
                action_result = {'status': 'ERROR', 'error_code': 'DEVICE_UNREACHABLE',
                                 'error_message': 'WM155 WAKE_IO: сетевая ошибка при отправке пакета включения.'}
            result['capabilities'] = [{'type': CAPABILITY, 'state': {'instance': 'on', 'action_result': action_result}}]
    answer['payload'] = {'devices': [result]}
    return answer


def _provider_handler(event, context):
    """Native Smart Home Cloud Function (raw); also supports an HTTP envelope for testing."""
    http = isinstance(event, dict) and 'httpMethod' in event
    try:
        if http:
            body = event.get('body', '')
            if event.get('isBase64Encoded'):
                body = base64.b64decode(body).decode('utf-8')
            event = json.loads(body)
        result = dispatch(event)
        if not http:
            return result
        return {'statusCode': 200, 'headers': {'Content-Type': 'application/json'},
                'body': json.dumps(result, ensure_ascii=False)}
    except IdentityUnavailable:
        if not http:
            raise RuntimeError('Identity service temporarily unavailable') from None
        return {'statusCode': 503, 'body': ''}
    except PermissionError:
        if not http:
            # Raw Cloud Functions always return HTTP 200 for normal returns;
            # raise a sanitized error to deny the request without leaking discovery or secrets.
            raise PermissionError('Account not authorized') from None
        return {'statusCode': 401, 'body': ''}
    except (KeyError, ValueError, TypeError, AttributeError):
        if not http:
            raise ValueError('Invalid request or missing function settings') from None
        return {'statusCode': 400, 'body': ''}


# Per-invocation timings; recent history is private, bounded and instance-local.
_TRACE = ContextVar('wake_trace', default=None)
_RECENT = deque(maxlen=20)
_RECENT_LOCK = threading.Lock()


def _outcome(exc):
    if isinstance(exc, IdentityUnavailable): return 'identity_unavailable'
    if isinstance(exc, PermissionError): return 'unauthorized'
    if isinstance(exc, DeviceError): return exc.code
    if isinstance(exc, OSError): return 'network_error'
    return 'internal_error'


def _timed(name, function):
    @wraps(function)
    def call(*args, **kwargs):
        trace = _TRACE.get()
        if trace is None: return function(*args, **kwargs)
        started = time.perf_counter()
        result = 'ok'
        try: return function(*args, **kwargs)
        except Exception as exc:
            result = _outcome(exc)
            raise
        finally:
            trace.append({'stage': name, 'result': result,
                          'ms': round((time.perf_counter() - started) * 1000)})
    return call


for _name in ('authorize', 'connect_agent', 'agent_command', 'power_state', 'power_on'):
    globals()[_name] = _timed(_name, globals()[_name])


def _diagnostic(event):
    headers = {str(k).lower(): v for k, v in (event.get('headers') or {}).items()}
    supplied = headers.get('x-wakemini-key', '')
    expected = os.environ.get('AGENT_KEY', '').strip()
    if not isinstance(supplied, str) or not re.fullmatch('[0-9a-fA-F]{64}', expected) or not hmac.compare_digest(supplied.lower(), expected.lower()):
        return {'statusCode': 403, 'body': ''}
    trace = []
    marker = _TRACE.set(trace)
    started = time.perf_counter()
    try:
        cfg = settings()
        ip = resolve_pc(cfg['PC_HOST'])
        trace.append({'stage': 'dns', 'result': 'ok', 'ms': round((time.perf_counter() - started) * 1000)})
        sock, _ = connect_agent(cfg, ip)
        sock.close()
        result = 'pc_replied'
    except Exception as exc:
        result = _outcome(exc)
    finally:
        _TRACE.reset(marker)
    with _RECENT_LOCK: recent = list(_RECENT)
    body = {'version': '1.5.5', 'probe': result, 'elapsed_ms': round((time.perf_counter() - started) * 1000),
            'stages': trace, 'recent_instance_requests': recent,
            'note': 'Read-only signed probe. No power or app commands. History is instance-local and may be empty.'}
    return {'statusCode': 200, 'headers': {'Content-Type': 'application/json', 'Cache-Control': 'no-store'},
            'body': json.dumps(body, ensure_ascii=False)}


def handler(event, context):
    if isinstance(event, dict) and event.get('httpMethod') == 'GET' and event.get('queryStringParameters', {}) and event['queryStringParameters'].get('wakemini_diag') == '1':
        return _diagnostic(event)
    trace = []
    marker = _TRACE.set(trace)
    started = time.perf_counter()
    outcome = 'returned'
    try:
        result = _provider_handler(event, context)
        if isinstance(result, dict) and 'statusCode' in result:
            outcome = 'http_' + str(result['statusCode'])
        return result
    except Exception as exc:
        outcome = _outcome(exc)
        raise
    finally:
        _TRACE.reset(marker)
        with _RECENT_LOCK:
            _RECENT.append({'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                            'outcome': outcome, 'ms': round((time.perf_counter() - started) * 1000), 'stages': trace})
