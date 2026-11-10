"""Steam dialog and signed PC synchronization. Durable storage, never instance RAM."""
import base64
import hashlib
import hmac
import json
import os
import re
import time
import unicodedata
from difflib import SequenceMatcher


class Store:
    def __init__(self):
        import boto3
        from botocore.config import Config
        self.bucket = os.environ['STEAM_BUCKET']
        self.client = boto3.client('s3', endpoint_url='https://storage.yandexcloud.net',
                                  region_name='ru-central1', config=Config(connect_timeout=1, read_timeout=2, retries={'max_attempts': 0}))

    def get(self, name, default=None):
        from botocore.exceptions import ClientError
        try:
            data = self.client.get_object(Bucket=self.bucket, Key='wakemini-steam/' + name)['Body'].read(1024 * 1024 + 1)
            if len(data) > 1024 * 1024:
                raise ValueError('Object too large')
            return json.loads(data)
        except ClientError as exc:
            if exc.response['Error']['Code'] in ('NoSuchKey', '404'):
                return default
            raise

    def put(self, name, data):
        self.client.put_object(Bucket=self.bucket, Key='wakemini-steam/' + name,
                               Body=json.dumps(data, ensure_ascii=False).encode(), ContentType='application/json')


def normalized(text):
    return ' '.join(re.findall(r'\w+', unicodedata.normalize('NFKC', text).casefold().replace('ё', 'е')))


def matches(text, games):
    query = normalized(text)
    query = re.sub(r'^(?:запусти|включи|игру|игра|пожалуйста)\s+', '', query)
    aliases = json.loads(os.environ.get('STEAM_ALIASES', '{}'))
    scores = []
    for game in games:
        names = [game['name'], *aliases.get(game['appid'], [])]
        if game['appid'] == '275850':
            names += ['ноу мэнс скай', 'ноу менс скай', 'номанс скай']
        values = [normalized(name) for name in names]
        exact = query in values
        score = 1 if exact else max((SequenceMatcher(None, query, v).ratio() for v in values), default=0)
        if exact or score >= .55 or any(len(query) >= 3 and query in v for v in values):
            scores.append((exact, score, game))
    scores.sort(key=lambda x: (x[0], x[1]), reverse=True)
    if scores and scores[0][0]:
        return [x[2] for x in scores if x[0]][:5], True
    return [x[2] for x in scores[:5]], False


def agent(event, cfg, store):
    headers = {k.lower(): v for k, v in (event.get('headers') or {}).items()}
    body = event.get('body') or ''
    body = base64.b64decode(body) if event.get('isBase64Encoded') else body.encode()
    stamp = headers.get('x-wake-steam-time', '')
    if not stamp.isdigit() or abs(time.time() - int(stamp)) > 90 or len(body) > 1024 * 1024:
        raise PermissionError('Expired request')
    prefix = (stamp + '\n').encode()
    expected = hmac.new(cfg['key'], prefix + body, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(expected, headers.get('x-wake-steam-sign', '').lower()):
        raise PermissionError('Invalid signature')
    request = json.loads(body)
    op, payload = request.get('op'), request.get('payload', {})
    if op == 'catalog':
        games = payload['games']
        if not isinstance(games, list) or len(games) > 5000:
            raise ValueError('Invalid catalog')
        seen = set()
        for game in games:
            if not re.fullmatch(r'[1-9][0-9]{0,9}', game.get('appid', '')) or not isinstance(game.get('name'), str) or not 1 <= len(game['name']) <= 256 or game['appid'] in seen:
                raise ValueError('Invalid game')
            seen.add(game['appid'])
        store.put('catalog.json', {'games': [{'appid': g['appid'], 'name': g['name']} for g in games], 'updated': int(time.time())})
        result = {'ok': True}
    elif op == 'next':
        job = store.get('pending.json')
        if job and (job['expires'] < time.time() or store.get('results/' + job['id'] + '.json')):
            job = None
        result = {'job': job}
    elif op == 'result':
        if not re.fullmatch('[a-f0-9]{32}', payload.get('id', '')) or payload.get('result') not in ('launch_requested', 'already_running', 'failed', 'interrupted'):
            raise ValueError('Invalid result')
        store.put('results/' + payload['id'] + '.json', payload)
        result = {'ok': True}
    else:
        raise ValueError('Invalid operation')
    response = json.dumps(result, ensure_ascii=False, separators=(',', ':')).encode()
    return {'statusCode': 200, 'headers': {'Content-Type': 'application/json', 'Cache-Control': 'no-store',
            'X-Wake-Steam-Sign': hmac.new(cfg['key'], prefix + response, hashlib.sha256).hexdigest()}, 'body': response.decode()}


def dialog(event, cfg, store, authorize, wake):
    if event.get('session', {}).get('skill_id') != os.environ.get('STEAM_SKILL_ID') or not os.environ.get('STEAM_SKILL_ID'):
        raise PermissionError('Unexpected skill')
    token = event.get('session', {}).get('user', {}).get('access_token')
    if not token:
        return {'version': '1.0', 'start_account_linking': {}}
    authorize('Bearer ' + token, cfg)
    state = event.get('state', {}).get('session', {})
    command = event.get('request', {}).get('command', '').strip()[:256]
    def say(text, state=None, end=False):
        return {'version': '1.0', 'response': {'text': text, 'end_session': end}, 'session_state': state or {}}
    if normalized(command) in ('отмена', 'хватит', 'выход', 'стоп'):
        return say('Запуск отменён.', end=True)
    if not command or event.get('session', {}).get('new'):
        return say('Какую игру запустить?')
    catalog = store.get('catalog.json', {'games': []})
    games = catalog.get('games', [])
    if not games:
        return say('Список игр пока пуст. Включите компьютер и настройте синхронизацию Steam в Wake Mini.')
    selected = None
    choices = state.get('choices', [])
    words = {'один': 1, 'первую': 1, 'два': 2, 'вторую': 2, 'три': 3, 'третью': 3, 'четыре': 4, 'пять': 5}
    number = words.get(normalized(command), int(command) if command.isdigit() else 0)
    if choices and 1 <= number <= len(choices):
        selected = next((g for g in games if g['appid'] == choices[number - 1]), None)
    elif state.get('confirm') and normalized(command) in ('да', 'верно', 'запускай'):
        selected = next((g for g in games if g['appid'] == state['confirm']), None)
    if selected is None:
        found, exact = matches(command, games)
        if not found:
            return say('Не нашёл такую установленную игру. Назовите её иначе.')
        if len(found) == 1 and exact:
            selected = found[0]
        elif len(found) == 1:
            return say('Запустить ' + found[0]['name'] + '?', {'confirm': found[0]['appid']})
        else:
            return say('Нашёл несколько игр. ' + '. '.join(str(i + 1) + ': ' + g['name'] for i, g in enumerate(found)) + '. Назовите номер.', {'choices': [g['appid'] for g in found]})
    # Stable ID for a redelivered Alice message; repeated delivery never creates a second launch.
    session = event['session']
    jobid = hashlib.sha256((session['session_id'] + ':' + str(session['message_id'])).encode()).hexdigest()[:32]
    job = {'id': jobid, 'appid': selected['appid'], 'expires': int(time.time()) + 600}
    completed = store.get('results/' + jobid + '.json')
    if not completed:
        old = store.get('pending.json')
        if not old or old['id'] != jobid:
            store.put('pending.json', job)
        wake(cfg)
    return say('Команда принята. Подготовлю компьютер, переключу телевизор на HDMI один и запущу ' + selected['name'] + '.', end=True)


def handler(event, cfg, authorize, wake):
    headers = {k.lower(): v for k, v in (event.get('headers') or {}).items()}
    http = 'httpMethod' in event
    try:
        raw = event
        if http and 'x-wake-steam-sign' not in headers:
            body = event.get('body') or '{}'
            if event.get('isBase64Encoded'):
                body = base64.b64decode(body).decode()
            raw = json.loads(body)
        if 'x-wake-steam-sign' in headers:
            # Authenticate before constructing a storage client.
            # agent repeats validation before any reads/writes.
            stamp = headers.get('x-wake-steam-time', '')
            body = base64.b64decode(event['body']) if event.get('isBase64Encoded') else event['body'].encode()
            if not stamp.isdigit() or abs(time.time() - int(stamp)) > 90 or not hmac.compare_digest(
                hmac.new(cfg['key'], (stamp + '\n').encode() + body, hashlib.sha256).hexdigest(), headers['x-wake-steam-sign'].lower()):
                raise PermissionError('Invalid signature')
            return agent(event, cfg, Store())
        # Identity is verified by dialog before any catalog read.
        result = dialog(raw, cfg, Store(), authorize, wake)
        return {'statusCode': 200, 'headers': {'Content-Type': 'application/json'}, 'body': json.dumps(result, ensure_ascii=False)} if http else result
    except PermissionError:
        if http: return {'statusCode': 403, 'body': ''}
        return {'version': '1.0', 'response': {'text': 'Для управления нужен привязанный аккаунт владельца.', 'end_session': True}}
    except Exception:
        if http: return {'statusCode': 503, 'body': ''}
        return {'version': '1.0', 'response': {'text': 'Не удалось связаться с библиотекой игр. Попробуйте позже.', 'end_session': True}}
