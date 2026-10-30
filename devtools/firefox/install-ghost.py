import argparse
import json,pathlib,re,shutil,socket,subprocess,time,base64
from ghost_bootstrap import Bootstrap, finish, require, profile_closed, check_profile_path, read_regular
ROOT=pathlib.Path(__file__).resolve().parent
connection=None
conn=None
sequence=0
seq=0

def receive():
    prefix=b''
    while not prefix.endswith(b':'):
        c=connection.recv(1)
        if not c:raise RuntimeError('Firefox disconnected')
        prefix+=c
    length=int(prefix[:-1]);data=b''
    while len(data)<length:
        c=connection.recv(length-len(data))
        if not c:raise RuntimeError('Firefox disconnected')
        data+=c
    return json.loads(data)
def cmd(name,parameters=None):
    global sequence
    sequence+=1;payload=json.dumps([0,sequence,name,parameters or {}]).encode()
    connection.sendall(str(len(payload)).encode()+b':'+payload)
    r=receive()
    if r[2]:raise RuntimeError(json.dumps(r[2]))
    v=r[3];return v.get('value',v) if isinstance(v,dict) else v
def js(script,args=None,async_=False):
    return cmd('WebDriver:ExecuteAsyncScript' if async_ else 'WebDriver:ExecuteScript',{'script':script,'args':args or [],'newSandbox':True,'sandbox':'system','scriptTimeout':30000,'filename':'ghost-install','line':1})
def context(value):cmd('Marionette:SetContext',{'value':value})
def pointer(x,y):cmd('WebDriver:PerformActions',{'actions':[{'type':'pointer','id':'ghost-live','parameters':{'pointerType':'mouse'},'actions':[{'type':'pointerMove','origin':'viewport','x':int(x),'y':int(y),'duration':0}]}]})
def merge_bonjourr_css(previous, added):
    require(isinstance(previous,str) and isinstance(added,str) and bool(added.strip()),
            'Bonjourr stylesheet must be nonempty text')
    start=previous.find('/* Ghost UI for Bonjourr')
    if start>=0:
        require(previous.count(added)==1 and previous.find(added)==start,
                'Existing Ghost CSS was changed; preserve it and reconcile manually')
        previous=previous[:start]+previous[start+len(added):]
    return previous+'\n\n'+added

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
    transaction=Bootstrap(PROFILE, args.state or BACKUP/'install-bootstrap-v2')
    REPORT=transaction.directory/'installation-check.json'
    if args.recover:
        print(transaction.recover(profile_closed(PROFILE)), flush=True)
        return
    require(profile_closed(PROFILE), 'Close Firefox normally before running this helper')
    require(not (ROOT/'GhostUI'/'backup'/'preferences-before.json').exists(), 'Legacy backup found: reconcile it manually; automatic resume is disabled')
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1',0))
        port=reservation.getsockname()[1]
    process=None
    report={}
    try:
        transaction.begin({'marionette.port':port,'marionette.enabled':True,'browser.startup.page':3,'sidebar.visibility':'expand-on-hover','toolkit.legacyUserProfileCustomizations.stylesheets':True})
        css=PROFILE/'chrome'/'userChrome.css'
        check_profile_path(css.parent)
        read_regular(css)
        shutil.copy2(css,transaction.directory/'userChrome-before.css')
        theme=ROOT/'GhostUI'
        if not (theme/'userChrome.css').is_file():
            theme=ROOT.parents[1]/'theme'
        shutil.copy2(theme/'userChrome.css',css)
        process=subprocess.Popen([EXE,'-no-remote','-profile',str(PROFILE),'-marionette','--remote-allow-system-access'])
        for _ in range(150):
            try:
                connection=socket.create_connection(('127.0.0.1',port),timeout=1);connection.settimeout(40);break
            except OSError:time.sleep(.2)
        if not connection:raise RuntimeError('Firefox automation did not start')
        receive();cmd('WebDriver:NewSession',{'capabilities':{'alwaysMatch':{}}});context('chrome');time.sleep(3)
        report['tabsBefore']=js("return [...gBrowser.tabs].map(t=>({url:t.linkedBrowser.currentURI.spec,pinned:t.pinned,selected:t.selected}));")
        (transaction.directory/'session-before.json').write_text(json.dumps(report['tabsBefore'],ensure_ascii=False,indent=2),encoding='utf-8')
        context('content');handles=cmd('WebDriver:GetWindowHandles')
        selectedIndex=next((i for i,t in enumerate(report['tabsBefore']) if t['selected']),0)
        originalHandle=handles[selectedIndex]
        cmd('WebDriver:SwitchToWindow',{'handle':originalHandle});context('chrome')
        url=js("return WebExtensionPolicy.getByID('{4f391a9e-8717-4ba6-a5b1-488a34931fcb}').getURL('index.html');")
        js("gBrowser.selectedTab=gBrowser.addTab(arguments[0],{triggeringPrincipal:Services.scriptSecurityManager.getSystemPrincipal()});return true;",[url])
        context('content');newHandle=next(h for h in cmd('WebDriver:GetWindowHandles') if h not in handles)
        cmd('WebDriver:SwitchToWindow',{'handle':newHandle});time.sleep(2)
        storage=js("const done=arguments[arguments.length-1];const w=window.wrappedJSObject;Promise.all([w.chrome.storage.sync.get(),w.chrome.storage.local.get()]).then(([sync,local])=>done({sync,local,web: w.localStorage.getItem('bonjourr')})).catch(e=>done({error:String(e)}));",async_=True)
        if 'error' in storage:raise RuntimeError(storage['error'])
        if not (transaction.directory/'bonjourr-before.json').exists():
            (transaction.directory/'bonjourr-before.json').write_text(json.dumps(storage,ensure_ascii=False,indent=2),encoding='utf-8')
        # Merge only presentation options into the active storage backend.
        backend='sync';data=storage['sync']
        if isinstance(storage['local'].get('syncStorage'),dict):backend='local';data=storage['local']['syncStorage']
        require(isinstance(data,dict) and data.get('backgrounds'), "Operation verification failed; original backups retained")
        bonjourr=theme/'bonjourr-ghost.css'
        if not bonjourr.is_file():
            bonjourr=theme/'bonjourr.css'
        added=bonjourr.read_text(encoding='utf-8')
        updated={**data,'css':merge_bonjourr_css(data.get('css',''),added),'searchbar':{**data.get('searchbar',{}),'on':False},'hide':{**data.get('hide',{}),'settingsicon':False}}
        payload={'syncStorage':updated} if backend=='local' else {k:updated[k] for k in ['css','searchbar','hide']}
        result=js("const done=arguments[arguments.length-1],w=window.wrappedJSObject,data=w.JSON.parse(arguments[0]);const task=arguments[1]==='local'?w.chrome.storage.local.set(data):w.chrome.storage.sync.set(data);task.then(()=>done(true)).catch(e=>done({error:String(e)}));",[json.dumps(payload),backend],True)
        require(result is True, "Operation verification failed; original backups retained")
        cmd('WebDriver:Refresh');time.sleep(3)
        report['bonjourr']=js("return {url:location.href,searchDisplay:getComputedStyle(document.querySelector('#sb_container')).display,animation:getComputedStyle(document.querySelector('#greetings')).animationName,settingsDisplay:getComputedStyle(document.querySelector('#show-settings')).display,clock:document.querySelector('#time').textContent};")
        require(report['bonjourr']['searchDisplay']=='none' and report['bonjourr']['animation']=='ghost-newtab-in', "Operation verification failed; original backups retained")
        context('chrome')
        report['extensions']=js("const ids=['{60493d8c-aec8-448e-a247-5d2cfa047d69}','ATBC@EasonWong','{4f391a9e-8717-4ba6-a5b1-488a34931fcb}'];return ids.map(id=>({id,active:!!WebExtensionPolicy.getByID(id)?.active}));")
        require(all(a['active'] for a in report['extensions']), "Operation verification failed; original backups retained")
        js("gURLBar.view.close();gBrowser.selectedBrowser.focus();return true;")
        dims=js('return {w:innerWidth,h:innerHeight};');pointer(dims['w']//2,dims['h']//2);time.sleep(1.7)
        measure="const n=document.querySelector('#nav-bar'),s=getComputedStyle(n,'::before'),e=document.querySelector('#urlbar'),u=e.getBoundingClientRect(),r=n.getBoundingClientRect();return {shell:{w:parseFloat(s.width),h:parseFloat(s.height),opacity:parseFloat(s.opacity)},bar:{x:r.x,right:r.right},url:{x:u.x,right:u.right,opacity:parseFloat(getComputedStyle(e).opacity),pointer:getComputedStyle(e).pointerEvents,delay:getComputedStyle(e).transitionDelay},icons:[...document.querySelector('#nav-bar-customization-target').children].filter(e=>getComputedStyle(e).display!=='none').map(e=>({id:e.id,opacity:getComputedStyle(e).opacity,visible:getComputedStyle(e).visibility,delay:getComputedStyle(e).transitionDelay})),content:{x:document.querySelector('#tabbrowser-tabbox').getBoundingClientRect().x,y:document.querySelector('#tabbrowser-tabbox').getBoundingClientRect().y},buttons:[...document.querySelectorAll('#toolbar-menubar .titlebar-button')].filter(e=>getComputedStyle(e).display!=='none').map(e=>{const r=e.getBoundingClientRect(),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return {id:e.className,hit:e===hit||e.contains(hit),w:r.width}})};"
        report['rest']=js(measure)
        require(report['rest']['shell']['opacity']<.02, "Operation verification failed; original backups retained")
        pointer(dims['w']//2,2);time.sleep(6);report['open']=js(measure)
        (transaction.directory/'live-open-debug.json').write_text(json.dumps(report['open'],indent=2),encoding='utf-8')
        require(report['open']['shell']['w']>500 and report['open']['shell']['opacity']==1, "Operation verification failed; original backups retained")
        require(report['open']['url']['x']>=report['open']['bar']['x']-1 and report['open']['url']['right']<=report['open']['bar']['right']+1, "Operation verification failed; original backups retained")
        require(all(b['hit'] and b['w']>0 for b in report['open']['buttons']), "Operation verification failed; original backups retained")
        require(report['open']['url']['opacity']>.95 and report['open']['url']['pointer']=='auto', "Operation verification failed; original backups retained")
        require(all(float(i['opacity'])>.95 and i['visible']=='visible' for i in report['open']['icons']), "Operation verification failed; original backups retained")
        require(report['rest']['content']==report['open']['content'], "Operation verification failed; original backups retained")
        shot=cmd('WebDriver:TakeScreenshot',{'id':None,'full':True,'scroll':False})
        (transaction.directory/'installed-preview.png').write_bytes(base64.b64decode(shot))
        pointer(dims['w']//2,dims['h']//2)
        context('content');cmd('WebDriver:CloseWindow');cmd('WebDriver:SwitchToWindow',{'handle':originalHandle});context('chrome')
        report['tabsAfter']=js("return [...gBrowser.tabs].map(t=>({url:t.linkedBrowser.currentURI.spec,pinned:t.pinned,selected:t.selected}));")
        require([t['url'] for t in report['tabsBefore']]==[t['url'] for t in report['tabsAfter']], "Operation verification failed; original backups retained")
        transaction.keep_preferences(['sidebar.visibility','toolkit.legacyUserProfileCustomizations.stylesheets'])
        report['status']='verified-installed'
    finally:
        finish(transaction, process, connection, lambda: cmd('Marionette:Quit',{'flags':['eAttemptQuit']}), report, REPORT)
    print('PASS: installation checks and temporary preference cleanup completed',flush=True)


if __name__ == '__main__':
    main()
