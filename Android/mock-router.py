"""Fictional ARRIS HTTPS server with one constrained web-flag test, solely for CI."""
import base64
import json
import ssl
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class RouterFixture:
    def __init__(self, certificate, private_key):
        self.webui = "0"
        self.webui_writes = 0
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
                # Only login and the exact reviewed web-interface test are accepted.
                if self.path not in ('/loginAction', '/setData'):
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
                if self.path == '/setData':
                    if self.headers.get('Cookie') != owner.cookie or not owner.cookie:
                        self.reply({'STATUS': 'SessionTimeout'})
                        return
                    request = json.loads(body)
                    expected = {'REQUEST': {'OPERTYPE': 'MODIFY', 'LOGINCFG': {'WEBUIENABLE': '1' if owner.webui == '0' else '0'}}}
                    if request != expected:
                        raise AssertionError('Unexpected web-interface payload')
                    owner.webui = request['REQUEST']['LOGINCFG']['WEBUIENABLE']
                    owner.webui_writes += 1
                    self.reply({'STATUS': 'OK'})
                    return
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
                    self.reply({'STATUS': 'OK', 'DEVICE_INFO': {'SERIAL_KEY': 'TESTSERIAL123', 'MODEL_NAME': 'W30', 'HARDWARE_VERSION': 'WR11AXER1', 'SOFTWARE_VERSION': 'AXR.0814.240522.10'}})
                elif self.path == '/getData?key=LOGINCFG_ALL':
                    self.reply({'STATUS': 'OK', 'LOGINCFG': {'WEBUIENABLE': owner.webui, 'SSOPASSWORD': 'FictionalSecret'}})
                elif self.path == '/getData?key=DEVICESTATUS_ALL':
                    self.reply({'STATUS': 'OK', 'DEVICESTATUS': {'BOOTING': '0', 'SOFTWAREUPDATEACTIVE': '0'}})
                elif self.path == '/getData?key=DEVICEOPERMODE_ALL':
                    self.reply({'STATUS': 'OK', 'DEVICEOPERMODE': {'MODE': 'BRIDGE'}})
                else:
                    raise AssertionError('Unexpected read')

        self.server = ThreadingHTTPServer(('0.0.0.0', 50011), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, private_key)
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
