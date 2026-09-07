"""Find-references and rename, across a project reference."""
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
SERVER = sys.argv[2]

proc = subprocess.Popen([SERVER], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE)
threading.Thread(target=lambda: [None for _ in proc.stderr], daemon=True).start()

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


def wait(rid):
    while True:
        m = read()
        if m is None:
            raise SystemExit("server closed the connection")
        if m.get("id") == rid and ("result" in m or "error" in m):
            return m


uri = lambda p: p.resolve().as_uri()

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
caps = wait(rid)["result"]["capabilities"]
print("== capabilities ==")
check("references advertised", caps.get("referencesProvider") is True, str(caps))
check("rename advertised", caps.get("renameProvider") is True, str(caps))

send("initialized", {}, request=False)

greeter = ROOT / "Lib" / "Greeter.cs"
program = ROOT / "App" / "Program.cs"
for f in (greeter, program):
    send("textDocument/didOpen", {"textDocument": {
        "uri": uri(f), "languageId": "csharp", "version": 1, "text": f.read_text()}},
        request=False)

print("\n== find references (declaration in Lib, use in App) ==")
rid = send("textDocument/references", {
    "textDocument": {"uri": uri(greeter)},
    "position": {"line": 6, "character": 19},        # `Greet` in its declaration
    "context": {"includeDeclaration": True},
})
locations = wait(rid).get("result") or []
files = sorted({Path(l["uri"].replace("file:///", "")).name for l in locations})
check("found more than one location", len(locations) >= 2, json.dumps(locations)[:400])
check("includes the declaration in Greeter.cs", "Greeter.cs" in files, str(files))
check("includes the use in Program.cs, another project", "Program.cs" in files, str(files))

print("\n== find references without the declaration ==")
rid = send("textDocument/references", {
    "textDocument": {"uri": uri(greeter)},
    "position": {"line": 6, "character": 19},
    "context": {"includeDeclaration": False},
})
fewer = wait(rid).get("result") or []
check("declaration is excluded", len(fewer) < len(locations),
      f"{len(fewer)} vs {len(locations)}")

print("\n== references from the call site ==")
rid = send("textDocument/references", {
    "textDocument": {"uri": uri(program)},
    "position": {"line": 4, "character": 27},        # `Greet` at the call
    "context": {"includeDeclaration": True},
})
from_use = wait(rid).get("result") or []
check("same set found from the other end", len(from_use) == len(locations),
      f"{len(from_use)} vs {len(locations)}")

print("\n== rename across projects ==")
rid = send("textDocument/rename", {
    "textDocument": {"uri": uri(greeter)},
    "position": {"line": 6, "character": 19},
    "newName": "Salute",
})
response = wait(rid)
edit = response.get("result")
check("rename returned a workspace edit", edit is not None, json.dumps(response)[:300])

if edit:
    changes = edit.get("changes", {})
    names = sorted(Path(u.replace("file:///", "")).name for u in changes)
    check("edits both files", names == ["Greeter.cs", "Program.cs"], str(names))

    for target, edits in changes.items():
        name = Path(target.replace("file:///", "")).name
        check(f"{name}: every edit inserts the new name",
              all(e["newText"] == "Salute" for e in edits),
              json.dumps(edits)[:200])
        check(f"{name}: ranges are ordered and non-overlapping",
              all(edits[i]["range"]["end"]["line"] < edits[i + 1]["range"]["start"]["line"]
                  or (edits[i]["range"]["end"]["line"] == edits[i + 1]["range"]["start"]["line"]
                      and edits[i]["range"]["end"]["character"] <= edits[i + 1]["range"]["start"]["character"])
                  for i in range(len(edits) - 1)),
              json.dumps(edits)[:300])

    # Apply the edits ourselves and confirm the result is what we expect.
    for target, edits in changes.items():
        path = Path(target.replace("file:///", ""))
        lines = path.read_text().splitlines(keepends=True)
        for e in sorted(edits, key=lambda x: (-x["range"]["start"]["line"],
                                              -x["range"]["start"]["character"])):
            ln = e["range"]["start"]["line"]
            a, b = e["range"]["start"]["character"], e["range"]["end"]["character"]
            lines[ln] = lines[ln][:a] + e["newText"] + lines[ln][b:]
        result = "".join(lines)
        check(f"{path.name}: applying the edit yields valid text",
              "Salute" in result and "Greet(" not in result.replace("Salute(", ""),
              repr(result[:160]))

print("\n== renaming something from a referenced assembly ==")
rid = send("textDocument/rename", {
    "textDocument": {"uri": uri(program)},
    "position": {"line": 4, "character": 2},         # `Console`
    "newName": "Terminal",
})
response = wait(rid)
check("refused with an error rather than a bad edit",
      "error" in response, json.dumps(response)[:300])
if "error" in response:
    check("the message explains why",
          "referenced assembly" in response["error"]["message"],
          response["error"]["message"])

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
