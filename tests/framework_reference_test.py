"""A project needing a second shared framework must resolve it.

The Web SDK declares Microsoft.AspNetCore.App alongside the base framework.
Resolving only the base one leaves every ASP.NET type unresolved, which shows
up as a wall of CS0234 and CS0246 in a file that builds perfectly well.

    python tests/framework_reference_test.py fixtures/web dist/cslite.exe
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
controller = ROOT / "Controllers" / "ThingsController.cs"

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
wait(rid)
send("initialized", {}, request=False)

send("textDocument/didOpen", {"textDocument": {
    "uri": uri(controller), "languageId": "csharp", "version": 1,
    "text": controller.read_text(encoding="utf-8-sig")}}, request=False)

rid = send("textDocument/hover", {"textDocument": {"uri": uri(controller)},
                                  "position": {"line": 0, "character": 0}})
_, notes = wait(rid)

diags = [n for n in notes if n["method"] == "textDocument/publishDiagnostics"]
reported = diags[-1]["params"]["diagnostics"] if diags else []
errors = [d for d in reported if d["severity"] == 1]

print("== an ASP.NET controller ==")
check("diagnostics were published", len(diags) > 0)
check("no errors reported", len(errors) == 0,
      json.dumps([f"{d['code']}: {d['message'][:60]}" for d in errors[:6]]))

print("\n== ASP.NET types resolve ==")
source = controller.read_text(encoding="utf-8-sig")


def hover_on(needle):
    index = source.find(needle)
    if index < 0:
        return ""
    line = source[:index].count("\n")
    character = index - (source.rfind("\n", 0, index) + 1)
    rid = send("textDocument/hover", {"textDocument": {"uri": uri(controller)},
                                      "position": {"line": line, "character": character + 1}})
    response, _ = wait(rid)
    return ((response.get("result") or {}).get("contents") or {}).get("value", "")


for needle, expected in [("ControllerBase", "Microsoft.AspNetCore.Mvc.ControllerBase"),
                         ("IActionResult", "Microsoft.AspNetCore.Mvc.IActionResult")]:
    value = hover_on(needle)
    check(f"{needle} resolves", expected in value, repr(value[:80]))

print("\n== reference packs matched to the target framework ==")
aspnet = [l for l in log if "Microsoft.AspNetCore.App:" in l]
netcore = [l for l in log if "Microsoft.NETCore.App:" in l]
check("the ASP.NET pack was used", len(aspnet) > 0, str(log[-3:]))
# The fixture targets net8.0; a newer pack is almost certainly installed, and
# picking it would mean compiling against APIs the build does not have.
check("ASP.NET pack is the net8.0 one", any("net8.0" in l for l in aspnet), str(aspnet))
check("base pack is the net8.0 one", any("net8.0" in l for l in netcore), str(netcore))

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
