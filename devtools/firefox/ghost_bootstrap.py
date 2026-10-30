"""Recoverable temporary preferences for an explicitly selected, closed profile.

No browser, network or profile operations occur on import. A pending transaction
must be recovered explicitly; an old backup JSON never authorizes resumption.
"""
import base64
import hashlib
import json
import os
import pathlib
import re
import stat
import tempfile


def require(condition, message='Verification failed'):
    if not condition:
        raise RuntimeError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_regular(path):
    info = path.stat(follow_symlinks=False)
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
            and not (getattr(info, 'st_file_attributes', 0) & 1024),
            'Expected a regular, single-link, non-redirected file')
    return path.read_bytes()


def check_profile_path(profile):
    path = pathlib.Path(profile).absolute()
    for component in (path, *path.parents):
        info = component.stat(follow_symlinks=False)
        require(not stat.S_ISLNK(info.st_mode)
                and not (getattr(info, 'st_file_attributes', 0) & 1024),
                'Redirected profile paths are not supported')
    return path.resolve(strict=True)


def atomic_write(path, data):
    fd, temporary = tempfile.mkstemp(prefix='.ghost-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def profile_closed(profile):
    """Fail closed on active/ambiguous Firefox lock files."""
    for name in ('parent.lock', '.parentlock', 'lock'):
        path = pathlib.Path(profile) / name
        if path.is_symlink():
            return False
        if not path.exists():
            continue
        if os.name != 'nt':
            return False
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        # Metadata-only (zero desired access) can succeed despite an existing
        # data handle. Require exclusive read/write access to detect a live lock.
        handle = kernel.CreateFileW(str(path), 0xC0000000, 0, None, 3, 0, None)
        if handle == wintypes.HANDLE(-1).value:
            return False
        kernel.CloseHandle(handle)
    return True


def pref_lines(data, keys):
    text = data.decode('utf-8')
    found = {}
    for line in text.splitlines(keepends=True):
        require(not line.lstrip().startswith(('/*', '*/', '*')),
                'Block comments in prefs.js require manual review')
        match = re.fullmatch(r'\s*user_pref\(("(?:[^"\\]|\\.)*"),\s*(.*?)\);\s*', line)
        if match:
            key = json.loads(match[1])
            if key not in keys:
                continue
            require(key not in found, 'Duplicate temporary preference; manual recovery required')
            found[key] = (json.loads(match[2]), line)
        elif line.lstrip().startswith('user_pref') and any(json.dumps(key) in line for key in keys):
            raise RuntimeError('Unsupported temporary preference syntax; manual review required')
    return found


class Bootstrap:
    def __init__(self, profile, state_dir):
        self.profile = check_profile_path(profile)
        self.directory = pathlib.Path(state_dir)
        self.path = self.directory / 'state.json'
        self.boot = self.profile / 'user.js'
        self.prefs = self.profile / 'prefs.js'
        self.state = None

    def save(self):
        atomic_write(self.path, json.dumps(self.state, indent=2).encode('utf-8'))

    def begin(self, settings):
        require(profile_closed(self.profile), 'Firefox profile is locked; close Firefox first')
        require(not self.directory.exists(),
                'Transaction record already exists; use --recover and inspect it before a new operation')
        require(not self.boot.exists() and not self.boot.is_symlink(),
                'Existing user.js preserved; remove it from this workflow explicitly before continuing')
        original = read_regular(self.prefs)
        pref_lines(original, settings)  # Validate before changing anything.
        bootstrap = ''.join('user_pref(' + json.dumps(k) + ', ' + json.dumps(v) + ');\n'
                            for k, v in settings.items()).encode('utf-8')
        self.directory.mkdir(parents=True)
        self.state = {'version': 1, 'profile': str(self.profile), 'status': 'pending',
                      'settings': settings, 'prefs': base64.b64encode(original).decode('ascii'),
                      'prefs_sha256': digest(original),
                      'bootstrap': base64.b64encode(bootstrap).decode('ascii'),
                      'bootstrap_identity': None}
        self.save()  # Durable recovery information precedes temporary mutation.
        # Exclusive creation preserves user.js that appeared since the preflight.
        with self.boot.open('xb') as output:
            identity = os.fstat(output.fileno())
            self.state['bootstrap_identity'] = [identity.st_dev, identity.st_ino]
            self.save()
            output.write(bootstrap)
            output.flush()
            os.fsync(output.fileno())

    def keep_preferences(self, keys):
        require(set(keys) <= {'sidebar.visibility', 'toolkit.legacyUserProfileCustomizations.stylesheets'},
                'Only installed presentation preferences may be retained')
        self.state['keep'] = list(keys)
        self.save()

    def load(self):
        state = json.loads(read_regular(self.path))
        require(isinstance(state, dict) and all(key in state for key in (
            'version', 'profile', 'status', 'settings', 'prefs', 'prefs_sha256', 'bootstrap', 'bootstrap_identity')),
            'Incomplete recovery record; manual review required')
        require(state.get('version') == 1 and state.get('profile') == str(self.profile),
                'Recovery record belongs to another profile or version')
        require(state.get('status') in ('pending', 'complete'), 'Incomplete recovery record')
        require(isinstance(state.get('settings'), dict) and bool(state['settings']),
                'Incomplete settings record')
        require(set(state.get('keep', [])) <= {
            'sidebar.visibility', 'toolkit.legacyUserProfileCustomizations.stylesheets'},
            'Invalid retained preferences')
        original = base64.b64decode(state['prefs'], validate=True)
        require(digest(original) == state['prefs_sha256'], 'Corrupt preferences backup')
        expected = ''.join('user_pref(' + json.dumps(k) + ', ' + json.dumps(v) + ');\n'
                           for k, v in state['settings'].items()).encode('utf-8')
        require(base64.b64decode(state['bootstrap'], validate=True) == expected,
                'Corrupt bootstrap record')
        pref_lines(original, state['settings'])
        self.state = state

    def recover(self, browser_stopped):
        self.load()  # Never infer ownership from just the presence of a backup.
        if self.state['status'] == 'complete':
            return 'Temporary preferences already restored'
        expected = base64.b64decode(self.state['bootstrap'], validate=True)
        conflict = False
        if self.boot.exists() or self.boot.is_symlink():
            identity = self.boot.stat(follow_symlinks=False)
            if ([identity.st_dev, identity.st_ino] == self.state['bootstrap_identity']
                    and read_regular(self.boot) == expected):
                self.boot.unlink()
            else:
                conflict = True  # Preserve foreign/edited user.js, but still restore prefs.
        require(browser_stopped and profile_closed(self.profile),
                'Bootstrap removed if owned; Firefox still running: close it and use --recover')
        original = base64.b64decode(self.state['prefs'], validate=True)
        current = read_regular(self.prefs)
        keys = {key: value for key, value in self.state['settings'].items()
                if key not in self.state.get('keep', [])}
        before = pref_lines(original, keys)
        now = pref_lines(current, keys)
        require(set(before) <= set(now),
                'An original preference disappeared; preserve current prefs and review manually')
        for key, (value, _) in now.items():
            matches_temporary = type(value) is type(keys[key]) and value == keys[key]
            matches_original = (key in before and type(value) is type(before[key][0])
                                and value == before[key][0])
            require(matches_temporary or matches_original,
                    'Temporary preference changed externally; manual recovery required')
        # Preserve unrelated current preferences instead of rolling the entire file back.
        selected_lines = {line for _, line in now.values()}
        text = ''.join(line for line in current.decode('utf-8').splitlines(keepends=True)
                       if line not in selected_lines)
        if text and not text.endswith('\n'):
            text += '\n'
        text += ''.join(item[1] if item[1].endswith('\n') else item[1] + '\n'
                        for item in before.values())
        restored = text.encode('utf-8')
        # If nothing else changed, restore the original byte-for-byte snapshot.
        if current == original:
            restored = current
        require(read_regular(self.prefs) == current, 'prefs.js changed during recovery; retry with Firefox closed')
        if restored != current:
            atomic_write(self.prefs, restored)
        require(not conflict, 'Preferences restored; changed user.js preserved: manual review required')
        self.state['status'] = 'complete'
        self.save()
        return 'Temporary preferences restored; original backup retained'


def finish(bootstrap, process, connection, quit_browser, report, report_path):
    """Cleanup is attempted before report writing, even after failed startup."""
    errors = []
    if bootstrap.state is None:
        return  # Preflight failed: do not overwrite an older transaction/report.
    if connection is not None:
        try:
            quit_browser()
        except Exception:
            errors.append('Normal Firefox quit failed')
        finally:
            try:
                connection.close()
            except Exception:
                errors.append('Connection could not be closed')
    stopped = process is None
    if process is not None:
        try:
            process.wait(timeout=20)
            stopped = True
        except Exception:
            errors.append('Firefox has not exited; close it normally before --recover')
    if bootstrap.state is not None:
        try:
            message = bootstrap.recover(stopped)
            print(message, flush=True)
        except Exception as error:
            errors.append(str(error))
    report['cleanup'] = 'complete' if not errors else errors
    try:
        atomic_write(pathlib.Path(report_path), json.dumps(report, ensure_ascii=False, indent=2).encode('utf-8'))
    except Exception:
        errors.append('Report could not be written; recovery was attempted independently')
    if errors:
        raise RuntimeError('; '.join(errors))
