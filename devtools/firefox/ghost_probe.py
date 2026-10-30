import json
import os
import re
import pathlib
import urllib.request

ROOT=pathlib.Path(__file__).resolve().parent
BRIDGE_URL='http://127.0.0.1:2832'

class NoBridgeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError('Bridge redirects are not allowed')

def request(data):
    token=os.environ.get('GHOST_PROBE_TOKEN','')
    if not re.fullmatch(r'[A-Za-z0-9_-]{43}',token):
        raise RuntimeError('Run clients through run-ghost-probe.py')
    payload=json.dumps(data).encode()
    with urllib.request.build_opener(urllib.request.ProxyHandler({}),NoBridgeRedirect()).open(urllib.request.Request(BRIDGE_URL, data=payload, headers={'Content-Type':'application/json','Authorization':'Bearer '+token}),timeout=50) as response:
        result=json.load(response)
    if not result['ok']:
        raise RuntimeError(result['error'])
    value=result['result']
    return value.get('value',value) if isinstance(value,dict) else value

def js(script,args=None,asynchronous=False):
    return request({'script':script,'args':args or [],'async':asynchronous})

def cmd(command,parameters=None):
    return request({'command':command,'parameters':parameters or {}})

def pointer(x,y):
    return cmd('WebDriver:PerformActions',{'actions':[{'type':'pointer','id':'ghost-mouse','parameters':{'pointerType':'mouse'},'actions':[{'type':'pointerMove','origin':'viewport','x':int(x),'y':int(y),'duration':0}]}]})

def apply_css(path):
    return js("document.getElementById('ghost-test-style')?.remove(); if(window.__ghostTestUserSheetURI)window.windowUtils.removeSheetUsingURIString(window.__ghostTestUserSheetURI,window.windowUtils.USER_SHEET); window.__ghostTestUserSheetURI='data:text/css;charset=utf-8,'+encodeURIComponent(arguments[0]); window.windowUtils.loadSheetUsingURIString(window.__ghostTestUserSheetURI,window.windowUtils.USER_SHEET); return true;",[pathlib.Path(path).read_text(encoding='utf-8')])

def screenshot(path):
    import base64
    data=cmd('WebDriver:TakeScreenshot',{'id':None,'full':True,'scroll':False})
    pathlib.Path(path).write_bytes(base64.b64decode(data))
