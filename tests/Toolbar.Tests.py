"""Native Firefox toolbar regression checks in a disposable, headless profile.

Run: python tests/Toolbar.Tests.py --firefox "C:/Program Files/Mozilla Firefox/firefox.exe"
No personal profile, extension settings, accounts or browsing data are used.
Windows 10+ only: Firefox is created inside a non-inherited kill-on-close Job
object, so driver death closes the job even before Python cleanup can run.
"""
import atexit
import argparse
import ctypes
import json
import pathlib
import os
import shutil
import socket
import subprocess
import tempfile
import time
import sys
import zipfile
import threading
from urllib.parse import quote
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = pathlib.Path(__file__).resolve().parents[1]


class JobProcess:
    """Create a process atomically inside our Windows kill-on-close job.

    Using PROC_THREAD_ATTRIBUTE_JOB_LIST avoids the Popen/AssignProcess race.
    Fail closed if job attributes are unsupported; never start unowned Firefox.
    """
    def __init__(self, args):
        if os.name != 'nt':
            raise OSError('These browser checks require Windows 10+ job support')
        from ctypes import wintypes as w
        self.args = list(map(str, args))
        self.returncode = None
        self._job = None
        self._handle = None
        self.api = ctypes.WinDLL('kernel32', use_last_error=True)
        size_t = ctypes.c_size_t

        class BasicLimits(ctypes.Structure):
            _fields_ = [('process_time', ctypes.c_longlong), ('job_time', ctypes.c_longlong),
                        ('flags', w.DWORD), ('min_working_set', size_t), ('max_working_set', size_t),
                        ('active_processes', w.DWORD), ('affinity', size_t),
                        ('priority', w.DWORD), ('scheduling', w.DWORD)]

        class ExtendedLimits(ctypes.Structure):
            _fields_ = [('basic', BasicLimits), ('io', ctypes.c_ulonglong * 6),
                        ('process_memory', size_t), ('job_memory', size_t),
                        ('peak_process_memory', size_t), ('peak_job_memory', size_t)]

        class StartupInfo(ctypes.Structure):
            _fields_ = [('cb', w.DWORD), ('reserved', w.LPWSTR), ('desktop', w.LPWSTR), ('title', w.LPWSTR),
                        ('x', w.DWORD), ('y', w.DWORD), ('width', w.DWORD), ('height', w.DWORD),
                        ('xchars', w.DWORD), ('ychars', w.DWORD), ('fill', w.DWORD), ('flags', w.DWORD),
                        ('show', w.WORD), ('reserved_size', w.WORD), ('reserved_bytes', ctypes.POINTER(w.BYTE)),
                        ('stdin', w.HANDLE), ('stdout', w.HANDLE), ('stderr', w.HANDLE)]

        class StartupInfoEx(ctypes.Structure):
            _fields_ = [('startup', StartupInfo), ('attributes', w.LPVOID)]

        class ProcessInfo(ctypes.Structure):
            _fields_ = [('process', w.HANDLE), ('thread', w.HANDLE), ('pid', w.DWORD), ('tid', w.DWORD)]

        signatures = {
            'CreateJobObjectW': ([w.LPVOID, w.LPCWSTR], w.HANDLE),
            'SetInformationJobObject': ([w.HANDLE, ctypes.c_int, w.LPVOID, w.DWORD], w.BOOL),
            'InitializeProcThreadAttributeList': ([w.LPVOID, w.DWORD, w.DWORD, ctypes.POINTER(size_t)], w.BOOL),
            'UpdateProcThreadAttribute': ([w.LPVOID, w.DWORD, size_t, w.LPVOID, size_t, w.LPVOID, w.LPVOID], w.BOOL),
            'DeleteProcThreadAttributeList': ([w.LPVOID], None),
            'CreateProcessW': ([w.LPCWSTR, w.LPWSTR, w.LPVOID, w.LPVOID, w.BOOL, w.DWORD,
                                w.LPVOID, w.LPCWSTR, w.LPVOID, ctypes.POINTER(ProcessInfo)], w.BOOL),
            'IsProcessInJob': ([w.HANDLE, w.HANDLE, ctypes.POINTER(w.BOOL)], w.BOOL),
            'CloseHandle': ([w.HANDLE], w.BOOL),
            'WaitForSingleObject': ([w.HANDLE, w.DWORD], w.DWORD),
            'GetExitCodeProcess': ([w.HANDLE, ctypes.POINTER(w.DWORD)], w.BOOL),
            'TerminateJobObject': ([w.HANDLE, w.UINT], w.BOOL),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = arguments, result
        attributes = None
        info = ProcessInfo()
        try:
            self._job = self.api.CreateJobObjectW(None, None) # Non-inheritable, unnamed.
            self.check(self._job)
            limits = ExtendedLimits()
            limits.basic.flags = 0x2000 # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway flags.
            self.check(self.api.SetInformationJobObject(self._job, 9, ctypes.byref(limits), ctypes.sizeof(limits)))
            size = size_t()
            self.api.InitializeProcThreadAttributeList(None, 1, 0, ctypes.byref(size))
            if not size.value:
                raise ctypes.WinError(ctypes.get_last_error())
            storage = ctypes.create_string_buffer(size.value)
            self.check(self.api.InitializeProcThreadAttributeList(storage, 1, 0, ctypes.byref(size)))
            attributes = storage
            jobs = (w.HANDLE * 1)(self._job)
            self.check(self.api.UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs,
                                                          ctypes.sizeof(jobs), None, None))
            startup = StartupInfoEx()
            startup.startup.cb = ctypes.sizeof(startup)
            startup.attributes = ctypes.cast(attributes, w.LPVOID)
            command = ctypes.create_unicode_buffer(subprocess.list2cmdline(self.args))
            executable = str(pathlib.Path(self.args[0]).resolve(strict=True))
            self.check(self.api.CreateProcessW(executable, command, None, None, False,
                       0x00080000 | 0x08000000, # EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
                       None, None, ctypes.byref(startup), ctypes.byref(info)))
            self._handle, self.pid = info.process, info.pid
            member = w.BOOL()
            self.check(self.api.IsProcessInJob(self._handle, self._job, ctypes.byref(member)))
            if not member.value:
                raise RuntimeError('Test Firefox was not assigned to its lifetime job')
            atexit.register(self.close)
        except BaseException:
            if self._handle is None and info.process:
                self._handle = info.process
            self.close()
            raise
        finally:
            if info.thread:
                self.api.CloseHandle(info.thread)
            if attributes is not None:
                self.api.DeleteProcThreadAttributeList(attributes)

    @staticmethod
    def check(result):
        if not result:
            raise ctypes.WinError(ctypes.get_last_error())
        return result

    def poll(self):
        if self._handle is None:
            return self.returncode
        result = self.api.WaitForSingleObject(self._handle, 0)
        if result == 258: # WAIT_TIMEOUT, still running.
            return None
        if result != 0:
            raise ctypes.WinError(ctypes.get_last_error())
        from ctypes import wintypes as w
        code = w.DWORD()
        self.check(self.api.GetExitCodeProcess(self._handle, ctypes.byref(code)))
        self.returncode = code.value
        return self.returncode

    def wait(self, timeout):
        if self._handle is None:
            return self.returncode
        result = self.api.WaitForSingleObject(self._handle, max(0, min(int(timeout * 1000), 0xFFFFFFFE)))
        if result == 258:
            raise subprocess.TimeoutExpired(self.args, timeout)
        if result != 0:
            raise ctypes.WinError(ctypes.get_last_error())
        return self.poll()

    def terminate(self):
        if self._job is not None:
            self.check(self.api.TerminateJobObject(self._job, 1))

    def close(self):
        atexit.unregister(self.close)
        if self._job is not None:
            self.check(self.api.CloseHandle(self._job))
            self._job = None # Kernel stops the entire owned tree, including Firefox children.
        if self._handle is not None:
            try:
                self.wait(8)
            finally:
                self.api.CloseHandle(self._handle)
                self._handle = None


class Firefox:
    def __init__(self, executable, css, folder):
        profile = folder / "profile"
        (profile / "chrome").mkdir(parents=True)
        shutil.copyfile(css, profile / "chrome" / "userChrome.css")
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        prefs = {
            "marionette.port": port,
            "toolkit.legacyUserProfileCustomizations.stylesheets": True,
            "sidebar.revamp": True,
            "sidebar.verticalTabs": True,
            "sidebar.visibility": "expand-on-hover",
            "browser.tabs.inTitlebar": 1,
            "browser.shell.checkDefaultBrowser": False,
            "browser.aboutwelcome.enabled": False,
            "browser.startup.homepage_override.mstone": "ignore",
            "browser.download.alwaysOpenPanel": False,
            "browser.download.panel.shown": True,
            # The fresh profile has no download history. Make the native
            # button visible for its coordinate-click check.
            "browser.download.autohideButton": False,
        }
        (profile / "user.js").write_text("\n".join(
            f"user_pref({json.dumps(k)}, {json.dumps(v)});" for k, v in prefs.items()
        ), encoding="utf8")
        self.process = JobProcess(
            [executable, "-headless", "-no-remote", "-profile", str(profile),
             "-marionette", "--remote-allow-system-access", "about:blank"],
        )
        self.connection = None
        self.sequence = 0
        try:
            for _ in range(100):
                if self.process.poll() is not None:
                    raise RuntimeError("Test Firefox exited during startup")
                try:
                    self.connection = socket.create_connection(("127.0.0.1", port), 1)
                    self.connection.settimeout(30)
                    break
                except OSError:
                    time.sleep(.2)
            if self.connection is None:
                raise RuntimeError("Test Firefox did not start")
            self.receive()
            self.command("WebDriver:NewSession", {"capabilities": {"alwaysMatch": {}}})
            self.command("Marionette:SetContext", {"value": "chrome"})
            self.command("WebDriver:SetWindowRect", {"width": 1440, "height": 1000})
        except BaseException:
            self.close()
            raise

    def receive(self):
        prefix = b""
        while not prefix.endswith(b":"):
            chunk = self.connection.recv(1)
            if not chunk:
                raise RuntimeError("Firefox disconnected")
            prefix += chunk
        size = int(prefix[:-1])
        body = b""
        while len(body) < size:
            chunk = self.connection.recv(size - len(body))
            if not chunk:
                raise RuntimeError("Firefox disconnected during response")
            body += chunk
        return json.loads(body)

    def command(self, name, parameters=None):
        self.sequence += 1
        body = json.dumps([0, self.sequence, name, parameters or {}]).encode()
        self.connection.sendall(str(len(body)).encode() + b":" + body)
        response = self.receive()
        if response[2]:
            raise RuntimeError(json.dumps(response[2]))
        value = response[3]
        return value.get("value", value) if isinstance(value, dict) else value

    def js(self, script, args=None):
        return self.command("WebDriver:ExecuteScript", {
            "script": script, "args": args or [], "newSandbox": True,
            "sandbox": "system", "scriptTimeout": 30000,
        })

    def move(self, x, y):
        self.command("WebDriver:PerformActions", {"actions": [{
            "type": "pointer", "id": "audit-mouse", "parameters": {"pointerType": "mouse"},
            "actions": [{"type": "pointerMove", "origin": "viewport", "x": x, "y": y}],
        }]})

    def click(self, selector):
        point = self.js("const e=document.querySelector(arguments[0]);if(!e)throw Error('Missing '+arguments[0]);"
                        "const r=e.getBoundingClientRect(),x=Math.round(r.x+r.width/2),y=Math.round(r.y+r.height/2);"
                        "const hit=document.elementFromPoint(x,y);return {x,y,popup:!!e.closest('panel,menupopup'),"
                        "reachable:hit===e||e.contains(hit),hit:hit?.id||hit?.localName};", [selector])
        if point["popup"]:
            # XUL popups have a separate native widget and are not returned by
            # the main document's elementFromPoint in headless Firefox.
            element = self.command("WebDriver:FindElement", {"using": "css selector", "value": selector})
            self.command("WebDriver:ElementClick", {"id": next(iter(element.values()))})
            return
        assert point["reachable"], f"Pointer intercepted at {selector} by {point['hit']}"
        self.command("WebDriver:PerformActions", {"actions": [{
            "type": "pointer", "id": "audit-mouse", "parameters": {"pointerType": "mouse"},
            "actions": [{"type": "pointerMove", "origin": "viewport", "x": point["x"], "y": point["y"]},
                        {"type": "pointerDown", "button": 0}, {"type": "pointerUp", "button": 0}],
        }]})

    def edge_cross_click(self, dwell, x, y):
        # Keep the dwell inside a single native action sequence: separate RPCs
        # can accidentally push a 50 ms experiment past the 120 ms intent gate.
        self.command("WebDriver:PerformActions", {"actions": [{
            "type": "pointer", "id": "audit-mouse", "parameters": {"pointerType": "mouse"},
            "actions": [{"type": "pointerMove", "origin": "viewport", "x": 200, "y": 2},
                        {"type": "pause", "duration": round(dwell * 1000)},
                        {"type": "pointerMove", "origin": "viewport", "x": x, "y": y},
                        {"type": "pointerDown", "button": 0}, {"type": "pointerUp", "button": 0}],
        }]})

    def keys(self, actions):
        self.command("WebDriver:PerformActions", {"actions": [
            {"type": "key", "id": "audit-keyboard", "actions": actions}
        ]})

    def dismiss(self):
        self.js("for(const p of document.querySelectorAll('panel,menupopup'))"
                "if(p.state!=='closed')p.hidePopup();gURLBar.blur();return true;")
        self.move(400, 600)
        time.sleep(.15)

    def reveal(self):
        self.move(700, 2)
        time.sleep(.7)

    def wait(self, script, args=None, timeout=6):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            value = self.js(script, args)
            if value:
                return value
            time.sleep(.05)
        raise AssertionError(f"Timed out: {script}")

    def content_tab(self, uri):
        # Menu commands can select a different tab without changing Marionette's
        # current handle. Follow the actual destination before inspecting it.
        self.command("Marionette:SetContext", {"value": "content"})
        for handle in self.command("WebDriver:GetWindowHandles"):
            self.command("WebDriver:SwitchToWindow", {"handle": handle})
            if self.js("return location.href.startsWith(arguments[0]);", [uri]):
                return
        raise AssertionError(f"Destination tab not found: {uri}")

    def close(self):
        try:
            if self.connection:
                try:
                    self.command("Marionette:Quit", {"flags": ["eForceQuit"]})
                except (OSError, RuntimeError):
                    pass
                finally:
                    self.connection.close()
                    self.connection = None
        finally:
            self.process.close()


def job_driver(path):
    """Dummy process tree for the bounded lifetime check; never starts Firefox."""
    code = (
        "import json,os,pathlib,subprocess,sys,time;"
        "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)']);"
        "path=pathlib.Path(sys.argv[1]);temporary=path.with_suffix('.tmp');"
        "temporary.write_text(json.dumps([os.getpid(),child.pid]));temporary.replace(path);"
        "time.sleep(120)"
    )
    process = JobProcess([sys.executable, '-c', code, str(path)])
    try:
        time.sleep(120)
    finally:
        process.close()


def check_job_lifetime():
    """Kill only our dummy driver and verify that its entire owned job exits."""
    if os.name != 'nt':
        raise OSError('The job lifetime check requires Windows 10+')
    from ctypes import wintypes as w
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.OpenProcess.argtypes, api.OpenProcess.restype = [w.DWORD, w.BOOL, w.DWORD], w.HANDLE
    api.WaitForSingleObject.argtypes, api.WaitForSingleObject.restype = [w.HANDLE, w.DWORD], w.DWORD
    api.TerminateProcess.argtypes, api.TerminateProcess.restype = [w.HANDLE, w.UINT], w.BOOL
    api.CloseHandle.argtypes, api.CloseHandle.restype = [w.HANDLE], w.BOOL
    with tempfile.TemporaryDirectory(prefix='ghost-job-test-') as folder:
        path = pathlib.Path(folder) / 'owned-processes.json'
        driver = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).resolve()), '--job-driver', str(path)],
                                  stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                  creationflags=subprocess.CREATE_NO_WINDOW)
        handles = []
        try:
            deadline = time.monotonic() + 8
            while not path.exists() and time.monotonic() < deadline and driver.poll() is None:
                time.sleep(.02)
            if not path.exists():
                raise RuntimeError('Dummy job driver did not create its process tree')
            pids = json.loads(path.read_text())
            if not isinstance(pids, list) or len(pids) != 2 or any(type(pid) is not int or pid <= 0 for pid in pids):
                raise RuntimeError('Invalid dummy process ownership record')
            for pid in pids:
                handle = api.OpenProcess(0x100001, False, pid) # SYNCHRONIZE | PROCESS_TERMINATE
                JobProcess.check(handle)
                handles.append(handle)
            driver.terminate() # Deliberately bypass the dummy driver's Python cleanup.
            driver.wait(timeout=5)
            for handle in handles:
                if api.WaitForSingleObject(handle, 5000) != 0:
                    raise RuntimeError('Dummy descendant survived hard driver termination')
            print('PASS: hard driver termination closes the job and stops both dummy descendants', flush=True)
        finally:
            try:
                if driver.poll() is None:
                    driver.terminate()
                driver.wait(timeout=5)
            finally:
                for handle in handles:
                    if api.WaitForSingleObject(handle, 0) == 258:
                        api.TerminateProcess(handle, 1)
                        api.WaitForSingleObject(handle, 5000)
                    api.CloseHandle(handle)


def run(f, folder):
    passed = 0

    def check(condition, description):
        nonlocal passed
        assert condition, description
        passed += 1

    # One unpinned extension keeps the list available; another supplies a real popup.
    for name in ["pinned", "listed"]:
        xpi = folder / f"{name}.xpi"
        with zipfile.ZipFile(xpi, "w") as archive:
            archive.writestr("manifest.json", json.dumps({
                "manifest_version": 2, "name": f"Ghost audit {name}", "version": "1.0",
                "browser_specific_settings": {"gecko": {"id": f"ghost-{name}@example.invalid"}},
                "browser_action": {"default_popup": "popup.html"},
            }))
            archive.writestr("popup.html", "<!doctype html><body style='width:320px;height:200px'>"
                             "<h1>Ghost UI popup</h1><button>Test control</button></body>")
        f.command("Addon:Install", {"path": str(xpi), "temporary": True})
    f.js("for(const id of ['stop-reload-button','bookmarks-menu-button','downloads-button','fxa-toolbar-menu-button',"
         "'ghost-pinned_example_invalid-browser-action'])"
         "CustomizableUI.addWidgetToArea(id,CustomizableUI.AREA_NAVBAR);return true;")
    time.sleep(.6)
    print("Firefox", f.js("return Services.appinfo.version;"), flush=True)

    # Reproduce crossing the edge on the way to a website's header. An idle
    # hit test misses the old bug: hover made the invisible nav rectangle
    # clickable immediately, and that rectangle then sustained its own hover.
    page = """<!doctype html><title>Header trajectory</title>
    <style>body{margin:0}button{position:fixed;top:20px;height:32px}
    #menu{left:20px;width:48px}#logo{left:100px;width:110px}
    #search{left:40%;width:140px}#create{right:152px;width:64px}</style>
    <button id=menu>Menu</button><button id=logo>Logo</button>
    <button id=search>Search</button><button id=create>Create</button>
    <script>document.addEventListener('click',e=>{
    if(e.target.tagName==='BUTTON')document.documentElement.dataset.lastClick=e.target.id})</script>"""
    f.command("Marionette:SetContext", {"value": "content"})
    f.command("WebDriver:Navigate", {"url": "data:text/html," + quote(page)})
    points = f.js("return [...document.querySelectorAll('button')].map(e=>{"
                  "const r=e.getBoundingClientRect();return {id:e.id,x:r.x+r.width/2,y:r.y+r.height/2}});")
    f.command("Marionette:SetContext", {"value": "chrome"})
    origin = f.js("const r=gBrowser.selectedBrowser.getBoundingClientRect();return {x:r.x,y:r.y};")
    for dwell in [.01, .05]:
        for point in points:
            f.dismiss()
            time.sleep(2.65)
            # The rightmost page control sits under the independent download
            # island. Use the same central edge sensor for all trajectories.
            x, y = round(origin["x"] + point["x"]), round(origin["y"] + point["y"])
            f.edge_cross_click(dwell, x, y)
            f.command("Marionette:SetContext", {"value": "content"})
            try:
                received = f.js("return document.documentElement.dataset.lastClick===arguments[0];", [point["id"]])
                f.js("delete document.documentElement.dataset.lastClick;return true;")
            finally:
                f.command("Marionette:SetContext", {"value": "chrome"})
            if not received:
                detail = f.js("const d=document.getElementById('downloads-button'),s=getComputedStyle(d);"
                              "return {hit:document.elementFromPoint(arguments[0],arguments[1])?.id,"
                              "url:getComputedStyle(gURLBar).opacity,download:{opacity:s.opacity,visibility:s.visibility,"
                              "pointer:s.pointerEvents,delay:s.transitionDelay,hover:d.matches(':hover')},"
                              "hover:document.getElementById('navigator-toolbox').matches(':hover')};", [x, y])
                raise AssertionError(f"Header click blocked after {dwell}s edge crossing: {point['id']}: {detail}")
            check(received, f"Page header click: {point['id']}")
            check(f.js("return !document.getElementById('navigator-toolbox').matches(':hover');"),
                  f"Invisible toolbar sustains hover over page {point['id']}")
            time.sleep(.4)
            check(f.js("return getComputedStyle(gURLBar).opacity==='0';"),
                  f"Cancelled edge crossing later reveals toolbar over {point['id']}")
    f.dismiss()
    time.sleep(2.65)
    f.move(200, 2)
    time.sleep(.2)
    middle = f.js("const r=document.getElementById('nav-bar').getBoundingClientRect();"
                  "return {x:Math.round(r.x+r.width/2),y:Math.round(r.y+r.height/2)};")
    f.move(middle["x"], middle["y"])
    check(f.js("return document.getElementById('navigator-toolbox').matches(':hover')&&"
               "getComputedStyle(document.getElementById('nav-bar'),'::before').pointerEvents==='auto';"),
          "Moving into the visible glass during opening cancels deliberate hover")
    time.sleep(.55)
    check(f.js("return getComputedStyle(gURLBar).opacity==='1';"),
          "Early entry into the visible shell fails to finish opening")
    f.dismiss()
    time.sleep(2.65)
    sensor = f.js("const s=getComputedStyle(document.getElementById('nav-bar'),'::after');"
                  "const r=document.getElementById('nav-bar').getBoundingClientRect();"
                  "return {width:parseFloat(s.width),height:parseFloat(s.height),x:Math.round(r.x+r.width/2),y:Math.round(r.y+6)};")
    check(sensor["width"] == 128 and sensor["height"] == 12, "Pill activation area changed")
    f.move(sensor["x"], sensor["y"])
    time.sleep(.75)
    check(f.js("return getComputedStyle(gURLBar).opacity==='1'&&"
               "getComputedStyle(document.getElementById('nav-bar')).pointerEvents==='auto';"),
          "Deliberate pill hover fails to open the toolbar")
    f.click("#PanelUI-menu-button")
    check(f.wait("return document.getElementById('appMenu-popup').state==='open';"),
          "Toolbar opened by pill is not interactive")
    print("PASS: page header clicks after edge crossings / growing bridge / fixed pill sensor", flush=True)

    # Stable native anchor boxes are essential during the reveal, not only afterwards.
    f.dismiss()
    time.sleep(2.5)
    f.move(700, 2)
    time.sleep(.25)
    for selector in ["#PanelUI-button", "#unified-extensions-button", "#bookmarks-menu-button",
                     "#ghost-pinned_example_invalid-browser-action", "#back-button"]:
        check(f.js("return !!document.querySelector(arguments[0]);", [selector]), f"Missing anchor: {selector}")
        style = f.js("const s=getComputedStyle(document.querySelector(arguments[0]));"
                     "return {scale:s.scale,translate:s.translate};", [selector])
        check(style == {"scale": "none", "translate": "none"}, f"Transformed popup anchor: {selector}: {style}")
    print("PASS: stationary hit targets during reveal", flush=True)

    f.dismiss()
    f.keys([{"type": "keyDown", "value": "\ue009"}, {"type": "keyDown", "value": "l"},
            {"type": "keyUp", "value": "l"}, {"type": "keyUp", "value": "\ue009"}])
    check(f.wait("return gURLBar.hasAttribute('focused')&&getComputedStyle(gURLBar).opacity==='1';"),
          "Ctrl+L did not expose and focus the address bar")
    f.keys([{"type": "keyDown", "value": "\ue00c"}, {"type": "keyUp", "value": "\ue00c"}])
    f.js("gURLBar.view.close();gURLBar.blur();gBrowser.selectedBrowser.focus();return true;")
    print("PASS: keyboard address focus / Escape", flush=True)

    class LocalPage(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            body = b"<title>Local submission</title><h1>Submission test</h1>"
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), LocalPage)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        for reduced in [False, True]:
            f.js("Services.prefs.setIntPref('ui.prefersReducedMotion',arguments[0]);return true;", [int(reduced)])
            f.move(400, 600)
            f.keys([{"type": "keyDown", "value": "\ue009"}, {"type": "keyDown", "value": "l"},
                    {"type": "keyUp", "value": "l"}, {"type": "keyUp", "value": "\ue009"}])
            address = f"http://127.0.0.1:{server.server_port}/?reduced={int(reduced)}"
            f.keys([action for char in address for action in
                    [{"type": "keyDown", "value": char}, {"type": "keyUp", "value": char}]])
            time.sleep(.35)
            f.keys([{"type": "keyDown", "value": "\ue007"}, {"type": "keyUp", "value": "\ue007"}])
            frames = []
            end = time.monotonic() + .6
            while time.monotonic() < end:
                frames.append(f.js("return {opacity:parseFloat(getComputedStyle(gURLBar).opacity),"
                                   "width:parseFloat(getComputedStyle(document.getElementById('nav-bar'),'::before').width)};"))
                time.sleep(.025)
            if not reduced:
                check(any(.01 < frame["opacity"] < .99 for frame in frames), "Missing smooth Enter fade")
                check(any(130 < frame["width"] < 1100 for frame in frames), "Missing Enter shell collapse")
            check(frames[-1]["opacity"] == 0 and abs(frames[-1]["width"] - 128) < 1,
                  f"Enter waited for hover grace, reduced={reduced}: {frames}")
            check(f.js("return !document.elementFromPoint(150,30)?.closest('#nav-bar');"),
                  "Submitted toolbar intercepts page clicks")
    finally:
        server.shutdown()
        server.server_close()
        f.js("Services.prefs.clearUserPref('ui.prefersReducedMotion');return true;")
    print("PASS: real Enter navigation / smooth fade / reduced motion / click-through", flush=True)

    f.keys([{"type": "keyDown", "value": "\ue009"}, {"type": "keyDown", "value": "l"},
            {"type": "keyUp", "value": "l"}, {"type": "keyUp", "value": "\ue009"}])
    f.js("gCustomizeMode.enter();return true;")
    f.wait("return document.documentElement.hasAttribute('customizing');")
    time.sleep(.4)
    check(f.js("return getComputedStyle(gURLBar).opacity==='1'&&"
               "[...document.querySelector('#nav-bar-customization-target').children]"
               ".filter(e=>e.getBoundingClientRect().width>0).every(e=>getComputedStyle(e).opacity==='1');"),
          "Recent editing hides draggable controls in customization")
    f.js("gCustomizeMode.exit();return true;")
    f.wait("return !document.documentElement.hasAttribute('customizing');")
    print("PASS: customization immediately after address editing", flush=True)

    # Open, hold after leaving the toolbar, dismiss and reopen all primary panels.
    for selector, panel in [
        ("#PanelUI-menu-button", "appMenu-popup"),
        ("#unified-extensions-button", "unified-extensions-panel"),
        ("#ghost-pinned_example_invalid-BAP", "customizationui-widget-panel"),
        ("#bookmarks-menu-button", "BMB_bookmarksPopup"),
        ("#fxa-toolbar-menu-button", "customizationui-widget-panel"),
        ("#downloads-button", "downloadsPanel"),
    ]:
        for repeat in range(2):
            f.dismiss()
            f.reveal()
            f.click(selector)
            f.wait("return document.getElementById(arguments[0])?.state==='open';", [panel])
            f.move(400, 600)
            time.sleep(2.35 if repeat == 0 else .15)
            check(f.js("const e=document.getElementById(arguments[0]),s=getComputedStyle(e);"
                       "return e.state==='open'&&s.visibility==='visible'&&s.pointerEvents==='auto'"
                       "&&e.getBoundingClientRect().height>20;", [panel]), f"Panel collapsed: {panel}")
            if panel != "downloadsPanel":
                check(f.js("return getComputedStyle(document.getElementById('urlbar')).opacity==='1';"),
                      f"Toolbar hidden beneath open {panel}")
        print("PASS: hold / close / reopen", selector, flush=True)

    f.dismiss()
    f.reveal()
    f.click("#unified-extensions-button")
    f.wait("return document.getElementById('unified-extensions-panel').state==='open';")
    f.click(".unified-extensions-item-menu-button[data-extensionid='ghost-listed@example.invalid']")
    check(f.wait("return document.getElementById('unified-extensions-context-menu').state==='open';"),
          "Extension actions menu did not open")
    f.dismiss()
    f.reveal()
    f.click("#unified-extensions-button")
    f.wait("return document.getElementById('unified-extensions-panel').state==='open';")
    f.click("#ghost-listed_example_invalid-BAP")
    check(f.wait("return document.getElementById('customizationui-widget-panel')?.state==='open';"),
          "Opening an unpinned extension from the list failed")
    print("PASS: extension actions / list to extension popup", flush=True)

    # Switching views must not collapse the main menu or its anchor.
    for button in ["#appMenu-history-button", "#appMenu-bookmarks-button",
                   "#appMenu-more-button2", "#appMenu-help-button2"]:
        f.dismiss()
        f.reveal()
        f.click("#PanelUI-menu-button")
        f.wait("return document.getElementById('appMenu-popup').state==='open';")
        f.click(button)
        time.sleep(.4)
        check(f.js("return document.getElementById('appMenu-popup').state==='open';"),
              f"Menu subview disappeared: {button}")
    print("PASS: history / bookmarks / more tools / help subviews", flush=True)

    for button, uri in [("#appMenu-extensions-themes-button", "about:addons"),
                        ("#appMenu-settings-button", "about:preferences")]:
        for _ in range(2):
            f.dismiss()
            f.reveal()
            f.click("#PanelUI-menu-button")
            f.wait("return document.getElementById('appMenu-popup').state==='open';")
            f.click(button)
            f.wait("return gBrowser.currentURI.spec.startsWith(arguments[0]);", [uri])
            f.content_tab(uri)
            try:
                f.wait("return document.readyState==='complete'&&document.body?.innerText.length>100;")
                check(f.js("return document.documentElement.getBoundingClientRect().height>200;"),
                      f"Blank manager page: {uri}")
            finally:
                f.command("Marionette:SetContext", {"value": "chrome"})
    print("PASS: menu to extensions manager / settings, including repeated calls", flush=True)

    f.dismiss()
    # Exercise navigation and reload with local, deterministic documents.
    urls = ["data:text/html,<title>Ghost-A</title><h1>First</h1>",
            "data:text/html,<title>Ghost-B</title><h1>Second</h1>"]
    for url in urls:
        f.command("Marionette:SetContext", {"value": "content"})
        f.command("WebDriver:Navigate", {"url": url})
        f.command("Marionette:SetContext", {"value": "chrome"})
    f.reveal()
    f.click("#back-button")
    check(f.wait("return gBrowser.selectedBrowser.contentTitle==='Ghost-A';"), "Back failed")
    f.reveal()
    f.click("#forward-button")
    check(f.wait("return gBrowser.selectedBrowser.contentTitle==='Ghost-B';"), "Forward failed")
    f.reveal()
    f.click("#reload-button")
    check(f.wait("return !gBrowser.selectedBrowser.webProgress.isLoadingDocument;"), "Reload did not finish")
    check(f.js("return gBrowser.selectedBrowser.contentTitle==='Ghost-B';"), "Reload changed the page")
    print("PASS: back / forward / reload", flush=True)

    f.dismiss()
    f.reveal()
    f.click("#PanelUI-menu-button")
    f.wait("return document.getElementById('appMenu-popup').state==='open';")
    f.click("#appMenu-find-button2")
    check(f.wait("return gBrowser.selectedTab._findBar&&!gBrowser.selectedTab._findBar.hidden;"),
          "Find in page did not open")
    f.js("gBrowser.selectedTab._findBar.close();return true;")

    for width in [600, 900, 1440]:
        f.dismiss()
        f.command("WebDriver:SetWindowRect", {"width": width, "height": 1000})
        f.move(100, 2)
        time.sleep(.7)
        check(f.js("return document.getElementById('urlbar-container').getBoundingClientRect().width>=120;"),
              f"Address field too narrow at requested width {width}")
        f.click("#PanelUI-menu-button")
        f.wait("return document.getElementById('appMenu-popup').state==='open';")
        box = f.js("return document.getElementById('appMenu-popup').getBoundingClientRect().toJSON();")
        actual_width = f.js("return innerWidth;")
        check(box["x"] >= 0 and box["right"] <= actual_width + 1 and box["y"] >= 0,
              f"Menu outside window at {width}: {box}")
    print("PASS: find bar / smaller window menu", flush=True)

    f.dismiss()
    f.js("SidebarController.show('viewCustomizeSidebar');return true;")
    f.wait("return document.getElementById('sidebar-container').hasAttribute('sidebar-panel-open');")
    f.move(25, 400)
    time.sleep(.7)
    check(f.js("return document.getElementById('sidebar-container').getBoundingClientRect().width===50;"),
          "Expanded rail covers the side tool")
    check(f.js("const r=document.getElementById('sidebar-box').getBoundingClientRect();"
               "return !!document.elementFromPoint(r.x+24,r.y+100)?.closest('#sidebar-box');"),
          "Side tool controls are covered")
    f.js("SidebarController.hide();BrowserCommands.fullScreen();return true;")
    f.wait("return document.documentElement.hasAttribute('inFullscreen');")
    check(f.js("return getComputedStyle(document.getElementById('nav-bar')).display==='none'"
               "&&getComputedStyle(document.getElementById('sidebar-container')).display==='none';"),
          "Floating controls remain in native fullscreen")
    f.js("BrowserCommands.fullScreen();return true;")
    f.wait("return !document.documentElement.hasAttribute('inFullscreen');")
    print("PASS: side tool hit target / native fullscreen hide and restore", flush=True)

    f.dismiss()
    f.reveal()
    f.move(400, 600)
    time.sleep(.7)
    check(f.js("return getComputedStyle(document.getElementById('urlbar')).opacity==='1';"),
          "Hover close grace was lost")
    time.sleep(2)
    check(f.js("return getComputedStyle(document.getElementById('urlbar')).opacity==='0';"),
          "Toolbar did not hide after the grace period")
    check(f.js("return !document.elementFromPoint(150,30)?.closest('#nav-bar');"),
          "Hidden toolbar intercepts page clicks")
    print(f"PASS: {passed} toolbar checks", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--firefox", default="C:/Program Files/Mozilla Firefox/firefox.exe")
    parser.add_argument("--css", type=pathlib.Path, default=ROOT / "theme" / "userChrome.css")
    parser.add_argument('--job-self-test', action='store_true', help='Check hard driver death using dummy processes, without Firefox')
    parser.add_argument('--job-driver', type=pathlib.Path, help=argparse.SUPPRESS)
    options = parser.parse_args()
    if options.job_driver is not None:
        job_driver(options.job_driver)
        raise SystemExit(0)
    if options.job_self_test:
        check_job_lifetime()
        raise SystemExit(0)
    with tempfile.TemporaryDirectory(prefix="ghost-toolbar-test-") as directory:
        folder = pathlib.Path(directory)
        browser = Firefox(options.firefox, options.css, folder)
        try:
            run(browser, folder)
        finally:
            browser.close()
