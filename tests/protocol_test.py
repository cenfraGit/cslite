"""A tiny LSP client that drives cslite the way Eglot would."""
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
SERVER = sys.argv[2]

proc = subprocess.Popen(
    [SERVER, "--verbose"],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
)

stderr_lines = []


def drain_stderr():
    for raw in proc.stderr:
        stderr_lines.append(raw.decode("utf-8", "replace").rstrip())


threading.Thread(target=drain_stderr, daemon=True).start()

_next_id = [0]


def send(method, params, is_request=True):
    message = {"jsonrpc": "2.0", "method": method, "params": params}
    if is_request:
        _next_id[0] += 1
        message["id"] = _next_id[0]
    body = json.dumps(message).encode("utf-8")
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
    proc.stdin.flush()
    return message.get("id")


def read():
    headers = {}
    while True:
        line = proc.stdout.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            break
        name, _, value = line.decode("ascii").partition(":")
        headers[name.strip().lower()] = value.strip()
    length = int(headers["content-length"])
    return json.loads(proc.stdout.read(length))


def await_response(request_id, timeout=90):
    """Collects notifications until the matching response arrives."""
    deadline = time.time() + timeout
    notifications = []
    while time.time() < deadline:
        message = read()
        if message is None:
            raise SystemExit("server closed the connection")
        if message.get("id") == request_id and ("result" in message or "error" in message):
            return message, notifications
        if "method" in message:
            notifications.append(message)
    raise SystemExit(f"timed out waiting for response {request_id}")


def uri_of(path):
    return path.resolve().as_uri()


failures = []


def check(label, condition, detail=""):
    print(("  PASS  " if condition else "  FAIL  ") + label + (f"   {detail}" if detail else ""))
    if not condition:
        failures.append(label)


# --- handshake ------------------------------------------------------------
rid = send("initialize", {
    "processId": None,
    "rootUri": uri_of(ROOT),
    "workspaceFolders": [{"uri": uri_of(ROOT), "name": ROOT.name}],
    "capabilities": {},
})
response, _ = await_response(rid)
caps = response["result"]["capabilities"]
print("== handshake ==")
check("initialize returns capabilities", caps.get("hoverProvider") is True, str(caps))
check("server identifies itself", response["result"]["serverInfo"]["name"] == "cslite")

send("initialized", {}, is_request=False)

# --- open a clean file ----------------------------------------------------
program = ROOT / "App" / "Program.cs"
source = program.read_text()
send("textDocument/didOpen", {"textDocument": {
    "uri": uri_of(program), "languageId": "csharp", "version": 1, "text": source,
}}, is_request=False)

# Force a round trip so the didOpen has certainly been processed.
rid = send("textDocument/hover", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": 4, "character": 27},   # `Greet` in greeter.Greet(...)
})
response, notifications = await_response(rid)

print("\n== diagnostics on a valid file ==")
diags = [n for n in notifications if n["method"] == "textDocument/publishDiagnostics"]
check("diagnostics were published", len(diags) > 0)
if diags:
    reported = diags[-1]["params"]["diagnostics"]
    check("a correct file reports nothing", len(reported) == 0,
          json.dumps(reported)[:400])

print("\n== hover ==")
hover = response.get("result")
check("hover returns content", hover is not None, json.dumps(response)[:300])
if hover:
    value = hover["contents"]["value"]
    check("hover shows the full signature", "Lib.Greeter.Greet(string name)" in value, repr(value[:200]))
    check("hover includes the doc summary", "greets someone by name" in value.lower(), repr(value[:200]))

rid = send("textDocument/hover", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": 2, "character": 20},   # `Greeter` in `new Greeter()`
})
response, _ = await_response(rid)
ctor = (response.get("result") or {}).get("contents", {}).get("value", "")
check("hover on a constructor falls back to the type docs",
      "greetings" in ctor.lower(), repr(ctor[:200]))

print("\n== percent-encoded URIs ==")
# Emacs sends the drive colon encoded, as file:///c%3A/Users/...  .NET's Uri
# then refuses to treat it as a DOS path and hands back "/c:/Users/...", which
# used to resolve against the current drive as "c:\c:\Users\...".
scheme, sep, rest = uri_of(program).partition("://")
if ":" in rest:
    encoded = scheme + sep + rest.replace(":", "%3A", 1)
    rid = send("textDocument/hover", {
        "textDocument": {"uri": encoded},
        "position": {"line": 4, "character": 27},
    })
    response, _ = await_response(rid)
    check("an encoded drive letter still finds the document",
          response.get("result") is not None, encoded)
else:
    print("  SKIP  no drive letter on this platform")

print("\n== go to definition (across a project reference) ==")
rid = send("textDocument/definition", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": 4, "character": 27},   # `Greet` in greeter.Greet(...)
})
response, _ = await_response(rid)
locations = response.get("result") or []
check("definition resolved", len(locations) > 0, json.dumps(response)[:300])
if locations:
    target = locations[0]["uri"]
    check("jumps into the referenced project", target.endswith("Lib/Greeter.cs"), target)
    check("lands on the Greet method", locations[0]["range"]["start"]["line"] == 6,
          json.dumps(locations[0]["range"]))

print("\n== completion ==")
edited = source.replace("Console.WriteLine(greeter.Greet(list[0]));", "greeter.")
send("textDocument/didChange", {
    "textDocument": {"uri": uri_of(program), "version": 2},
    "contentChanges": [{"text": edited}],
}, is_request=False)

dot_line = edited.splitlines().index("greeter.")
rid = send("textDocument/completion", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": dot_line, "character": 8},
    "context": {"triggerKind": 2, "triggerCharacter": "."},
})
response, _ = await_response(rid)
items = (response.get("result") or {}).get("items", [])
labels = [i["label"] for i in items]
check("member completion returns items", len(items) > 0, json.dumps(response)[:300])
check("offers the Greet method", "Greet" in labels, str(labels[:20]))
check("offers the Count property", "Count" in labels, str(labels[:20]))
check("does not offer unrelated globals", "Console" not in labels, str(labels[:20]))
if "Greet" in labels:
    kind = items[labels.index("Greet")].get("kind")
    check("Greet is tagged as a method", kind == 2, f"kind={kind}")

print("\n== diagnostics on a broken file ==")
broken = "using Lib;\n\nvar g = new Greeter();\nint n = g.Greet(\"x\");\nundefined_call();\n"
send("textDocument/didChange", {
    "textDocument": {"uri": uri_of(program), "version": 3},
    "contentChanges": [{"text": broken}],
}, is_request=False)
rid = send("textDocument/hover", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": 0, "character": 0},
})
response, notifications = await_response(rid)
diags = [n for n in notifications if n["method"] == "textDocument/publishDiagnostics"]
reported = diags[-1]["params"]["diagnostics"] if diags else []
codes = sorted({d["code"] for d in reported})
check("errors are reported", len(reported) > 0, str(codes))
check("catches the type mismatch (CS0029)", "CS0029" in codes, str(codes))
check("catches the unknown call (CS0103)", "CS0103" in codes, str(codes))
if reported:
    first = reported[0]
    check("severity is Error", any(d["severity"] == 1 for d in reported))
    check("range is well formed", first["range"]["start"]["line"] >= 0, json.dumps(first))

print("\n== syntax errors ==")
send("textDocument/didChange", {
    "textDocument": {"uri": uri_of(program), "version": 4},
    "contentChanges": [{"text": "class Broken { void M( }\n"}],
}, is_request=False)
rid = send("textDocument/hover", {
    "textDocument": {"uri": uri_of(program)},
    "position": {"line": 0, "character": 0},
})
response, notifications = await_response(rid)
diags = [n for n in notifications if n["method"] == "textDocument/publishDiagnostics"]
reported = diags[-1]["params"]["diagnostics"] if diags else []
check("syntax errors reach the editor", len(reported) > 0,
      str(sorted({d["code"] for d in reported})))

# --- shutdown -------------------------------------------------------------
print("\n== shutdown ==")
rid = send("shutdown", None)
response, _ = await_response(rid)
check("shutdown succeeds without an error", "error" not in response, json.dumps(response)[:200])
send("exit", None, is_request=False)

try:
    code = proc.wait(timeout=15)
except subprocess.TimeoutExpired:
    proc.kill()
    code = "timeout"
check("exits cleanly", code == 0, f"exit code {code}")

print("\n== server log (tail) ==")
time.sleep(0.3)
for line in stderr_lines[-14:]:
    print("   " + line)

print()
if failures:
    print(f"FAILED {len(failures)}: " + "; ".join(failures))
    sys.exit(1)
print("all checks passed")
