"""Signature help: parameter labels, active parameter tracking, overloads."""
import json
import subprocess
import sys
import threading
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
program = ROOT / "Program.cs"
version = [1]


def retype(text):
    """Replace the buffer, as an editor would while the user types."""
    version[0] += 1
    send("textDocument/didChange", {
        "textDocument": {"uri": uri(program), "version": version[0]},
        "contentChanges": [{"text": text}]}, request=False)


def signature_at(line, character):
    rid = send("textDocument/signatureHelp", {
        "textDocument": {"uri": uri(program)},
        "position": {"line": line, "character": character}})
    return wait(rid).get("result")


rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
caps = wait(rid)["result"]["capabilities"]
print("== capability ==")
check("signatureHelp advertised", caps.get("signatureHelpProvider") is not None, str(caps))
if caps.get("signatureHelpProvider"):
    check("triggers on ( and ,",
          caps["signatureHelpProvider"]["triggerCharacters"] == ["(", ","],
          str(caps["signatureHelpProvider"]))

send("initialized", {}, request=False)
source = program.read_text()
send("textDocument/didOpen", {"textDocument": {
    "uri": uri(program), "languageId": "csharp", "version": 1, "text": source}}, request=False)

print("\n== inside a simple call ==")
# line 3: `var sum = api.Add(1, 2);`  -- offset 18 is just after the "("
help_ = signature_at(3, 18)
check("returned a signature", help_ is not None, json.dumps(help_)[:200])
if help_:
    sig = help_["signatures"][help_["activeSignature"]]
    check("label shows the full signature", sig["label"] == "int Add(int left, int right)", sig["label"])
    check("first parameter is active", help_["activeParameter"] == 0, str(help_["activeParameter"]))
    check("documentation carried through", "Adds two numbers" in (sig.get("documentation") or ""),
          str(sig.get("documentation")))
    labels = [sig["label"][p["label"][0]:p["label"][1]] for p in sig["parameters"]]
    check("parameter offsets point at the right text", labels == ["int left", "int right"], str(labels))

print("\n== after a comma ==")
help_ = signature_at(3, 21)          # after "1, "
check("second parameter becomes active", help_ and help_["activeParameter"] == 1,
      str(help_ and help_["activeParameter"]))

print("\n== half-typed call, no closing paren ==")
retype("using Sig;\n\nvar api = new Api();\nvar sum = api.Add(\n")
help_ = signature_at(3, 18)
check("still resolves while the call is incomplete", help_ is not None, json.dumps(help_)[:200])
if help_:
    check("names the method", "Add" in help_["signatures"][help_["activeSignature"]]["label"])

print("\n== overloads ==")
retype("using Sig;\n\nvar api = new Api();\napi.Overloaded(\n")
help_ = signature_at(3, 15)
check("all three overloads offered", help_ and len(help_["signatures"]) == 3,
      str(help_ and [s["label"] for s in help_["signatures"]]))
if help_:
    check("ordered by parameter count",
          [len(s["parameters"]) for s in help_["signatures"]] == [1, 2, 3],
          str([s["label"] for s in help_["signatures"]]))

retype("using Sig;\n\nvar api = new Api();\napi.Overloaded(1, \n")
help_ = signature_at(3, 18)
check("picks an overload that has a second parameter",
      help_ and len(help_["signatures"][help_["activeSignature"]]["parameters"]) > 1,
      str(help_ and help_["signatures"][help_["activeSignature"]]["label"]))

print("\n== constructor ==")
retype("using Sig;\n\nvar other = new Api(\n")
help_ = signature_at(2, 20)
check("constructor signature offered", help_ is not None, json.dumps(help_)[:200])
if help_:
    check("labelled with the type name",
          help_["signatures"][0]["label"].startswith("Api("),
          help_["signatures"][0]["label"])

print("\n== params array ==")
retype('using Sig;\n\nvar api = new Api();\nvar j = api.Join(", ", \n')
help_ = signature_at(3, 23)
if help_:
    sig = help_["signatures"][help_["activeSignature"]]
    check("params modifier rendered", "params" in sig["label"], sig["label"])

print("\n== outside any call ==")
retype("using Sig;\n\nvar api = new Api();\nvar x = 1;\n")
help_ = signature_at(3, 9)
check("nothing offered outside a call", help_ is None, json.dumps(help_)[:200])

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
