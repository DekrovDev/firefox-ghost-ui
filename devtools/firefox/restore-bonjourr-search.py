import argparse
import json,pathlib,re,shutil,socket,subprocess,time,base64
from ghost_bootstrap import Bootstrap, finish, require, profile_closed, check_profile_path
ROOT=pathlib.Path(__file__).resolve().parent
connection=None
conn=None
sequence=0
seq=0

def receive():
    prefix=b''
    while not prefix.endswith(b':'):
        char=conn.recv(1)
        if not char:raise RuntimeError('Firefox disconnected')
        prefix+=char
    count=int(prefix[:-1]);data=b''
    while len(data)<count:
        char=conn.recv(count-len(data))
        if not char:raise RuntimeError('Firefox disconnected')
        data+=char
    return json.loads(data)
def cmd(name,args=None):
    global seq
    seq+=1;data=json.dumps([0,seq,name,args or {}]).encode()
    conn.sendall(str(len(data)).encode()+b':'+data)
    result=receive()
    if result[2]:raise RuntimeError(json.dumps(result[2]))
    v=result[3];return v.get('value',v) if isinstance(v,dict) else v
def js(code,args=None,async_=False):
    return cmd('WebDriver:ExecuteAsyncScript' if async_ else 'WebDriver:ExecuteScript',{'script':code,'args':args or [],'newSandbox':True,'sandbox':'system','scriptTimeout':30000,'filename':'restore-bonjourr-search','line':1})
def context(name):cmd('Marionette:SetContext',{'value':name})
TABS="return [...gBrowser.tabs].map(t=>({url:t.linkedBrowser.currentURI.spec,pinned:t.pinned,selected:t.selected}));"
def main():
    global PROFILE, EXE, BACKUP, PREFS, BOOT, REPORT, connection, conn, sequence, seq
    connection=None;conn=None;sequence=0;seq=0
    parser=argparse.ArgumentParser(description='Legacy Firefox helper: explicit closed profile required')
    parser.add_argument('--profile', type=pathlib.Path, required=True)
    parser.add_argument('--firefox', default=r'C:\Program Files\Mozilla Firefox\firefox.exe')
    parser.add_argument('--state', type=pathlib.Path)
    parser.add_argument('--recover', action='store_true')
    args=parser.parse_args()
    PROFILE=check_profile_path(args.profile)
    EXE=args.firefox
    BACKUP=ROOT/'.local'/'backup'
    PREFS=PROFILE/'prefs.js'
    BOOT=PROFILE/'user.js'
    transaction=Bootstrap(PROFILE, args.state or BACKUP/'search-bootstrap-v2')
    REPORT=transaction.directory/'search-restored.json'
    if args.recover:
        print(transaction.recover(profile_closed(PROFILE)), flush=True)
        return
    require(profile_closed(PROFILE), 'Close Firefox normally before running this helper')
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1',0))
        port=reservation.getsockname()[1]
    process=None
    report={}
    try:
        transaction.begin({'marionette.port':port,'marionette.enabled':True,'browser.startup.page':3})
        process=subprocess.Popen([EXE,'-no-remote','-profile',str(PROFILE),'-marionette','--remote-allow-system-access'])
        for _ in range(150):
            try:
                conn=socket.create_connection(('127.0.0.1',port),timeout=1);conn.settimeout(40);break
            except OSError:time.sleep(.2)
        if not conn:raise RuntimeError('Firefox connection did not start')
        receive();cmd('WebDriver:NewSession',{'capabilities':{'alwaysMatch':{}}});context('chrome');time.sleep(2)
        before=js(TABS)
        context('content');handles=cmd('WebDriver:GetWindowHandles');original=handles[next((i for i,t in enumerate(before) if t['selected']),0)]
        cmd('WebDriver:SwitchToWindow',{'handle':original});context('chrome')
        url=js("return WebExtensionPolicy.getByID('{4f391a9e-8717-4ba6-a5b1-488a34931fcb}').getURL('index.html');")
        js("gBrowser.selectedTab=gBrowser.addTab(arguments[0],{triggeringPrincipal:Services.scriptSecurityManager.getSystemPrincipal()});return true;",[url])
        context('content');temp=next(h for h in cmd('WebDriver:GetWindowHandles') if h not in handles);cmd('WebDriver:SwitchToWindow',{'handle':temp});time.sleep(2)
        current=js("const done=arguments[arguments.length-1];window.wrappedJSObject.chrome.storage.sync.get().then(done).catch(e=>done({error:String(e)}));",async_=True)
        require(isinstance(current.get('searchbar'),dict), "Operation verification failed; original backups retained")
        previous={'css':current.get('css',''),'searchbar':current['searchbar']}
        (transaction.directory/'bonjourr-before-search-restore.json').write_text(json.dumps(previous,ensure_ascii=False,indent=2),encoding='utf-8')
        css=re.sub(r'/\* The Firefox address field provides search; avoid a second empty search box\. \*/\s*','',previous['css'])
        css=re.sub(r'#sb_container\s*\{\s*display:\s*none\s*!important;?\s*\}\s*','',css)
        patch={'css':css,'searchbar':{**previous['searchbar'],'on':True}}
        require(patch['searchbar']=={**current['searchbar'],'on':True}, "Operation verification failed; original backups retained")
        result=js("const done=arguments[arguments.length-1],w=window.wrappedJSObject,data=w.JSON.parse(arguments[0]);w.chrome.storage.sync.set(data).then(()=>w.chrome.storage.sync.get()).then(done).catch(e=>done({error:String(e)}));",[json.dumps(patch)],True)
        require(result.get('searchbar',{}).get('on') is True, "Operation verification failed; original backups retained")
        require(all(result.get(k)==v for k,v in current.items() if k not in ['css','searchbar']), "Operation verification failed; original backups retained")
        cmd('WebDriver:Refresh');time.sleep(2)
        report['search']=js("const box=document.querySelector('#sb_container'),input=box?.querySelector('input'),r=box?.getBoundingClientRect();return {display:box?getComputedStyle(box).display:null,width:r?.width,height:r?.height,input:!!input,disabled:input?.disabled,background:document.querySelector('#background-media')?.innerHTML.slice(0,100)};")
        require(report['search']['display']!='none' and report['search']['width']>100 and report['search']['height']>20 and report['search']['input'] and not report['search']['disabled'], "Operation verification failed; original backups retained")
        report['otherSettingsPreserved']=True
        # Test actual input without submitting a query or changing the user's tabs.
        e=cmd('WebDriver:FindElement',{'using':'css selector','value':'#sb_container input'});eid=next(iter(e.values()))
        cmd('WebDriver:ElementClick',{'id':eid});cmd('WebDriver:ElementSendKeys',{'id':eid,'text':'проверка'})
        report['inputAcceptsText']=js("return document.querySelector('#sb_container input').value;")=='проверка'
        require(report['inputAcceptsText'], "Operation verification failed; original backups retained")
        cmd('WebDriver:ElementClear',{'id':eid});context('chrome')
        js("gURLBar.view.close();gBrowser.selectedBrowser.focus();return true;");time.sleep(1.5)
        data=cmd('WebDriver:TakeScreenshot',{'id':None,'full':True,'scroll':False})
        (transaction.directory/'search-restored.png').write_bytes(base64.b64decode(data))
        context('content');cmd('WebDriver:CloseWindow');cmd('WebDriver:SwitchToWindow',{'handle':original});context('chrome')
        after=js(TABS)
        require(before==after, "Operation verification failed; original backups retained")
        report['tabsPreserved']=len(before);report['hoverDelayMs']=725
        require('--ghost-open-wait: 725ms;' in (PROFILE/'chrome'/'userChrome.css').read_text(encoding='utf-8'), "Operation verification failed; original backups retained")
        report['status']='search-restored-and-verified'
    finally:
        finish(transaction, process, conn, lambda: cmd('Marionette:Quit',{'flags':['eAttemptQuit']}), report, REPORT)
    print('PASS: search checks and temporary preference cleanup completed',flush=True)


if __name__ == '__main__':
    main()
