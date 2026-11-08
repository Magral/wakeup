import json
import unittest
from unittest.mock import patch, MagicMock
from test_main import ENV, event
import main

class Diagnostics(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(main.os.environ, ENV, clear=True); self.env.start(); self.addCleanup(self.env.stop)
        main._AUTH_CACHE.clear()
        with main._RECENT_LOCK: main._RECENT.clear()
    def request(self, key=None):
        return {'httpMethod':'GET', 'queryStringParameters':{'wakemini_diag':'1'},
                'headers':{'X-WakeMini-Key': ENV['AGENT_KEY'] if key is None else key}}
    def test_diagnostic_denies_without_key_before_any_probe(self):
        with patch('main.connect_agent') as probe:
            self.assertEqual(main.handler(self.request(''),None)['statusCode'],403); probe.assert_not_called()
    def test_diagnostic_only_probes_and_never_executes(self):
        sock=MagicMock()
        with patch('main.connect_agent', return_value=(sock,'nonce')), patch('main.agent_command') as action, patch('main.power_on') as wake:
            result=main.handler(self.request(),None); body=json.loads(result['body'])
            self.assertEqual(body['probe'],'pc_replied'); action.assert_not_called(); wake.assert_not_called(); sock.close.assert_called_once()
            self.assertNotIn(ENV['AGENT_KEY'],result['body']); self.assertNotIn(ENV['YANDEX_LOGIN'],result['body'])
    def test_transient_identity_http_is_503_not_invalid_credentials(self):
        request={'httpMethod':'POST','body':json.dumps(event())}
        with patch('main.authorize',side_effect=main.IdentityUnavailable('private detail')):
            self.assertEqual(main.handler(request,None)['statusCode'],503)
        with patch('main.authorize',side_effect=PermissionError('private detail')):
            self.assertEqual(main.handler(request,None)['statusCode'],401)
        self.assertNotIn('private detail',json.dumps(list(main._RECENT)))
    def test_failed_probe_is_visible_without_secret_or_exception_details(self):
        with patch('main.connect_agent',side_effect=main.DeviceError('DEVICE_UNREACHABLE','sensitive text')):
            report=main.handler(self.request(),None)
            self.assertEqual(json.loads(report['body'])['probe'],'DEVICE_UNREACHABLE')
            self.assertNotIn('sensitive text',report['body'])
