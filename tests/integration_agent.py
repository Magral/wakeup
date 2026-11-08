"""Exercise the real Windows agent; only launches our harmless counter, never powers off."""
import hashlib
import hmac
import json
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import time

agent, counter = map(lambda x: Path(x).resolve(), sys.argv[1:3])
key = secrets.token_bytes(32)
(agent.parent / 'wake.key').write_text(key.hex())
def signature(message):
    return hmac.new(key, message.encode(), hashlib.sha256).hexdigest()

def signed(verb, nonce):
    message = verb + '|' + nonce
    return ('WM1|' + message + '|' + signature(message)).encode()

with tempfile.TemporaryDirectory() as temp:
    tally = Path(temp) / 'launches.txt'
    settings = {'Profiles': {'work': {'Path': str(counter), 'Arguments': '--launch-counter "' + str(tally) + '"'}}}
    (agent.parent / 'wake-actions.json').write_text(json.dumps(settings))
    process = subprocess.Popen([str(agent)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.connect(('127.0.0.1', 3000)); client.settimeout(0.5)
            # A hosted Windows runner can take several seconds to start WinForms
            # and load .NET. Keep startup tolerance separate from UDP ACK timing.
            startup_deadline = time.monotonic() + 25
            while time.monotonic() < startup_deadline:
                if process.poll() is not None:
                    raise AssertionError('Agent exited during startup: ' + str(process.returncode))
                nonce = secrets.token_hex(16)
                client.send(('WM1|PING|' + nonce).encode())
                try:
                    reply = client.recv(256)
                    if reply == signed('ONLINE', nonce): break
                except (socket.timeout, ConnectionRefusedError, ConnectionResetError): pass
                time.sleep(0.1)
            else:
                log = agent.parent / 'WakeMiniAgent.log'
                detail = log.read_text(errors='replace')[-2000:] if log.exists() else 'No agent log created'
                raise AssertionError('Agent did not answer a signed probe within 25 seconds. ' + detail)
            packet = signed('PROFILE_WORK', nonce)
            # A replay arriving from another source must neither execute nor
            # consume the original sender's pending challenge.
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stranger:
                stranger.connect(('127.0.0.1', 3000)); stranger.settimeout(0.2)
                stranger.send(packet)
                try:
                    stranger.recv(256)
                    raise AssertionError('Command accepted from a different endpoint')
                except socket.timeout: pass
            # Malformed and oversized packets must not disturb a valid session.
            client.send(b'WM1|junk')
            client.send(b'x' * 1024)
            client.send(signed('PROFILE_WORK', nonce)[:-1] + b'z')
            client.send(packet)
            # Simulate a sender recovering a missing acknowledgement by sending identical bytes.
            client.send(packet)
            client.settimeout(5)
            assert client.recv(256) == signed('PROFILE_WORK_ACCEPTED', nonce)
            assert client.recv(256) == signed('PROFILE_WORK_ACCEPTED', nonce)
            for _ in range(50):
                if tally.exists(): break
                time.sleep(0.1)
            time.sleep(0.5)
            assert tally.read_text().splitlines() == ['launch'], 'Profile executed more than once'
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stranger:
                stranger.connect(('127.0.0.1', 3000)); stranger.settimeout(0.4)
                stranger.send(packet)
                try:
                    stranger.recv(256)
                    raise AssertionError('Confirmation leaked to a different UDP endpoint')
                except socket.timeout: pass
            for command, expected in [('TV_STATUS', 'TV_STATUS_IDLE'), ('TV_PC', 'TV_PC_NOT_CONFIGURED'), ('TV_CANCEL', 'TV_CANCEL_ACCEPTED')]:
                token = secrets.token_hex(16)
                client.send(('WM1|PING|' + token).encode())
                assert client.recv(256) == signed('ONLINE', token)
                request = signed(command, token)
                client.send(request)
                assert client.recv(256) == signed(expected, token), command
                client.send(request)
                assert client.recv(256) == signed(expected, token), 'TV acknowledgement retry failed'
            for command, expected in [('CHAT_STATUS', 'CHAT_STATUS_IDLE'), ('CHATGPT', 'CHATGPT_NOT_CONFIGURED'), ('CHAT_CANCEL', 'CHAT_CANCEL_ACCEPTED')]:
                token = secrets.token_hex(16)
                client.send(('WM1|PING|' + token).encode())
                assert client.recv(256) == signed('ONLINE', token)
                request = signed(command, token)
                client.send(request)
                assert client.recv(256) == signed(expected, token), command
                client.send(request)
                assert client.recv(256) == signed(expected, token), 'Chat acknowledgement retry failed'
        print('Real Windows agent: signed probe, duplicate acknowledgements, single execution and endpoint binding passed.')
    finally:
        process.terminate()
        process.wait(timeout=10)
