"""Run one server and its client with an ephemeral credential in their environment.

Usage: python run-ghost-probe.py probe|screenshot client.py [client arguments]
Only use disposable browser profiles. No token files or token output are produced.
"""
import os
import pathlib
import queue
import secrets
import subprocess
import sys
import threading


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ('probe', 'screenshot'):
        raise SystemExit('Usage: run-ghost-probe.py probe|screenshot client.py [args]')
    root = pathlib.Path(__file__).resolve().parent
    env = dict(os.environ, GHOST_PROBE_TOKEN=secrets.token_urlsafe(32))
    server = subprocess.Popen(
        [sys.executable, '-u', str(root / ('firefox-' + sys.argv[1] + '-server.py'))],
        env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    ready = queue.Queue()

    def read_output():
        for line in server.stdout:
            if line.startswith('READY:'):
                ready.put(True)
        ready.put(False)

    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    started = False
    try:
        try:
            started = ready.get(timeout=45)
        except queue.Empty:
            started = False
        if not started or server.poll() is not None:
            raise RuntimeError('Bridge did not start; client was not launched')
        return subprocess.call([sys.executable, *sys.argv[2:]], env=env)
    finally:
        # EOF is a private parent/child shutdown signal, independent of READY,
        # the HTTP listener and the session token. Never contact an old listener.
        server.stdin.close()
        if server.poll() is None:
            try:
                server.wait(timeout=20)
            except subprocess.TimeoutExpired:
                server.terminate()
                server.wait(timeout=5)
        reader.join(timeout=2)
        server.stdout.close()


if __name__ == '__main__':
    raise SystemExit(main())
