"""Probe actual password enforcement. Passwords are read locally and never printed."""
from pathlib import Path
import base64
import socket
import urllib.request
import urllib.error

config = dict(line.split('=', 1) for line in (Path(__file__).resolve().parents[1] / '.env').read_text().splitlines() if line and not line.startswith('#'))

def redis_command(connection, *parts):
    encoded = [str(part).encode() for part in parts]
    message = b'*%d\r\n' % len(encoded)
    for part in encoded:
        message += b'$%d\r\n' % len(part) + part + b'\r\n'
    connection.sendall(message)
    return connection.makefile('rb').readline().strip()

with socket.create_connection(('127.0.0.1', 6379), timeout=10) as connection:
    assert redis_command(connection, 'PING').startswith(b'-NOAUTH'), 'Redis accepted an anonymous client'
    assert redis_command(connection, 'AUTH', 'intentionally-wrong-password').startswith(b'-WRONGPASS'), 'Redis accepted an invalid password'
    assert redis_command(connection, 'AUTH', config['REDIS_PASSWORD']) == b'+OK', 'Redis rejected the configured password'
    assert redis_command(connection, 'PING') == b'+PONG'

def management_status(user=None, password=None):
    request = urllib.request.Request('http://localhost:15672/api/overview')
    if user is not None:
        credentials = base64.b64encode(f'{user}:{password}'.encode()).decode()
        request.add_header('Authorization', 'Basic ' + credentials)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code

assert management_status() == 401, 'RabbitMQ allowed anonymous management access'
assert management_status(config['RABBITMQ_USER'], 'intentionally-wrong-password') == 401, 'RabbitMQ accepted an invalid password'
assert management_status(config['RABBITMQ_USER'], config['RABBITMQ_PASSWORD']) == 200, 'RabbitMQ rejected the configured credentials'
print('Middleware passed: Redis and RabbitMQ reject missing/wrong credentials and accept configured passwords.')
