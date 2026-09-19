"""The server must not outlive the editor that started it.

A stranded server keeps its binary and log file open, which on Windows blocks
the next build and stops the replacement server from starting. LSP hands the
server the editor's process id at initialize precisely so this can be avoided.

    python tests/watchdog_test.py <sample-project-dir> dist/cslite.exe
"""
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
SERVER = sys.argv[2]

failures = []


def check(label, ok, detail=""):
    print(("  PASS  " if ok else "  FAIL  ") + label + (f"   {detail}" if detail else ""))
    if not ok:
        failures.append(label)


def start_server():
    proc = subprocess.Popen([SERVER, "--verbose"], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    threading.Thread(target=lambda: [None for _ in proc.stderr], daemon=True).start()
    return proc


def send(proc, ids, method, params, request=True):
    msg = {"jsonrpc": "2.0", "method": method, "params": params}
    if request:
        ids[0] += 1
        msg["id"] = ids[0]
    body = json.dumps(msg).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
    proc.stdin.flush()
    return msg.get("id")


def read(proc):
    headers = {}
    while True:
        line = proc.stdout.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            break
        k, _, v = line.decode("ascii").partition(":")
        headers[k.strip().lower()] = v.strip()
    return json.loads(proc.stdout.read(int(headers["content-length"])))


def wait(proc, rid):
    while True:
        m = read(proc)
        if m is None:
            raise SystemExit("server closed the connection")
        if m.get("id") == rid and ("result" in m or "error" in m):
            return m


uri = lambda p: p.resolve().as_uri()

# A process for the server to watch, standing in for the editor. It waits on
# stdin, so it stays alive until we kill it.
stand_in = subprocess.Popen([sys.executable, "-c", "import sys; sys.stdin.read()"],
                            stdin=subprocess.PIPE)

print("== the editor going away ==")
server = start_server()
ids = [0]
rid = send(server, ids, "initialize", {
    "processId": stand_in.pid,
    "rootUri": uri(ROOT),
    "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
    "capabilities": {}})
wait(server, rid)
send(server, ids, "initialized", {}, request=False)
time.sleep(1)
check("server is up", server.poll() is None)

stand_in.kill()
stand_in.wait()
print("  editor killed; the watchdog polls every 10s ...")

deadline = time.time() + 40
while time.time() < deadline and server.poll() is None:
    time.sleep(0.5)

check("server noticed and exited", server.poll() is not None,
      "still running after 40s")
check("it exited cleanly", server.poll() == 0, f"exit code {server.poll()}")
if server.poll() is None:
    server.kill()

print("\n== a live editor is left alone ==")
alive = subprocess.Popen([sys.executable, "-c", "import sys; sys.stdin.read()"],
                         stdin=subprocess.PIPE)
server = start_server()
ids = [0]
rid = send(server, ids, "initialize", {
    "processId": alive.pid,
    "rootUri": uri(ROOT),
    "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
    "capabilities": {}})
wait(server, rid)
send(server, ids, "initialized", {}, request=False)

time.sleep(25)          # two watchdog ticks
check("server still running while the editor lives", server.poll() is None,
      f"exited with {server.poll()}")

rid = send(server, ids, "shutdown", None)
check("and still answering requests", "error" not in wait(server, rid))
send(server, ids, "exit", None, request=False)
try:
    server.wait(timeout=15)
except subprocess.TimeoutExpired:
    server.kill()
alive.kill()

print("\n== no processId given ==")
server = start_server()
ids = [0]
rid = send(server, ids, "initialize", {
    "processId": None,
    "rootUri": uri(ROOT),
    "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
    "capabilities": {}})
wait(server, rid)
send(server, ids, "initialized", {}, request=False)
time.sleep(12)
check("server runs normally without one", server.poll() is None,
      f"exited with {server.poll()}")
rid = send(server, ids, "shutdown", None)
wait(server, rid)
send(server, ids, "exit", None, request=False)
try:
    server.wait(timeout=15)
except subprocess.TimeoutExpired:
    server.kill()

print()
if failures:
    print(f"FAILED {len(failures)}: " + "; ".join(failures))
    sys.exit(1)
print("all checks passed")
