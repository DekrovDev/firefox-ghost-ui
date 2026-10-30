"""Bounded, authenticated loopback bridge; no Firefox startup on import."""
import hmac
import json
import os
import re
import socket
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

MAX_BODY = 2 * 1024 * 1024
READ_SECONDS = 5
TOKEN_ENV = 'GHOST_PROBE_TOKEN'


def session_token():
    token = os.environ.pop(TOKEN_ENV, '')
    if not re.fullmatch(r'[A-Za-z0-9_-]{43}', token):
        raise RuntimeError('Start the bridge with run-ghost-probe.py')
    return token


class BridgeServer(HTTPServer):
    allow_reuse_address = False
    request_queue_size = 4

    def __init__(self, address, command, token):
        if address[0] != '127.0.0.1' or not re.fullmatch(r'[A-Za-z0-9_-]{43}', token):
            raise ValueError('Loopback address and session token required')
        self.command = command
        self.token = token
        self.stopping = False
        super().__init__(address, BridgeHandler)
        self.timeout = 1


class BridgeHandler(BaseHTTPRequestHandler):
    # One request per connection; never echo headers, credentials or payloads.
    protocol_version = 'HTTP/1.0'

    def log_message(self, *args):
        pass

    def handle_one_request(self):
        self.connection.settimeout(READ_SECONDS)
        self.deadline = threading.Timer(READ_SECONDS, self.expire)
        self.deadline.daemon = True
        self.deadline.start()
        try:
            super().handle_one_request()
        except (OSError, ValueError):
            self.close_connection = True
        finally:
            self.deadline.cancel()

    def expire(self):
        try:
            self.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

    def reply(self, status, data):
        self.close_connection = True
        body = json.dumps(data).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(body)

    def reject(self, status):
        self.reply(status, {'ok': False, 'error': 'Request rejected'})

    def do_POST(self):
        # Authenticate before reading a single body byte or dispatching commands.
        expected_host = '127.0.0.1:' + str(self.server.server_port)
        if self.headers.get_all('Host') != [expected_host] or self.headers.get_all('Origin'):
            return self.reject(403)
        auth = self.headers.get_all('Authorization', [])
        if len(auth) != 1 or not hmac.compare_digest(
                auth[0].encode('utf-8'), ('Bearer ' + self.server.token).encode('ascii')):
            return self.reject(401)
        if self.path != '/' or self.headers.get_all('Transfer-Encoding') or self.headers.get_all('Expect'):
            return self.reject(400)
        if self.headers.get_all('Content-Type') != ['application/json']:
            return self.reject(415)
        lengths = self.headers.get_all('Content-Length', [])
        if len(lengths) != 1 or not re.fullmatch(r'[0-9]{1,10}', lengths[0]):
            return self.reject(411)
        length = int(lengths[0])
        if not 0 < length <= MAX_BODY:
            return self.reject(413)
        try:
            body = self.rfile.read(length)
            if len(body) != length:
                return self.reject(400)
            request = json.loads(body)
            if not isinstance(request, dict):
                return self.reject(400)
        except (ValueError, UnicodeError, RecursionError):
            return self.reject(400)
        # Reading has finished. Marionette has its own bounded command timeout.
        self.deadline.cancel()
        try:
            if request.get('quit') is True:
                self.server.stopping = True
                result = True
            elif isinstance(request.get('script'), str):
                method = 'WebDriver:ExecuteAsyncScript' if request.get('async') else 'WebDriver:ExecuteScript'
                result = self.server.command(method, {
                    'script': request['script'], 'args': request.get('args', []),
                    'newSandbox': True, 'sandbox': 'system', 'line': 1,
                    'filename': 'ghost-ui-check', 'scriptTimeout': 30000})
            elif isinstance(request.get('command'), str):
                result = self.server.command(request['command'], request.get('parameters', {}))
            else:
                return self.reject(400)
        except Exception:
            return self.reply(200, {'ok': False, 'error': 'Bridge command failed'})
        self.reply(200, {'ok': True, 'result': result})
