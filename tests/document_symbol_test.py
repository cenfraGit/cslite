"""The outline of a file: every declaration, nested as written.

    python tests/document_symbol_test.py fixtures/outline dist/cslite.exe
"""
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

ids = [0]
failures = []

NAMESPACE, CLASS, METHOD, PROPERTY, FIELD = 3, 5, 6, 7, 8
CONSTRUCTOR, ENUM, INTERFACE, FUNCTION = 9, 10, 11, 12
CONSTANT, ENUM_MEMBER, STRUCT, EVENT, OPERATOR = 14, 22, 23, 24, 25


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
    while True:
        m = read()
        if m is None:
            raise SystemExit("server closed the connection")
        if m.get("id") == rid and ("result" in m or "error" in m):
            return m


def walk(symbols, depth=0):
    for symbol in symbols:
        yield depth, symbol
        yield from walk(symbol.get("children") or [], depth + 1)


def find(symbols, name):
    for _, symbol in walk(symbols):
        if symbol["name"] == name:
            return symbol
    return None


def before(a, b):
    """Is position a at or before position b?"""
    return (a["line"], a["character"]) <= (b["line"], b["character"])


def contains(outer, inner):
    return (before(outer["start"], inner["start"])
            and before(inner["end"], outer["end"]))


uri = lambda p: p.resolve().as_uri()
shapes = ROOT / "Shapes.cs"

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
caps = wait(rid)["result"]["capabilities"]
print("== capability ==")
check("documentSymbol advertised", caps.get("documentSymbolProvider") is True, str(caps))

send("initialized", {}, request=False)
send("textDocument/didOpen", {"textDocument": {
    "uri": uri(shapes), "languageId": "csharp", "version": 1,
    "text": shapes.read_text()}}, request=False)

rid = send("textDocument/documentSymbol", {"textDocument": {"uri": uri(shapes)}})
symbols = wait(rid).get("result") or []

print("\n== the outline ==")
for depth, symbol in walk(symbols):
    print(f"  {'  ' * depth}{symbol['name']}  [{symbol['kind']}]"
          + (f"  {symbol['detail']}" if symbol.get("detail") else ""))

print("\n== shape ==")
check("something came back", len(symbols) > 0)
check("the namespace is the single root",
      len(symbols) == 1 and symbols[0]["kind"] == NAMESPACE,
      str([(s["name"], s["kind"]) for s in symbols]))

# A type and its constructor share a name, so index by name AND kind.
found = {(symbol["name"], symbol["kind"]) for _, symbol in walk(symbols)}
names = {symbol["name"] for _, symbol in walk(symbols)}

print("\n== every kind of declaration ==")
for name, kind, label in [
    ("Outline", NAMESPACE, "namespace"),
    ("IShape", INTERFACE, "interface"),
    ("Colour", ENUM, "enum"),
    ("Red", ENUM_MEMBER, "enum member"),
    ("Point", STRUCT, "struct"),
    ("Painted", FUNCTION, "delegate"),
    ("Canvas", CLASS, "class"),
    ("MaxLayers", CONSTANT, "const"),
    ("_layers", FIELD, "field"),
    ("OnPainted", EVENT, "event"),
    ("Canvas", CLASS, "class"),
    ("Area", PROPERTY, "property"),
    ("this[]", PROPERTY, "indexer"),
    ("Paint", METHOD, "method"),
    ("operator +", OPERATOR, "operator"),
    ("Layer", CLASS, "nested class"),
]:
    check(f"{label} '{name}'", (name, kind) in found,
          f"kinds seen: {sorted(k for n, k in found if n == name)}")

check("both names of a two-variable field",
      "_width" in names and "_height" in names,
      str([n for n in names if n.startswith("_")]))
check("the destructor", "~Canvas" in names, str(sorted(names)[:20]))
check("the constructor is a constructor",
      any(s["kind"] == CONSTRUCTOR for _, s in walk(symbols) if s["name"] == "Canvas"),
      str([s["kind"] for _, s in walk(symbols) if s["name"] == "Canvas"]))

print("\n== nesting ==")
canvas = next((s for _, s in walk(symbols) if s["name"] == "Canvas" and s["kind"] == CLASS), None)
check("Canvas has children", canvas and len(canvas.get("children") or []) > 0)
if canvas:
    inner = find(canvas.get("children") or [], "Layer")
    check("Layer is nested inside Canvas", inner is not None)
    if inner:
        check("Clear is nested inside Layer",
              find(inner.get("children") or [], "Clear") is not None)

print("\n== ranges are well formed ==")
bad_selection = [s["name"] for _, s in walk(symbols)
                 if not contains(s["range"], s["selectionRange"])]
check("selectionRange sits inside range", not bad_selection, str(bad_selection))

bad_children = []
for _, symbol in walk(symbols):
    for child in symbol.get("children") or []:
        if not contains(symbol["range"], child["range"]):
            bad_children.append(f"{child['name']} not inside {symbol['name']}")
check("children sit inside their parent", not bad_children, str(bad_children))

print("\n== detail ==")
paint = find(symbols, "Paint")
check("a method carries its signature",
      paint and paint.get("detail") and "Colour" in paint["detail"],
      str(paint.get("detail") if paint else None))

print("\n== a file that does not compile still has an outline ==")
broken = shapes.read_text().replace("public void Paint(Colour colour, int layer = 0) { }",
                                    "public void Paint(Colour colour, int layer = 0) { this.")
send("textDocument/didChange", {
    "textDocument": {"uri": uri(shapes), "version": 2},
    "contentChanges": [{"text": broken}]}, request=False)
rid = send("textDocument/documentSymbol", {"textDocument": {"uri": uri(shapes)}})
partial = wait(rid).get("result") or []
still = {s["name"] for _, s in walk(partial)}
check("the outline survives a syntax error", "Canvas" in still and "Colour" in still,
      str(sorted(still)[:12]))

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
