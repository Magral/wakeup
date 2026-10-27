import json
import os
import socket
import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main

ENV = {'PC_HOST': '127.0.0.1', 'PC_MAC': '00:11:22:AA:BB:CC', 'AGENT_KEY': 'ab' * 32,
       'YANDEX_LOGIN': 'owner', 'OAUTH_CLIENT_ID': 'client'}


def event(value=True, kind='action'):
    return {'request_type': kind, 'headers': {'request_id': 'test', 'authorization': 'Bearer test'},
            'payload': {'devices': [{'id': main.DEVICE_ID, 'capabilities': [{'type': main.CAPABILITY,
                        'state': {'instance': 'on', 'value': value}}]}]}}


class Tests(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, ENV, clear=True)
        self.env.start()
        self.addCleanup(self.env.stop)

    def test_magic_packet_over_real_udp(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver:
            receiver.bind(('127.0.0.1', 0))
            receiver.settimeout(1)
            cfg = main.settings() | {'port': receiver.getsockname()[1]}
            main.power_on(cfg)
            for _ in range(3):
                packet, _ = receiver.recvfrom(256)
                self.assertEqual(packet, b'\xff' * 6 + bytes.fromhex('001122aabbcc') * 16)

    def test_commands_interoperate_over_real_udp(self):
        for command in ('SHUTDOWN', 'RESTART', 'SLEEP', 'STATUS'):
            failures = []
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
                server.bind(('127.0.0.1', 0))
                server.settimeout(2)
                cfg = main.settings() | {'port': server.getsockname()[1]}
                def agent():
                    try:
                        ping, remote = server.recvfrom(256)
                        nonce = ping.decode().split('|')[2]
                        msg = 'ONLINE|' + nonce
                        server.sendto(('WM1|' + msg + '|' + main.sign(cfg['key'], msg)).encode(), remote)
                        packet, same_remote = server.recvfrom(256)
                        self.assertEqual(remote, same_remote)
                        self.assertEqual(packet.decode(), 'WM1|' + command + '|' + nonce + '|' + main.sign(cfg['key'], command + '|' + nonce))
                        verb = 'STATUS_ONLINE' if command == 'STATUS' else command + '_ACCEPTED'
                        msg = verb + '|' + nonce
                        server.sendto(('WM1|' + msg + '|' + main.sign(cfg['key'], msg)).encode(), remote)
                    except Exception as exc:
                        failures.append(exc)
                thread = threading.Thread(target=agent)
                thread.start()
                result = main.agent_command(cfg, command)
                thread.join()
                self.assertEqual(failures, [])
                self.assertEqual(result, True if command == 'STATUS' else None)

    @patch('main.authorize')
    def test_on_and_off_are_separate(self, auth):
        with patch('main.power_on') as on, patch('main.power_off') as off:
            main.dispatch(event(True)); on.assert_called_once(); off.assert_not_called()
        with patch('main.power_on') as on, patch('main.power_off', side_effect=main.DeviceError('DEVICE_UNREACHABLE', 'offline')) as off:
            result = main.dispatch(event(False))
            on.assert_not_called(); off.assert_called_once()
            self.assertEqual(result['payload']['devices'][0]['capabilities'][0]['state']['action_result']['status'], 'ERROR')

    @patch('main.authorize')
    def test_restart_and_sleep_are_separate_one_shot_devices(self, auth):
        for device_id, command in ((main.RESTART_DEVICE_ID, 'RESTART'), (main.SLEEP_DEVICE_ID, 'SLEEP')):
            request = event()
            request['payload']['devices'][0]['id'] = device_id
            with patch('main.agent_command') as run:
                result = main.dispatch(request)
                run.assert_called_once_with(main.settings(), command)
            self.assertEqual(result['payload']['devices'][0]['capabilities'][0]['state']['action_result']['status'], 'DONE')
            request['payload']['devices'][0]['capabilities'][0]['state']['value'] = False
            result = main.dispatch(request)
            self.assertEqual(result['payload']['devices'][0]['capabilities'][0]['state']['action_result']['error_code'], 'INVALID_ACTION')

    def test_auth_denied_before_network(self):
        with patch('main.authorize', side_effect=PermissionError), patch('main.power_on') as on:
            with self.assertRaises(PermissionError): main.dispatch(event())
            on.assert_not_called()

    def test_checks_both_owner_and_oauth_client(self):
        for user in [{'id': 'other', 'login': 'other', 'client_id': 'client'}, {'id': '123', 'login': 'owner', 'client_id': 'other'}]:
            response = MagicMock()
            response.__enter__.return_value.read.return_value = json.dumps(user)
            with patch('main.urlopen', return_value=response):
                with self.assertRaises(PermissionError): main.authorize('Bearer token', main.settings())

    def test_valid_yandex_identity(self):
        response = MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps({'id': '123', 'login': 'owner', 'client_id': 'client'})
        with patch('main.urlopen', return_value=response): main.authorize('Bearer token', main.settings())

    def test_bad_hmac_rejected(self):
        self.assertFalse(main.validate_reply(b'WM1|ONLINE|nonce|bad', 'ONLINE', 'nonce', bytes(32)))

    @patch('main.authorize')
    def test_reject_string_boolean_and_unknown_device(self, auth):
        with patch('main.power_on') as on:
            response = main.dispatch(event('true'))
            self.assertEqual(response['payload']['devices'][0]['action_result']['error_code'], 'INVALID_ACTION')
            invalid = event(); invalid['payload']['devices'][0]['id'] = 'intruder'
            response = main.dispatch(invalid)
            self.assertEqual(response['payload']['devices'][0]['error_code'], 'DEVICE_NOT_FOUND')
            on.assert_not_called()

    @patch('main.authorize')
    def test_discovery_and_query_do_not_invent_power_state(self, auth):
        found = main.dispatch(event(kind='discovery'))['payload']['devices']
        self.assertEqual([device['id'] for device in found], [main.DEVICE_ID, main.RESTART_DEVICE_ID, main.SLEEP_DEVICE_ID])
        self.assertTrue(found[0]['capabilities'][0]['retrievable'])
        self.assertFalse(found[1]['capabilities'][0]['retrievable'])
        with patch('main.power_state', return_value=True):
            status = main.dispatch(event(kind='query'))['payload']['devices'][0]
        self.assertEqual(status['capabilities'][0]['state']['value'], True)
        query = event(kind='query')
        query['payload']['devices'].append({'id': main.RESTART_DEVICE_ID})
        with patch('main.power_state', return_value=True):
            results = main.dispatch(query)['payload']['devices']
        self.assertEqual(len(results), 2)
        self.assertEqual(results[1]['capabilities'], [])
        with patch('main.power_state', side_effect=main.DeviceError('DEVICE_UNREACHABLE', 'offline')):
            status = main.dispatch(event(kind='query'))['payload']['devices'][0]
        self.assertEqual(status['error_code'], 'DEVICE_UNREACHABLE')

if __name__ == '__main__': unittest.main()
