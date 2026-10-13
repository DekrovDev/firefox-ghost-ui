"""Native Firefox toolbar regression checks in a disposable, headless profile.

Run: python tests/Toolbar.Tests.py --firefox "C:/Program Files/Mozilla Firefox/firefox.exe"
No personal profile, extension settings, accounts or browsing data are used.
"""
import argparse
import json
import pathlib
import shutil
import socket
import subprocess
import tempfile
import time
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


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
        self.process = subprocess.Popen(
            [executable, "-headless", "-no-remote", "-profile", str(profile),
             "-marionette", "--remote-allow-system-access", "about:blank"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
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
        if self.connection:
            try:
                self.command("Marionette:Quit", {"flags": ["eForceQuit"]})
            except (OSError, RuntimeError):
                pass
            self.connection.close()
        try:
            self.process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=8)


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

    for width in [900, 1440]:
        f.dismiss()
        f.command("WebDriver:SetWindowRect", {"width": width, "height": 1000})
        f.reveal()
        f.click("#PanelUI-menu-button")
        f.wait("return document.getElementById('appMenu-popup').state==='open';")
        box = f.js("return document.getElementById('appMenu-popup').getBoundingClientRect().toJSON();")
        check(box["x"] >= 0 and box["right"] <= width + 1 and box["y"] >= 0,
              f"Menu outside window at {width}: {box}")
    print("PASS: find bar / smaller window menu", flush=True)

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
    options = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="ghost-toolbar-test-") as directory:
        folder = pathlib.Path(directory)
        browser = Firefox(options.firefox, options.css, folder)
        try:
            run(browser, folder)
        finally:
            browser.close()
