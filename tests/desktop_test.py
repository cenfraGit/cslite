"""A wpf project must see its own framework and everything its references see.

Two separate failures look alike here. The base framework ships a WindowsBase
facade, and taking it over the desktop pack's real one turns every wpf type
into CS7069. And msbuild passes project references on to the projects above
them where roslyn does not, so a type two references away is CS0012/CS0246.

    python tests/desktop_test.py fixtures/desktop dist/cslite.exe
"""
import json
import subprocess
import sys
import threading
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
SERVER = sys.argv[2]

proc = subprocess.Popen([SERVER, "--verbose"], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
log = []
threading.Thread(
    target=lambda: [log.append(l.decode("utf-8", "replace").rstrip()) for l in proc.stderr],
    daemon=True).start()

ids = [0]
failures = []


def check(label, ok, detail=""):
    print(("  PASS  " if ok else "  FAIL  ") + label + (f"   {detail}" if detail else ""))
    if not ok:
        failures.append(label)


def send(method, params, request=True):
    msg = {"jsonrpc": "2.0", "method": method, "params": params}
    if request:
        ids[0] += 1
        msg["id"] = ids[0]
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


def wait(rid):
    notes = []
    while True:
        m = read()
        if m is None:
            raise SystemExit("server closed the connection")
        if m.get("id") == rid and ("result" in m or "error" in m):
            return m, notes
        if "method" in m:
            notes.append(m)


uri = lambda p: p.resolve().as_uri()
shell = ROOT / "App" / "Shell.cs"
source = shell.read_text(encoding="utf-8-sig")

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
wait(rid)
send("initialized", {}, request=False)

send("textDocument/didOpen", {"textDocument": {
    "uri": uri(shell), "languageId": "csharp", "version": 1, "text": source}}, request=False)

rid = send("textDocument/hover", {"textDocument": {"uri": uri(shell)},
                                  "position": {"line": 0, "character": 0}})
_, notes = wait(rid)

diags = [n for n in notes if n["method"] == "textDocument/publishDiagnostics"]
reported = diags[-1]["params"]["diagnostics"] if diags else []
codes = [d["code"] for d in reported]

print("== a wpf file using a type two references away ==")
check("diagnostics were published", len(diags) > 0)
check("no errors reported", not any(d["severity"] == 1 for d in reported),
      json.dumps([f"{d['code']}: {d['message'][:60]}" for d in reported[:6]]))
check("wpf types resolve (no CS7069)", "CS7069" not in codes)
check("transitive references resolve (no CS0012 or CS0246)",
      "CS0012" not in codes and "CS0246" not in codes)


def hover_on(needle):
    index = source.find(needle)
    line = source[:index].count("\n")
    character = index - (source.rfind("\n", 0, index) + 1)
    rid = send("textDocument/hover", {"textDocument": {"uri": uri(shell)},
                                      "position": {"line": line, "character": character + 1}})
    response, _ = wait(rid)
    return ((response.get("result") or {}).get("contents") or {}).get("value", "")


print("\n== hover ==")
value = hover_on("Ledger _ledger")
check("Ledger comes from Core", "Core.Ledger" in value, repr(value[:80]))
value = hover_on("Dispatcher.Invoke")
check("Dispatcher resolves", "Dispatcher" in value, repr(value[:80]))

print("\n== log ==")
check("the wpf profile is served by the desktop pack",
      not any("Microsoft.WindowsDesktop.App.WPF" in l and "no reference pack" in l for l in log),
      str([l for l in log if "no reference pack" in l][:2]))

rid = send("shutdown", None)
wait(rid)
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
