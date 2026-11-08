import json
import socket
import threading
import time
import unittest
from unittest.mock import MagicMock, patch
import main


class DnsTests(unittest.TestCase):
    def setUp(self):
        with main._DNS_LOCK:
            main._DNS_CACHE.clear()

    def test_literal_and_working_system_dns_need_no_https(self):
        with patch('main._dns_https') as https:
            self.assertEqual(main.resolve_pc('192.168.0.199'), '192.168.0.199')
            with patch('main.socket.gethostbyname', return_value='91.78.221.109'):
                self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.109')
            https.assert_not_called()

    def test_system_failure_recovers_and_cache_expires_after_ip_change(self):
        with patch('main.socket.gethostbyname', side_effect=socket.gaierror()), patch('main._dns_https', return_value=('91.78.221.109', 20)):
            self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.109')
        with patch('main._dns_https') as https:
            self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.109')
            https.assert_not_called()
        main._DNS_CACHE['magral.tplinkdns.com'] = (0, '91.78.221.109')
        with patch('main.socket.gethostbyname', side_effect=socket.gaierror()), patch('main._dns_https', return_value=('91.78.221.110', 20)):
            self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.110')

    def test_one_https_resolver_failure_does_not_block_other(self):
        def lookup(host, endpoint):
            if 'google' in endpoint:
                raise TimeoutError()
            return '91.78.221.109', 0
        with patch('main.socket.gethostbyname', side_effect=socket.gaierror()), patch('main._dns_https', side_effect=lookup):
            self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.109')

    def test_stalled_system_dns_does_not_block_fallback(self):
        gate = threading.Event()
        def stuck(host):
            gate.wait(2)
            raise socket.gaierror()
        try:
            with patch('main.socket.gethostbyname', side_effect=stuck), patch('main._dns_https', return_value=('91.78.221.109', 0)):
                start = time.monotonic()
                self.assertEqual(main.resolve_pc('magral.tplinkdns.com'), '91.78.221.109')
                self.assertLess(time.monotonic() - start, 0.5)
        finally:
            gate.set()

    def test_all_fail_no_stale_address_or_command(self):
        main._DNS_CACHE['magral.tplinkdns.com'] = (0, '91.78.221.109')
        with patch('main.socket.gethostbyname', side_effect=socket.gaierror()), patch('main._dns_https', side_effect=TimeoutError()), patch('main.connect_agent') as agent:
            with self.assertRaises(main.DeviceError):
                main.agent_command({'PC_HOST': 'magral.tplinkdns.com'}, 'PROFILE_WORK')
            agent.assert_not_called()

    def test_https_validates_answer_and_cname_ttl(self):
        response = MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps({'Status': 0, 'Answer': [
            {'type': 5, 'TTL': 4, 'data': 'other.example.'},
            {'type': 1, 'TTL': 100, 'data': '91.78.221.109'}]}).encode()
        with patch('main.urlopen', return_value=response):
            self.assertEqual(main._dns_https('magral.tplinkdns.com', 'https://dns.google/resolve'), ('91.78.221.109', 4))
        for data in [b'{', b'x' * 16385, b'{"Status":3}', b'{"Status":0,"Answer":[{"type":1,"data":"127.0.0.1"}]}']:
            response.__enter__.return_value.read.return_value = data
            with patch('main.urlopen', return_value=response), self.assertRaises(ValueError):
                main._dns_https('magral.tplinkdns.com', 'https://dns.google/resolve')
