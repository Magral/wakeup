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
            for _ in range(30):
                nonce = secrets.token_hex(16)
                client.send(('WM1|PING|' + nonce).encode())
                try:
                    reply = client.recv(256)
                    if reply == signed('ONLINE', nonce): break
                except (socket.timeout, ConnectionRefusedError, ConnectionResetError): pass
                time.sleep(0.1)
            else: raise AssertionError('Agent did not answer a signed probe')
            packet = signed('PROFILE_WORK', nonce)
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
        print('Real Windows agent: signed probe, duplicate acknowledgements, single execution and endpoint binding passed.')
    finally:
        process.terminate()
        process.wait(timeout=10)
