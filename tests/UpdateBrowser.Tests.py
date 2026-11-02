"""Real Firefox/MSAA update-card fixture, only on disposable hosted Windows CI.

Uses an authored temporary extension, not third-party Bonjourr or a personal
profile. No live release API, download or installation is used by this fixture.
"""
import argparse
import ctypes
from datetime import datetime, timezone
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
    (root / 'updates/updates.json').write_text(json.dumps({'Release': release, 'Disabled': True, 'CheckedUtc': datetime.now(timezone.utc).isoformat()}), encoding='utf-8')
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

    def start_browser(with_extension=True):
        nonlocal browser
        browser = toolbar.Firefox(args.firefox, ROOT / 'theme/userChrome.css', folder, headless=False)
        browser.browser_pid = int(browser.js('return Services.appinfo.processID;'))
        print(f'Firefox launcher PID={browser.process.pid}, browser PID={browser.browser_pid}', flush=True)
        browser.command('WebDriver:SetWindowRect', {'x': 0, 'y': 0, 'width': 1100, 'height': 820})
        origin = None
        if with_extension:
            browser.command('Addon:Install', {'path': str(extension), 'temporary': True})
            uuid = browser.js("return JSON.parse(Services.prefs.getStringPref('extensions.webextensions.uuids'))[arguments[0]];", [addon_id])
            assert re.fullmatch(r'[0-9a-f-]{36}', uuid), 'Missing temporary extension origin'
            origin = f'moz-extension://{uuid}/'
        profile = folder / 'profile'
        payload = profile / 'chrome/ghost-ui'
        payload.mkdir(exist_ok=True)
        state = {'version': '1.1.2'}
        if origin:
            state['bonjourrOrigin'] = origin
        (payload / 'state.json').write_text(json.dumps(state), encoding='utf-8')
        # No stylesheet is needed to register this profile or identify its page.
        (payload / 'userContent.css').unlink(missing_ok=True)
        (root / 'handles/installed.json').write_text(json.dumps({'profiles': [str(profile)], 'executable': str(args.actor.resolve())}), encoding='utf-8')
        browser.command('Marionette:SetContext', {'value': 'content'})
        browser.command('WebDriver:Navigate', {'url': origin + 'index.html' if origin else 'about:newtab'})
        return origin

    def start_actor(expect_card=True):
        nonlocal actor
        actor = toolbar.JobProcess([str(args.actor), str(root), str(browser.browser_pid)])
        wait(lambda s: True, 'Actor did not report state')
        command('focus')
        # A focus ack is not the same as the OS foreground having moved; the
        # card rule requires the Firefox window itself to be foreground, which
        # the product reports independently once it adopts the window as owner.
        # An in-session dismissal ('Later') makes the card stay hidden on
        # purpose, so this condition only applies before any dismissal.
        if expect_card:
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
        wait(lambda s: not s['visible'] and s['launcherVisible'], 'Unrelated page did not retain only the manual entry')
        browser.command('WebDriver:Navigate', {'url': origin + 'index.html'})
        wait(lambda s: s['visible'], 'Card did not return to the start page')
        command('other')
        wait(lambda s: not s['visible'], 'Card remained over another application')
        command('focus')
        wait(lambda s: s['visible'], 'Card did not return with Firefox focus')
        command('later')
        wait(lambda s: not s['visible'], 'Later did not dismiss the card')
        stop_actor()
        # Same Firefox process and session, so the dismissed card correctly
        # stays hidden and the foreground condition no longer applies.
        start_actor(False)
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
        command('later')
        command('profile-current')
        wait(lambda s: s['launcherVisible'] and not s['visible'], 'Disabled checks have no settings launcher after updating')
        command('open-settings')
        state = wait(lambda s: s['visible'] and s['title'] == 'This profile is up to date', 'Settings did not open')
        assert not state['autoChecks'] and state['checkEnabled'], state
        command('enable-checks')
        assert wait(lambda s: s['autoChecks'], 'Daily checks could not be enabled')['autoChecks']
        assert not json.loads((root / 'updates/updates.json').read_text(encoding='utf-8'))['Disabled']
        command('later')
        wait(lambda s: not s['visible'] and s['launcherVisible'], 'Up-to-date profile lost its manual entry')
        stop_actor()
        browser.close()
        browser = None
        start_browser(False)
        start_actor()
        state = wait(lambda s: s['visible'] and s['origin'] == 'about:newtab', 'Native Firefox new-tab card did not appear without Bonjourr or CSS')
        assert state['profiles'] == 1 and state['inside'], state
        browser.command('WebDriver:Navigate', {'url': 'about:home'})
        wait(lambda s: s['visible'] and s['origin'] == 'about:home', 'Native Firefox home card did not appear')
        browser.command('WebDriver:Navigate', {'url': 'about:blank'})
        wait(lambda s: s['launcherVisible'] and not s['visible'], 'Blank page automatically expanded the card')
        command('open-settings')
        state = wait(lambda s: s['visible'] and s['checkEnabled'], 'Manual settings unavailable on an ordinary tab')
        assert state['autoChecks'], state
        command('install')
        wait(lambda s: s['confirming'], 'Native profile installation skipped confirmation')
        command('cancel')
        wait(lambda s: not s['approved'] and not s['confirming'], 'Native profile cancellation failed')
        command('later')
        browser.command('WebDriver:Navigate', {'url': 'about:newtab'})
        time.sleep(2)
        assert not wait(lambda s: s['launcherVisible'], 'Native manual entry vanished after Later')['visible']
        # Native URLs cannot select between two registered profiles.
        second = root / 'second-profile'
        (second / 'chrome/ghost-ui').mkdir(parents=True)
        (second / 'prefs.js').write_text('// synthetic profile', encoding='utf-8')
        (second / 'chrome/ghost-ui/state.json').write_text('{"version":"1.2.1"}', encoding='utf-8')
        registration = root / 'handles/installed.json'
        registered = json.loads(registration.read_text(encoding='utf-8'))
        registered['profiles'].append(str(second))
        registration.write_text(json.dumps(registered), encoding='utf-8')
        wait(lambda s: s['profiles'] == 2, 'A profile without optional CSS was excluded')
        command('open-settings')
        state = wait(lambda s: s['visible'], 'Ambiguous-profile settings did not open')
        assert state['selectedProfile'] == -1 and not state['primaryEnabled'], state
        command('select-second')
        wait(lambda s: s['primaryEnabled'], 'Explicit profile selection did not enable installation')
        command('install')
        state = wait(lambda s: s['confirming'], 'Selected destination did not require confirmation')
        assert str(second) in state['destination'] and not state['approved'], state
        command('cancel')
        browser.command('Marionette:SetContext', {'value': 'chrome'})
        browser.command('WebDriver:SetWindowRect', {'x': 0, 'y': 0, 'width': 400, 'height': 420})
        wait(lambda s: s['visible'] and s['inside'] and s['browserWidth'] <= 420, 'Small-window manual card escaped Firefox')
        browser.command('WebDriver:SetWindowRect', {'x': 0, 'y': 0, 'width': 1100, 'height': 820})
        wait(lambda s: s['visible'] and s['inside'] and s['browserWidth'] >= 1000, 'Expanded manual card escaped Firefox')
        stop_actor()
        (root / 'updates/updates.json').write_text('{"Disabled":true}', encoding='utf-8')
        start_actor(False)
        wait(lambda s: s['profiles'] == 2 and s['launcherVisible'], 'No-release state lost manual access')
        command('open-settings')
        state = wait(lambda s: s['visible'] and s['checkEnabled'], 'First manual check is unreachable without cached metadata')
        assert state['title'] == 'Ghost UI updates' and not state['autoChecks'] and not state['approved'], state
        print('PASS: extension and native Firefox URL/MSAA, no-Bonjourr/no-CSS registration, native home/newtab automatic cards, manual access on other tabs, daily settings, foreground hiding, session/cache reminders, confirmation/cancellation and handoff recovery', flush=True)
    finally:
        if actor is not None:
            actor.close()
        if browser is not None:
            browser.close()


if __name__ == '__main__':
    main()
