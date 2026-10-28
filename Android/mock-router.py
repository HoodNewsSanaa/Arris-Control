"""Read-only fictional ARRIS HTTPS server used solely on the GitHub emulator runner."""
import base64
import json
import ssl
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class RouterFixture:
    def __init__(self, certificate, private_key):
        self.logins = 0
        self.cookie = ''
        self.anonymous_reads = 0
        self.protected_reads = 0
        owner = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def log_message(self, *_args):
                pass

            def reply(self, payload):
                body = json.dumps({'RESPONSE': payload}).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.send_header('Connection', 'close')
                if payload['STATUS'] == 'Login-OK':
                    self.send_header('Set-Cookie', owner.cookie + '; Path=/; HttpOnly; Secure')
                self.end_headers()
                self.wfile.write(body)

            def do_POST(self):
                # No setting modifications exist in this fixture.
                if self.path != '/loginAction':
                    raise AssertionError('Unexpected modifying request')
                if 'chunked' in self.headers.get('Transfer-Encoding', '').lower():
                    chunks = []
                    while True:
                        size = int(self.rfile.readline().split(b';', 1)[0], 16)
                        if size == 0:
                            while self.rfile.readline().strip():
                                pass
                            break
                        chunks.append(self.rfile.read(size))
                        self.rfile.read(2)
                    body = b''.join(chunks)
                else:
                    body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                expected = base64.b64encode(b'test%40example.com:FictionalLocalPassword123!')
                if not body.startswith(b'arg=' + expected + b'&'):
                    self.reply({'STATUS': 'IncorrectCredentials'})
                    return
                owner.logins += 1
                owner.cookie = 'MOBAPPSESSID=fictional-session-' + str(owner.logins)
                self.reply({'STATUS': 'Login-OK'})

            def do_GET(self):
                if self.headers.get('Cookie') != owner.cookie or not owner.cookie:
                    owner.anonymous_reads += 1
                    self.reply({'STATUS': 'SessionTimeout'})
                    return
                owner.protected_reads += 1
                if self.path == '/getData?key=DEVICE_INFO_ALL':
                    self.reply({'STATUS': 'OK', 'DEVICE_INFO': {'SERIAL_KEY': 'TESTSERIAL123'}})
                elif self.path == '/getData?key=DEVICEOPERMODE_ALL':
                    self.reply({'STATUS': 'OK', 'DEVICEOPERMODE': {'MODE': 'BRIDGE'}})
                else:
                    raise AssertionError('Unexpected read')

        self.server = ThreadingHTTPServer(('0.0.0.0', 50011), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, private_key)
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
