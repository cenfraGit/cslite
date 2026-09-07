"""Does cslite lock a source generator DLL while it has the workspace open?

This is the failure the user hits with other C# servers: the server loads the
generator assembly to run it, Windows locks the file, and the next build of the
generator project fails with MSB3027 / "being used by another process".
"""
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
SERVER = sys.argv[2]

proc = subprocess.Popen([SERVER, "--verbose"],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

log = []
threading.Thread(
    target=lambda: [log.append(l.decode("utf-8", "replace").rstrip()) for l in proc.stderr],
    daemon=True).start()

_id = [0]
failures = []


def check(label, ok, detail=""):
    print(("  PASS  " if ok else "  FAIL  ") + label + (f"   {detail}" if detail else ""))
    if not ok:
        failures.append(label)


def send(method, params, request=True):
    msg = {"jsonrpc": "2.0", "method": method, "params": params}
    if request:
        _id[0] += 1
        msg["id"] = _id[0]
    body = json.dumps(msg).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
    proc.stdin.flush()
    return msg.get("id")


def read():
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


def await_response(rid, timeout=120):
    end = time.time() + timeout
    notes = []
    while time.time() < end:
        m = read()
        if m is None:
            raise SystemExit("server closed the connection")
        if m.get("id") == rid and ("result" in m or "error" in m):
            return m, notes
        if "method" in m:
            notes.append(m)
    raise SystemExit("timed out")


uri = lambda p: p.resolve().as_uri()

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
await_response(rid)
send("initialized", {}, request=False)

program = ROOT / "Consumer" / "Program.cs"
send("textDocument/didOpen", {"textDocument": {
    "uri": uri(program), "languageId": "csharp", "version": 1,
    "text": program.read_text()}}, request=False)

rid = send("textDocument/hover", {"textDocument": {"uri": uri(program)},
                                  "position": {"line": 2, "character": 20}})
response, notes = await_response(rid)

print("== does the server see generator output it never executed? ==")
diags = [n for n in notes if n["method"] == "textDocument/publishDiagnostics"]
reported = diags[-1]["params"]["diagnostics"] if diags else []
check("generated types resolve with no errors", len(reported) == 0, json.dumps(reported)[:400])

hover = (response.get("result") or {}).get("contents", {}).get("value", "")
check("hover works on a generated symbol", "GeneratedGreeting" in hover, repr(hover[:160]))

print("\n== is the generator DLL locked while the server holds the workspace? ==")
dll = next(ROOT.glob("Gen/bin/Debug/netstandard2.0/Gen.dll"), None)
check("generator DLL exists", dll is not None, str(dll))

if dll:
    try:
        with open(dll, "ab"):
            pass
        writable = True
        error = ""
    except OSError as exc:
        writable = False
        error = str(exc)
    check("DLL is still writable by another process", writable, error)

# The decisive test: rebuild the generator while the server is still running.
generator_source = ROOT / "Gen" / "HelloGenerator.cs"
generator_source.write_text(generator_source.read_text().replace("Version => 1", "Version => 2"))

build = subprocess.run(["dotnet", "build", str(ROOT / "Gen" / "Gen.csproj"), "--nologo", "-v", "q"],
                       capture_output=True, text=True)
check("generator rebuilds while the server is live", build.returncode == 0,
      (build.stdout + build.stderr).strip()[-500:])

check("server is still alive afterwards", proc.poll() is None)

rid = send("shutdown", None)
await_response(rid)
send("exit", None, request=False)
try:
    proc.wait(timeout=15)
except subprocess.TimeoutExpired:
    proc.kill()

print()
if failures:
    print(f"FAILED {len(failures)}: " + "; ".join(failures))
    sys.exit(1)
print("all checks passed")
