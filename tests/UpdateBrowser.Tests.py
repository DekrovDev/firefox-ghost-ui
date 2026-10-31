"""Real Firefox/MSAA update-card fixture, only on disposable hosted Windows CI.

Uses an authored temporary extension, not third-party Bonjourr or a personal
profile. No live release API, download or installation is used by this fixture.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ghost_toolbar', ROOT / 'tests/Toolbar.Tests.py')
toolbar = importlib.util.module_from_spec(spec)
spec.loader.exec_module(toolbar)


def main():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise RuntimeError('Use a disposable GitHub-hosted Windows runner')
    parser = argparse.ArgumentParser()
    parser.add_argument('--actor', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--firefox', default=r'C:\Program Files\Mozilla Firefox\firefox.exe')
    args = parser.parse_args()
    root = args.root.resolve()
    (root / 'updates').mkdir(exist_ok=True)
    (root / 'handles').mkdir(exist_ok=True)
    folder = root / 'browser'
    extension = root / 'startpage.xpi'
    addon_id = 'ghost-startpage-fixture@example.invalid'
    with zipfile.ZipFile(extension, 'w') as archive:
        archive.writestr('manifest.json', json.dumps({
            'manifest_version': 2, 'name': 'Ghost UI startpage fixture', 'version': '1.0',
            'browser_specific_settings': {'gecko': {'id': addon_id}},
            'chrome_url_overrides': {'newtab': 'index.html'},
        }))
        archive.writestr('index.html', '<!doctype html><title>Ghost UI startpage fixture</title>'
                         '<body style="background:#433d30;color:white;font:32px sans-serif">'
                         '<h1>Start page fixture</h1><p>Trusted temporary test extension.</p></body>')
    digest = hashlib.sha256(b'abc').hexdigest()
    release = {'Version': '9.9.9', 'Size': 3, 'Digest': digest, 'Summary': ['A short English update summary.']}
    (root / 'updates/updates.json').write_text(json.dumps({'Release': release, 'Disabled': True}), encoding='utf-8')
    browser = None
    actor = None
    command_id = 0
    last = {}

    def wait(predicate, message, timeout=25):
        nonlocal last
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if actor.poll() is not None:
                raise AssertionError(f'Actor exited: {message}')
            try:
                last = json.loads((root / 'status.json').read_text(encoding='utf-8-sig'))
                if last.get('actor') == actor.pid and predicate(last):
                    return last
            except (OSError, ValueError):
                pass
            time.sleep(.2)
        raise AssertionError(f'{message}: {last}')

    def command(action):
        nonlocal command_id
        command_id += 1
        temp = root / 'command.tmp'
        temp.write_text(json.dumps({'id': command_id, 'action': action}), encoding='utf-8')
        temp.replace(root / 'command.json')
        return wait(lambda s: s['ack'] == command_id, f'Command {action} not acknowledged')

    def start_browser():
        nonlocal browser
        browser = toolbar.Firefox(args.firefox, ROOT / 'theme/userChrome.css', folder, headless=False)
        browser.command('WebDriver:SetWindowRect', {'x': 0, 'y': 0, 'width': 1100, 'height': 820})
        browser.command('Addon:Install', {'path': str(extension), 'temporary': True})
        uuid = browser.js("return JSON.parse(Services.prefs.getStringPref('extensions.webextensions.uuids'))[arguments[0]];", [addon_id])
        assert re.fullmatch(r'[0-9a-f-]{36}', uuid), 'Missing temporary extension origin'
        origin = f'moz-extension://{uuid}/'
        profile = folder / 'profile'
        payload = profile / 'chrome/ghost-ui'
        payload.mkdir(exist_ok=True)
        (payload / 'state.json').write_text(json.dumps({'version': '1.1.2'}), encoding='utf-8')
        (payload / 'userContent.css').write_text(f'@-moz-document url-prefix("{origin}") {{}}', encoding='utf-8')
        (root / 'handles/installed.json').write_text(json.dumps({'profiles': [str(profile)], 'executable': str(args.actor.resolve())}), encoding='utf-8')
        browser.command('Marionette:SetContext', {'value': 'content'})
        browser.command('WebDriver:Navigate', {'url': origin + 'index.html'})
        return origin

    def start_actor():
        nonlocal actor
        actor = toolbar.JobProcess([str(args.actor), str(root), str(browser.process.pid)])
        wait(lambda s: True, 'Actor did not report state')
        command('focus')
        # A focus ack is not the same as the OS foreground having moved; the
        # card rule requires the Firefox window itself to be foreground, which
        # the product reports independently once it adopts the window as owner.
        wait(lambda s: s.get('ownerIsForeground'), 'Firefox window did not take foreground')

    def stop_actor():
        nonlocal actor
        actor.close()
        actor = None

    try:
        origin = start_browser()
        start_actor()
        state = wait(lambda s: s['visible'], 'Update card did not appear over the real extension page')
        assert state['origin'] == origin and state['inside'], state
        assert state['primary'] == 'Download' and state['manual'] == 'Download manually' and state['manualEnabled'], state
        assert 'A short English update summary.' in state['summary'], state
        browser.command('WebDriver:Navigate', {'url': 'about:blank'})
        wait(lambda s: not s['visible'], 'Card remained over an unrelated page')
        browser.command('WebDriver:Navigate', {'url': origin + 'index.html'})
        wait(lambda s: s['visible'], 'Card did not return to the start page')
        command('other')
        wait(lambda s: not s['visible'], 'Card remained over another application')
        command('focus')
        wait(lambda s: s['visible'], 'Card did not return with Firefox focus')
        command('later')
        wait(lambda s: not s['visible'], 'Later did not dismiss the card')
        stop_actor()
        start_actor()
        time.sleep(2)
        assert not wait(lambda s: s['profiles'] == 1, 'Profile not loaded')['visible'], 'Companion restart repeated a dismissed session'
        stop_actor()
        browser.close()
        browser = None
        (root / f'updates/package-9.9.9-{digest}.zip').write_bytes(b'abc')
        start_browser()
        start_actor()
        state = wait(lambda s: s['visible'] and s['ready'], 'New Firefox session did not remind about the verified cache')
        assert state['primary'] == 'Install update', state
        command('install')
        assert wait(lambda s: s['confirming'], 'Install did not ask for confirmation')['approved'] is False
        command('approve')
        wait(lambda s: s['approved'], 'Explicit confirmation did not create in-memory approval')
        command('cancel')
        wait(lambda s: not s['approved'] and not s['confirming'], 'Cancel did not revoke approval')
        assert not json.loads((root / 'updates/updates.json').read_text(encoding='utf-8')).get('consent'), 'Approval was persisted'
        command('install')
        command('approve')
        # Simulate Firefox reopening between the parent check and the helper
        # check. The real helper must reject and the still-running card recover.
        command('handoff-failure')
        wait(lambda s: not s['launching'] and s['visible'] and 'not installed' in s['status'], 'Failed pre-stop helper did not restore a retryable card')
        print('PASS: real Firefox extension URL/MSAA, native card bounds/summary/manual button, page/focus hiding, session dismissal, cached reminder, confirmation/cancellation and pre-stop helper failure recovery', flush=True)
    finally:
        if actor is not None:
            actor.close()
        if browser is not None:
            browser.close()


if __name__ == '__main__':
    main()
