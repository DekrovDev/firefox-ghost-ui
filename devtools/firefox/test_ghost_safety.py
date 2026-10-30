"""Sandbox-only regression fixtures. Never launches Firefox or accesses port 2832.

Run with unittest in the isolated environment described in README.md.
"""
import contextlib
import io
import importlib.util
import json
import os
import pathlib
import secrets
import socket
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from unittest import mock

import ghost_bootstrap as bootstrap
import ghost_bridge as bridge
import ghost_probe


def load_driver(filename):
    path = pathlib.Path(__file__).resolve().parent / filename
    spec = importlib.util.spec_from_file_location('fixture_driver', path)
    driver = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(driver)
    return driver


class LauncherTests(unittest.TestCase):
    def test_failed_readiness_closes_private_pipe_without_running_client(self):
        driver = load_driver('run-ghost-probe.py')
        server = mock.Mock()
        server.stdout = io.StringIO('')
        server.stdin = io.StringIO()
        server.poll.return_value = None
        with mock.patch('sys.argv', ['run-ghost-probe.py', 'probe', 'dummy-client.py']), \
             mock.patch.object(driver.subprocess, 'Popen', return_value=server), \
             mock.patch.object(driver.subprocess, 'call') as client:
            with self.assertRaisesRegex(RuntimeError, 'did not start'):
                driver.main()
        client.assert_not_called()
        self.assertTrue(server.stdin.closed)
        server.wait.assert_called_once_with(timeout=20)

    def test_token_changes_per_run_and_is_only_passed_in_environment(self):
        driver = load_driver('run-ghost-probe.py')
        tokens = []
        for _ in range(2):
            server = mock.Mock()
            server.stdout = io.StringIO('READY: dummy\n')
            server.stdin = io.StringIO()
            server.poll.return_value = None
            with mock.patch('sys.argv', ['run-ghost-probe.py', 'probe', 'dummy-client.py']), \
                 mock.patch.object(driver.subprocess, 'Popen', return_value=server) as launch, \
                 mock.patch.object(driver.subprocess, 'call', return_value=0) as client:
                self.assertEqual(driver.main(), 0)
            token = launch.call_args.kwargs['env']['GHOST_PROBE_TOKEN']
            tokens.append(token)
            self.assertEqual(client.call_args.kwargs['env']['GHOST_PROBE_TOKEN'], token)
            self.assertNotIn(token, str(launch.call_args.args) + str(client.call_args.args))
            self.assertTrue(server.stdin.closed)
        self.assertNotEqual(*tokens)

    def test_parent_eof_stops_both_servers_before_connecting(self):
        for filename in ('firefox-probe-server.py', 'firefox-screenshot-server.py'):
            driver = load_driver(filename)
            with tempfile.TemporaryDirectory() as folder:
                root = pathlib.Path(folder)
                (root / 'GhostUI').mkdir()
                (root / 'GhostUI' / 'userChrome.css').write_text('/* dummy */')
                driver.ROOT = root
                server = mock.Mock(stopping=False)
                process = mock.Mock()
                process.poll.return_value = 0

                def immediate_thread(*, target, daemon):
                    return mock.Mock(start=target)

                with mock.patch.object(driver, 'session_token', return_value=secrets.token_urlsafe(32)), \
                     mock.patch.object(driver, 'BridgeServer', return_value=server), \
                     mock.patch.object(driver.threading, 'Thread', side_effect=immediate_thread), \
                     mock.patch.object(driver.sys, 'stdin', mock.Mock(buffer=io.BytesIO(b''))), \
                     mock.patch.object(driver.subprocess, 'Popen', return_value=process), \
                     mock.patch.object(driver.socket, 'create_connection') as connect:
                    with self.assertRaisesRegex(RuntimeError, 'launcher closed'):
                        driver.main()
                connect.assert_not_called()
                server.server_close.assert_called_once()
                process.wait.assert_called_once_with(timeout=8)

    def test_bonjourr_merge_preserves_user_css_after_managed_block(self):
        driver = load_driver('install-ghost.py')
        added = '/* Ghost UI for Bonjourr */\n#time {color: red;}\n'
        before, after = '/* user before */\n', '\n/* user after */'
        merged = driver.merge_bonjourr_css(before + added + after, added)
        self.assertIn(before, merged)
        self.assertIn(after, merged)
        self.assertEqual(merged.count(added), 1)
        with self.assertRaisesRegex(RuntimeError, 'CSS was changed'):
            driver.merge_bonjourr_css(before + added.replace('red', 'blue') + after, added)


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.token = secrets.token_urlsafe(32)
        self.calls = []
        self.server = bridge.BridgeServer(('127.0.0.1', 0), self.command, self.token)
        self.port = self.server.server_port
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .02})
        self.thread.start()

    def command(self, name, parameters):
        self.calls.append((name, parameters))
        return {'value': 'dummy-result'}

    def tearDown(self):
        self.server.shutdown()
        self.thread.join(timeout=7)
        self.server.server_close()
        self.assertFalse(self.thread.is_alive())

    def wire(self, body=b'{"command":"dummy"}', extra=(), omit=()):
        headers = {'Host': f'127.0.0.1:{self.port}', 'Authorization': 'Bearer ' + self.token,
                   'Content-Type': 'application/json', 'Content-Length': str(len(body))}
        for name in omit:
            headers.pop(name, None)
        for name, value in extra:
            headers[name] = value
        return (b'POST / HTTP/1.1\r\n' + ''.join(k + ': ' + v + '\r\n' for k, v in headers.items()).encode()
                + b'\r\n' + body)

    def send(self, wire):
        with socket.create_connection(('127.0.0.1', self.port), timeout=7) as connection:
            connection.sendall(wire)
            response = b''
            while True:
                chunk = connection.recv(65536)
                if not chunk:
                    break
                response += chunk
        return response

    def test_authorized_real_client(self):
        with mock.patch.dict(os.environ, {'GHOST_PROBE_TOKEN': self.token}), mock.patch.object(
                ghost_probe, 'BRIDGE_URL', f'http://127.0.0.1:{self.port}'):
            self.assertEqual(ghost_probe.cmd('dummy'), 'dummy-result')
        self.assertEqual(len(self.calls), 1)

    def test_client_does_not_forward_credentials_on_redirect(self):
        class Redirect(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(handler):
                handler.send_response(302)
                handler.send_header('Location', f'http://127.0.0.1:{self.port}/')
                handler.send_header('Content-Length', '0')
                handler.end_headers()

        redirect = HTTPServer(('127.0.0.1', 0), Redirect)
        thread = threading.Thread(target=redirect.serve_forever, kwargs={'poll_interval': .02})
        thread.start()
        try:
            with mock.patch.dict(os.environ, {'GHOST_PROBE_TOKEN': self.token}), mock.patch.object(
                    ghost_probe, 'BRIDGE_URL', f'http://127.0.0.1:{redirect.server_port}'):
                with self.assertRaisesRegex(RuntimeError, 'redirects are not allowed'):
                    ghost_probe.cmd('dummy')
            self.assertEqual(self.calls, [])
        finally:
            redirect.shutdown()
            thread.join(timeout=2)
            redirect.server_close()

    def test_authentication_precedes_body_and_dispatch(self):
        fixtures = [self.wire(omit=['Authorization']),
                    self.wire(extra=[('Authorization', 'Bearer invalid')]),
                    self.wire(extra=[('Host', 'evil.invalid')]),
                    self.wire(extra=[('Origin', 'https://evil.invalid')]),
                    self.wire(extra=[('Origin', 'null')])]
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            for fixture in fixtures:
                # Deliberately withhold body: rejection must not await it.
                response = self.send(fixture.split(b'\r\n\r\n')[0] + b'\r\n\r\n')
                self.assertRegex(response.split(b'\r\n')[0], rb' (401|403) ')
                self.assertNotIn(self.token.encode(), response)
        self.assertNotIn(self.token, output.getvalue())
        self.assertEqual(self.calls, [])

    def test_framing_boundaries(self):
        fixtures = [self.wire(omit=['Content-Length'])]
        for length in ('bad', '-1', '0', str(bridge.MAX_BODY + 1), '9' * 40):
            fixtures.append(self.wire(extra=[('Content-Length', length)]))
        fixtures += [self.wire(extra=[('Transfer-Encoding', 'chunked')]),
                     self.wire(extra=[('Expect', '100-continue')]),
                     self.wire(extra=[('Content-Type', 'text/plain')]),
                     self.wire().replace(b'Content-Length:', b'Content-Length: 1\r\nContent-Length:'),
                     self.wire(body=b'[]'), self.wire(body=b'not json')]
        for fixture in fixtures:
            response = self.send(fixture)
            self.assertRegex(response.split(b'\r\n')[0], rb' 4[0-9][0-9] ')
        self.assertEqual(self.calls, [])
        self.assertIn(b'200 OK', self.send(self.wire()))
        self.assertEqual(len(self.calls), 1)

    def test_exact_size_limit(self):
        body = b'{"script":"' + b'x' * (bridge.MAX_BODY - 13) + b'"}'
        self.assertEqual(len(body), bridge.MAX_BODY)
        self.assertIn(b'200 OK', self.send(self.wire(body)))
        self.assertEqual(len(self.calls), 1)

    def test_incomplete_body_headers_and_slow_trickle_recover(self):
        with mock.patch.object(bridge, 'READ_SECONDS', .2):
            fixtures = [self.wire()[:-3], b'POST / HTTP/1.1\r\nHost: unfinished']
            for fixture in fixtures:
                started = time.monotonic()
                with socket.create_connection(('127.0.0.1', self.port), timeout=2) as connection:
                    connection.sendall(fixture)
                    try:
                        while connection.recv(4096):
                            pass
                    except (ConnectionResetError, ConnectionAbortedError):
                        pass
                self.assertLess(time.monotonic() - started, 1.5)
            # Progress every 50ms must not reset the absolute header deadline.
            with socket.create_connection(('127.0.0.1', self.port), timeout=2) as connection:
                connection.sendall(b'POST / HTTP/1.1\r\nX: ')
                for _ in range(7):
                    time.sleep(.05)
                    try:
                        connection.sendall(b'x')
                    except OSError:
                        break
                try:
                    self.assertEqual(connection.recv(4096), b'')
                except (ConnectionResetError, ConnectionAbortedError):
                    pass
        self.assertEqual(self.calls, [])
        self.assertIn(b'200 OK', self.send(self.wire()))
        self.assertEqual(len(self.calls), 1)


class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.profile = self.root / 'profile'
        self.profile.mkdir()
        self.original = b'// original\nuser_pref("browser.startup.page", 1);\nuser_pref("other", "preserve");\n'
        (self.profile / 'prefs.js').write_bytes(self.original)
        self.tx = bootstrap.Bootstrap(self.profile, self.root / 'state')
        self.settings = {'browser.startup.page': 3, 'marionette.port': 2831, 'marionette.enabled': True}

    def tearDown(self):
        self.temp.cleanup()

    def applied(self):
        (self.profile / 'prefs.js').write_text(
            'user_pref("other", "new user value");\n' +
            ''.join('user_pref(' + json.dumps(k) + ', ' + json.dumps(v) + ');\n'
                    for k, v in self.settings.items()), encoding='utf-8')

    def assert_restored(self):
        current = bootstrap.pref_lines((self.profile / 'prefs.js').read_bytes(), self.settings)
        self.assertEqual({k: v[0] for k, v in current.items()}, {'browser.startup.page': 1})
        self.assertFalse((self.profile / 'user.js').exists())

    def test_success_and_repeated_recovery(self):
        self.tx.begin(self.settings)
        self.applied()
        self.tx.recover(True)
        self.assert_restored()
        self.assertIn('new user value', (self.profile / 'prefs.js').read_text())
        self.assertIn('already', self.tx.recover(True))
        with self.assertRaises(RuntimeError):
            self.tx.begin(self.settings)

    def test_comment_containing_preference_text_is_not_removed(self):
        self.tx.begin(self.settings)
        self.applied()
        prefs = self.profile / 'prefs.js'
        comment = '// example user_pref("marionette.port", 2831);\n'
        prefs.write_bytes(comment.encode() + prefs.read_bytes())
        self.tx.recover(True)
        self.assertTrue(prefs.read_text().startswith(comment))
        self.assert_restored()

    def test_unsupported_pref_syntax_preserves_current_file(self):
        self.tx.begin(self.settings)
        self.applied()
        prefs = self.profile / 'prefs.js'
        prefs.write_bytes(prefs.read_bytes().replace(
            b'user_pref("marionette.port", 2831);', b'user_pref("marionette.port", 2831); // user edit'))
        changed = prefs.read_bytes()
        with self.assertRaisesRegex(RuntimeError, 'Unsupported temporary preference syntax'):
            self.tx.recover(True)
        self.assertEqual(prefs.read_bytes(), changed)

    def test_existing_journal_report_survives_rejected_begin(self):
        self.tx.directory.mkdir()
        report = self.tx.directory / 'report.json'
        report.write_bytes(b'previous record')
        with self.assertRaises(RuntimeError):
            try:
                self.tx.begin(self.settings)
            finally:
                bootstrap.finish(self.tx, None, None, lambda: None, {}, report)
        self.assertEqual(report.read_bytes(), b'previous record')

    def test_launch_failure(self):
        self.tx.begin(self.settings)
        bootstrap.finish(self.tx, None, None, lambda: None, {}, self.root / 'report.json')
        self.assertEqual((self.profile / 'prefs.js').read_bytes(), self.original)
        self.assert_restored()

    def test_connection_and_mid_operation_failure(self):
        for connected in (False, True):
            tx = bootstrap.Bootstrap(self.profile, self.root / ('state-' + str(connected)))
            tx.begin(self.settings)
            self.applied()
            process = mock.Mock()
            connection = mock.Mock() if connected else None
            quit_browser = mock.Mock(side_effect=OSError('dummy connection failure'))
            if connected:
                with self.assertRaises(RuntimeError):
                    bootstrap.finish(tx, process, connection, quit_browser, {}, self.root / 'report.json')
                connection.close.assert_called_once()
            else:
                bootstrap.finish(tx, process, None, quit_browser, {}, self.root / 'report.json')
            self.assert_restored()

    def test_report_failure_does_not_skip_cleanup(self):
        self.tx.begin(self.settings)
        self.applied()
        with self.assertRaisesRegex(RuntimeError, 'Report could not'):
            bootstrap.finish(self.tx, None, None, lambda: None, {}, self.root / 'missing' / 'report.json')
        self.assert_restored()

    def test_existing_and_changed_user_js_preserved(self):
        boot = self.profile / 'user.js'
        boot.write_bytes(b'// user original')
        with self.assertRaises(RuntimeError):
            self.tx.begin(self.settings)
        self.assertEqual(boot.read_bytes(), b'// user original')
        boot.unlink()
        self.tx.begin(self.settings)
        self.applied()
        boot.write_bytes(b'// changed by user')
        with self.assertRaisesRegex(RuntimeError, 'changed user.js preserved'):
            self.tx.recover(True)
        self.assertEqual(boot.read_bytes(), b'// changed by user')
        prefs = bootstrap.pref_lines((self.profile / 'prefs.js').read_bytes(), self.settings)
        self.assertEqual(prefs['browser.startup.page'][0], 1)

    def test_incomplete_backup_is_not_resumed(self):
        self.tx.directory.mkdir()
        self.tx.path.write_text('{"version":1}')
        with self.assertRaises(RuntimeError):
            self.tx.begin(self.settings)
        with self.assertRaises(RuntimeError):
            self.tx.recover(True)
        self.assertEqual((self.profile / 'prefs.js').read_bytes(), self.original)

    def test_external_pref_change_and_running_browser_need_manual_action(self):
        self.tx.begin(self.settings)
        self.applied()
        with self.assertRaisesRegex(RuntimeError, 'still running'):
            self.tx.recover(False)
        prefs = self.profile / 'prefs.js'
        prefs.write_text(prefs.read_text().replace('2831', '9999'))
        changed = prefs.read_bytes()
        with self.assertRaisesRegex(RuntimeError, 'changed externally'):
            self.tx.recover(True)
        self.assertEqual(prefs.read_bytes(), changed)

    def test_unexpected_missing_original_pref_needs_review(self):
        self.tx.begin(self.settings)
        prefs = self.profile / 'prefs.js'
        prefs.write_text('user_pref("other", "preserve");\n')
        changed = prefs.read_bytes()
        with self.assertRaisesRegex(RuntimeError, 'original preference disappeared'):
            self.tx.recover(True)
        self.assertEqual(prefs.read_bytes(), changed)

    def test_replaced_user_js_with_identical_content_is_preserved(self):
        self.tx.begin(self.settings)
        boot = self.profile / 'user.js'
        replacement = self.profile / 'replacement.js'
        replacement.write_bytes(boot.read_bytes())
        replacement.replace(boot)
        with self.assertRaisesRegex(RuntimeError, 'changed user.js preserved'):
            self.tx.recover(True)
        self.assertTrue(boot.exists())

    def test_success_keeps_only_installed_presentation_preferences(self):
        settings = dict(self.settings, **{'sidebar.visibility': 'expand-on-hover'})
        self.tx.begin(settings)
        self.applied()
        prefs = self.profile / 'prefs.js'
        prefs.write_text(prefs.read_text() + 'user_pref("sidebar.visibility", "expand-on-hover");\n')
        self.tx.keep_preferences(['sidebar.visibility'])
        self.tx.recover(True)
        self.assert_restored()
        self.assertIn('expand-on-hover', prefs.read_text())

    def test_driver_faults_always_reach_cleanup(self):
        # Load the actual drivers with __main__ disabled. All browser/socket work
        # is mocked and every profile/report/style lives under the fixture root.
        source = pathlib.Path(__file__).resolve().parent
        for filename in ('install-ghost.py', 'restore-bonjourr-search.py'):
            for fault in ('launch', 'connection', 'operation', 'report'):
                case = self.root / (filename + '-' + fault)
                ghost = case / 'GhostUI'
                ghost.mkdir(parents=True)
                (ghost / 'userChrome.css').write_text('/* dummy installed style */')
                profile = case / 'profile'
                (profile / 'chrome').mkdir(parents=True)
                (profile / 'prefs.js').write_bytes(self.original)
                (profile / 'chrome' / 'userChrome.css').write_text('/* original */')
                spec = importlib.util.spec_from_file_location('fixture_driver', source / filename)
                driver = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(driver)
                driver.ROOT = case
                process = mock.Mock()
                connection = mock.Mock()
                popen = mock.Mock(return_value=process)
                connect = mock.Mock(return_value=connection)
                if fault == 'launch':
                    popen.side_effect = OSError('fixture launch failure')
                if fault == 'connection':
                    connect.side_effect = OSError('fixture connection unavailable')
                real_write = bootstrap.atomic_write

                def write(path, data):
                    if fault == 'report' and path.name in ('installation-check.json', 'search-restored.json'):
                        raise OSError('fixture report failure')
                    return real_write(path, data)

                with mock.patch('sys.argv', [filename, '--profile', str(profile), '--state', str(case/'state')]), \
                     mock.patch.object(driver.subprocess, 'Popen', popen), \
                     mock.patch.object(driver.socket, 'create_connection', connect), \
                     mock.patch.object(driver.time, 'sleep'), \
                     mock.patch.object(driver, 'receive'), \
                     mock.patch.object(driver, 'cmd', side_effect=RuntimeError('fixture operation failure')), \
                     mock.patch.object(bootstrap, 'atomic_write', side_effect=write):
                    with self.assertRaises(Exception):
                        driver.main()
                self.assertFalse((profile / 'user.js').exists(), (filename, fault))
                self.assertEqual((profile / 'prefs.js').read_bytes(), self.original)
                state = json.loads((case / 'state' / 'state.json').read_text())
                self.assertEqual(state['status'], 'complete')


if __name__ == '__main__':
    unittest.main()
