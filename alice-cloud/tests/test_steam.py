import copy
import hashlib
import hmac
import json
import os
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import steam_cloud as steam

class Memory:
    def __init__(self): self.data = {'catalog.json': {'games': [{'appid': '275850', 'name': "No Man's Sky"}, {'appid': '10', 'name': 'Doom'}, {'appid': '20', 'name': 'Doom Eternal'}]}}
    def get(self, name, default=None): return copy.deepcopy(self.data.get(name, default))
    def put(self, name, value): self.data[name] = copy.deepcopy(value)

class Tests(unittest.TestCase):
    def setUp(self):
        self.store = Memory(); self.cfg = {'key': b'a'*32}; self.auth = Mock(); self.wake = Mock()
        self.env = patch.dict(os.environ, {'STEAM_SKILL_ID': 'steam-test'}, clear=True); self.env.start(); self.addCleanup(self.env.stop)
    def event(self, text, state=None):
        return {'session': {'skill_id': 'steam-test', 'session_id': 'session', 'message_id': 1, 'user': {'access_token': 'owner-token'}}, 'request': {'command': text}, 'state': {'session': state or {}}}
    def call(self, text, state=None): return steam.dialog(self.event(text, state), self.cfg, self.store, self.auth, self.wake)
    def test_prompt_and_linking(self):
        self.assertIn('Какую игру', self.call('')['response']['text'])
        e=self.event(''); e['session']['user']={}
        self.assertIn('start_account_linking', steam.dialog(e,self.cfg,self.store,self.auth,self.wake))
        self.wake.assert_not_called()
    def test_exact_and_cyrillic_alias(self):
        self.call('ноу мэнс скай'); self.assertEqual(self.store.get('pending.json')['appid'], '275850'); self.wake.assert_called_once()
    def test_ambiguous_no_launch(self):
        result=self.call('дум'); self.wake.assert_not_called()
        result=self.call('doom e'); self.assertIn('choices', result['session_state'])
        self.call('2', result['session_state']); self.wake.assert_called_once()
    def test_duplicate_after_completion_does_not_enqueue(self):
        self.call("No Man's Sky"); job=self.store.get('pending.json')
        self.store.put('results/'+job['id']+'.json', {'result':'launch_requested'})
        self.call("No Man's Sky"); self.assertEqual(self.wake.call_count,1)
    def test_unauthorized_does_not_wake(self):
        self.auth.side_effect=PermissionError()
        with self.assertRaises(PermissionError): self.call('Doom')
        self.wake.assert_not_called(); self.assertIsNone(self.store.get('pending.json'))
    def signed(self, op, payload):
        body=json.dumps({'op':op,'payload':payload}); stamp=str(int(time.time()))
        return {'body':body,'headers':{'X-Wake-Steam-Time':stamp,'X-Wake-Steam-Sign':hmac.new(self.cfg['key'],(stamp+'\n'+body).encode(),hashlib.sha256).hexdigest()}}
    def test_queue_survives_instance_and_filters_completed_expired(self):
        self.call("No Man's Sky"); e=self.signed('next',{})
        self.assertIsNotNone(json.loads(steam.agent(e,self.cfg,self.store)['body'])['job'])
        job=self.store.get('pending.json'); self.store.put('results/'+job['id']+'.json',{'result':'launch_requested'})
        self.assertIsNone(json.loads(steam.agent(e,self.cfg,self.store)['body'])['job'])
        self.store.data['pending.json']['expires']=0
        self.assertIsNone(json.loads(steam.agent(e,self.cfg,self.store)['body'])['job'])
    def test_bad_signature_no_write(self):
        e=self.signed('catalog',{'games':[]});e['headers']['X-Wake-Steam-Sign']='0'*64
        with self.assertRaises(PermissionError): steam.agent(e,self.cfg,self.store)
        self.assertEqual(len(self.store.get('catalog.json')['games']),3)
