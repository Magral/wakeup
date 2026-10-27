"""Private Yandex Smart Home provider; stdlib only. No secrets in source."""
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

CAPABILITY = 'devices.capabilities.on_off'
DEVICE_ID = 'wakemini-pc'
RESTART_DEVICE_ID = 'wakemini-restart'
SLEEP_DEVICE_ID = 'wakemini-sleep'
APP_DEVICE_ID = 'wakemini-app'
COMMANDS = {
    RESTART_DEVICE_ID: 'RESTART',
    SLEEP_DEVICE_ID: 'SLEEP',
    APP_DEVICE_ID: 'LAUNCH',
}
LOG = logging.getLogger(__name__)
_AUTH_CACHE = {}
_AUTH_CACHE_TTL_SECONDS = 20


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
    try:
        with urlopen(request, timeout=1.0) as response:
            user = json.load(response)
    except Exception:
        raise PermissionError('Authorization unavailable') from None
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
    ip = socket.gethostbyname(cfg['PC_HOST'])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        for _ in range(3):
            sock.sendto(packet, (ip, cfg['port']))


def connect_agent(cfg, ip):
    # A short retry for the idempotent PING absorbs a lost first UDP packet.
    # Each attempt uses its own nonce and socket. Commands themselves are never retried.
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
    if command not in ('SHUTDOWN', 'RESTART', 'SLEEP', 'LAUNCH'):
        raise ValueError('Unsupported agent command')
    ip = socket.gethostbyname(cfg['PC_HOST'])
    sock, nonce = connect_agent(cfg, ip)
    with sock:
        sock.settimeout(0.75)
        message = command + '|' + nonce
        packet = 'WM1|' + message + '|' + sign(cfg['key'], message)
        sock.send(packet.encode('ascii'))
        try:
            reply = sock.recv(256)
        except (socket.timeout, ConnectionRefusedError):
            # Do not retry a command: Windows may have accepted it and only lost the acknowledgement.
            raise DeviceError('DEVICE_UNREACHABLE', 'Windows-помощник не подтвердил команду.') from None
        if validate_reply(reply, command + '_ACCEPTED', nonce, cfg['key']):
            return
        if command == 'LAUNCH' and validate_reply(reply, 'LAUNCH_NOT_CONFIGURED', nonce, cfg['key']):
            raise DeviceError('INVALID_ACTION', 'Сначала выберите приложение в настройках Wake Mini на компьютере.')
        if command == 'LAUNCH' and validate_reply(reply, 'LAUNCH_FAILED', nonce, cfg['key']):
            raise DeviceError('INTERNAL_ERROR', 'Windows не удалось запустить выбранное приложение.')
        raise DeviceError('INTERNAL_ERROR', 'Подтверждение команды не прошло проверку.')


def power_off(cfg):
    agent_command(cfg, 'SHUTDOWN')


def power_state(cfg):
    # The signed challenge response confirms the helper is awake. The query
    # handler reports no response as "off" so Alice keeps the wake command usable.
    ip = socket.gethostbyname(cfg['PC_HOST'])
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
        info = {'manufacturer': 'Wake Mini', 'model': 'PC power', 'sw_version': '1.2.4'}
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
                elif value:
                    agent_command(cfg, COMMANDS[device_id])
                else:
                    raise DeviceError('INVALID_ACTION', 'Для этой команды поддерживается только запуск.')
            except DeviceError as exc:
                action_result = {'status': 'ERROR', 'error_code': exc.code, 'error_message': exc.message}
            except OSError:
                action_result = {'status': 'ERROR', 'error_code': 'DEVICE_UNREACHABLE'}
            result['capabilities'] = [{'type': CAPABILITY, 'state': {'instance': 'on', 'action_result': action_result}}]
    answer['payload'] = {'devices': [result]}
    return answer


def handler(event, context):
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
