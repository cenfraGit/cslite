"""Code actions: the fixes offered for the errors under the cursor.

    python tests/code_action_test.py fixtures/sandbox dist/cslite.exe
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
program = ROOT / "Program.cs"
version = [1]


def retype(text):
    version[0] += 1
    send("textDocument/didChange", {
        "textDocument": {"uri": uri(program), "version": version[0]},
        "contentChanges": [{"text": text}]}, request=False)


def actions_at(text, marker, diagnostics=None):
    """Ask for the fixes offered where `marker` appears."""
    retype(text)
    index = text.index(marker)
    line = text[:index].count("\n")
    character = index - (text.rfind("\n", 0, index) + 1)
    rid = send("textDocument/codeAction", {
        "textDocument": {"uri": uri(program)},
        "range": {"start": {"line": line, "character": character},
                  "end": {"line": line, "character": character + len(marker)}},
        "context": {"diagnostics": diagnostics or []}})
    response, _ = wait(rid)
    return response.get("result") or []


rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
caps = wait(rid)[0]["result"]["capabilities"]
print("== capability ==")
check("codeAction advertised", caps.get("codeActionProvider") is True, str(caps))

send("initialized", {}, request=False)
send("textDocument/didOpen", {"textDocument": {
    "uri": uri(program), "languageId": "csharp", "version": 1,
    "text": program.read_text()}}, request=False)

print("\n== a type that needs a using ==")
# StringBuilder is not in the implicit usings, so this is a real CS0246.
source = """var builder = new StringBuilder();
builder.Append("hi");
"""
actions = actions_at(source, "StringBuilder")
titles = [a["title"] for a in actions]
check("some fix was offered", len(actions) > 0, str(titles))
check("offers the using directive", any("System.Text" in t for t in titles), str(titles[:8]))

using_fix = next((a for a in actions if "using System.Text" in a["title"]), None)
if using_fix:
    check("it is a quickfix", using_fix.get("kind") == "quickfix", str(using_fix.get("kind")))
    check("it carries the edit outright", using_fix.get("edit") is not None)
    check("it names the diagnostic it fixes",
          any(d["code"] == "CS0246" for d in using_fix.get("diagnostics") or []),
          json.dumps(using_fix.get("diagnostics"))[:200])

    changes = using_fix["edit"]["changes"]
    edits = next(iter(changes.values()))
    check("exactly one insertion", len(edits) == 1, json.dumps(edits)[:200])
    check("inserts the using", "using System.Text;" in edits[0]["newText"],
          json.dumps(edits[0])[:200])
    check("at the top of the file", edits[0]["range"]["start"]["line"] == 0,
          json.dumps(edits[0]["range"]))

print("\n== applying it actually fixes the error ==")
if using_fix:
    edits = next(iter(using_fix["edit"]["changes"].values()))
    lines = source.splitlines(keepends=True)
    edit = edits[0]
    start_line, start_char = edit["range"]["start"]["line"], edit["range"]["start"]["character"]
    end_line, end_char = edit["range"]["end"]["line"], edit["range"]["end"]["character"]
    if start_line == end_line:
        lines[start_line] = (lines[start_line][:start_char] + edit["newText"]
                             + lines[start_line][end_char:])
    fixed = "".join(lines)

    retype(fixed)
    rid = send("textDocument/hover", {"textDocument": {"uri": uri(program)},
                                      "position": {"line": 0, "character": 0}})
    _, notes = wait(rid)
    diags = [n for n in notes if n["method"] == "textDocument/publishDiagnostics"]
    reported = diags[-1]["params"]["diagnostics"] if diags else []
    errors = [d for d in reported if d["severity"] == 1]
    check("no errors left after applying", len(errors) == 0,
          json.dumps([d["message"][:60] for d in errors]))

print("\n== nothing wrong, nothing offered ==")
clean = "var n = 1;\nSystem.Console.WriteLine(n);\n"
actions = actions_at(clean, "var n")
check("no fixes on correct code", len(actions) == 0,
      str([a["title"] for a in actions][:5]))

print("\n== an unknown name ==")
actions = actions_at("Undefined.Thing();\n", "Undefined")
print(f"  offered: {[a['title'] for a in actions][:6]}")
check("responds without failing", isinstance(actions, list))

rid = send("shutdown", None)
wait(rid)
send("exit", None, request=False)
try:
    proc.wait(timeout=15)
except subprocess.TimeoutExpired:
    proc.kill()

print("\n--- provider discovery ---")
for line in log:
    if "code fixes:" in line:
        print("   " + line)

print()
if failures:
    print(f"FAILED {len(failures)}: " + "; ".join(failures))
    sys.exit(1)
print("all checks passed")
