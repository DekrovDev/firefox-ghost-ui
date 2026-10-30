import json
import pathlib
import shutil
import socket
import subprocess
import time
import sys
import tempfile
import threading
from ghost_bridge import BridgeServer, session_token


ROOT = pathlib.Path(__file__).resolve().parent
connection = None
sequence = 0

def receive():
    prefix = b''
    while not prefix.endswith(b':'):
        chunk = connection.recv(1)
        if not chunk:
            raise RuntimeError('Firefox disconnected')
        prefix += chunk
    remaining = int(prefix[:-1])
    data = b''
    while remaining:
        chunk = connection.recv(remaining)
        if not chunk:
            raise RuntimeError('Firefox disconnected mid-message')
        data += chunk
        remaining -= len(chunk)
    return json.loads(data)

def command(name, parameters):
    global sequence
    sequence += 1
    data = json.dumps([0, sequence, name, parameters]).encode()
    connection.sendall(str(len(data)).encode() + b':' + data)
    result = receive()
    if result[2]:
        raise RuntimeError(json.dumps(result[2]))
    return result[3]


def main():
    global connection, sequence
    token = session_token()
    connection = None
    sequence = 0
    process = None
    server = BridgeServer(('127.0.0.1',2832),command,token)
    profile_dir = None
    stopping = threading.Event()

    def watch_parent():
        try:
            sys.stdin.buffer.read(1)  # STOP byte or EOF when the launcher exits.
        except OSError:
            pass
        stopping.set()
        server.stopping = True
        if connection is not None:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    threading.Thread(target=watch_parent, daemon=True).start()
    try:
        profile_dir = tempfile.TemporaryDirectory(prefix='ghost-probe-')
        PROFILE = pathlib.Path(profile_dir.name)
        CHROME=PROFILE/'chrome'
        CHROME.mkdir(exist_ok=True)
        css = ROOT/'GhostUI'/'userChrome.css'
        if not css.is_file():
            css = ROOT.parents[1]/'theme'/'userChrome.css'
        shutil.copy2(css,CHROME/'userChrome.css')
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        prefs = {
            'marionette.port': port,
            'browser.shell.checkDefaultBrowser': False,
            'browser.startup.page': 0,
            'browser.startup.homepage_override.mstone': 'ignore',
            'browser.tabs.inTitlebar': 1,
            'sidebar.revamp': True,
            'sidebar.verticalTabs': True,
            'sidebar.visibility': 'expand-on-hover',
            'browser.aboutwelcome.enabled': False,
            'datareporting.policy.dataSubmissionPolicyBypassNotification': True,
            'toolkit.legacyUserProfileCustomizations.stylesheets': False,
        }
        (PROFILE / 'user.js').write_text('\n'.join(f'user_pref({json.dumps(k)}, {json.dumps(v)});' for k, v in prefs.items()), encoding='utf-8')
        process = subprocess.Popen([r'C:\Program Files\Mozilla Firefox\firefox.exe', '-headless', '-no-remote', '-profile', str(PROFILE), '-marionette', '--remote-allow-system-access', 'about:blank'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        for _ in range(100):
            if stopping.is_set():
                raise RuntimeError('Probe launcher closed during startup')
            if process.poll() is not None:
                raise RuntimeError(f'Test Firefox exited: {process.returncode}')
            try:
                connection = socket.create_connection(('127.0.0.1',port),timeout=1)
                connection.settimeout(40)
                break
            except OSError:
                time.sleep(.2)
        if connection is None:
            raise RuntimeError('Test Firefox did not start Marionette')
        receive()
        command('WebDriver:NewSession', {'capabilities':{'alwaysMatch':{}}})
        command('Marionette:SetContext',{'value':'chrome'})
        command('WebDriver:SetWindowRect',{'width':1440,'height':1000})
        server.timeout=1
        if stopping.is_set():
            raise RuntimeError('Probe launcher closed during startup')
        print('READY: isolated Firefox probe on localhost:2832',flush=True)
        while not server.stopping and not stopping.is_set():
            server.handle_request()
    finally:
        if server is not None:
            server.server_close()
        if connection:
            try:
                command('Marionette:Quit',{'flags':['eForceQuit']})
            except Exception:
                pass
            connection.close()
        if process is not None:
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=5)
        if profile_dir is not None and (process is None or process.poll() is not None):
            profile_dir.cleanup()


if __name__ == "__main__":
    main()
