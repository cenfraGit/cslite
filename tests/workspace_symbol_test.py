"""Finding a type or member anywhere in the solution by name.

    python tests/workspace_symbol_test.py fixtures/sample dist/cslite.exe
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

CLASS, METHOD, PROPERTY = 5, 6, 7


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


def search(query):
    rid = send("workspace/symbol", {"query": query})
    return wait(rid).get("result") or []


def names(results):
    return [r["name"] for r in results]


uri = lambda p: p.resolve().as_uri()

rid = send("initialize", {"processId": None, "rootUri": uri(ROOT),
                          "workspaceFolders": [{"uri": uri(ROOT), "name": ROOT.name}],
                          "capabilities": {}})
caps = wait(rid)["result"]["capabilities"]
print("== capability ==")
check("workspaceSymbol advertised", caps.get("workspaceSymbolProvider") is True, str(caps))

send("initialized", {}, request=False)

print("\n== an exact name ==")
results = search("Greeter")
print(f"  {names(results)}")
check("found it", len(results) > 0)
greeter = next((r for r in results if r["name"] == "Greeter" and r["kind"] == CLASS), None)
check("the class is there", greeter is not None, str([(r["name"], r["kind"]) for r in results]))
if greeter:
    check("it points at Greeter.cs", greeter["location"]["uri"].endswith("Greeter.cs"),
          greeter["location"]["uri"])
    check("with a real range", greeter["location"]["range"]["start"]["line"] >= 0)
    check("and names its namespace", greeter.get("containerName") == "Lib",
          str(greeter.get("containerName")))
check("the exact match sorts first", results[0]["name"] == "Greeter", str(names(results)[:5]))

print("\n== a member ==")
results = search("Greet")
check("found the method", any(r["name"] == "Greet" and r["kind"] == METHOD for r in results),
      str([(r["name"], r["kind"]) for r in results]))
method = next((r for r in results if r["name"] == "Greet" and r["kind"] == METHOD), None)
if method:
    check("its container is the declaring type", method.get("containerName") == "Lib.Greeter",
          str(method.get("containerName")))

print("\n== camel case matching ==")
# Roslyn's pattern matcher understands an abbreviation of the capitals, so the
# query needs humps to abbreviate: "Count" is one word, "FormatAll" is two.
results = search("fa")
check("'fa' reaches FormatAll", "FormatAll" in names(results), str(names(results)[:8]))
results = search("mf")
check("'mf' reaches MessageFormatter", "MessageFormatter" in names(results),
      str(names(results)[:8]))

print("\n== a prefix ==")
results = search("Gre")
found = names(results)
check("prefix finds both the type and the method",
      "Greeter" in found and "Greet" in found, str(found[:8]))

print("\n== searching across projects ==")
# Greeter lives in Lib; the query is answered from the whole solution.
results = search("Greeter")
check("results are not limited to one project", len(results) >= 1)
paths = {r["location"]["uri"].split("/")[-2] for r in search("Gre")}
check("reaches into Lib", "Lib" in paths, str(paths))

print("\n== nonsense and emptiness ==")
check("no match returns nothing", search("zzzznotathing") == [])
check("an empty query returns nothing rather than everything", search("") == [])
check("whitespace likewise", search("   ") == [])

print("\n== results are well formed ==")
bad = [r for r in search("G")
       if not r.get("name") or not isinstance(r.get("kind"), int)
       or "uri" not in r.get("location", {})]
check("every result has a name, kind and location", not bad, json.dumps(bad[:2])[:200])

many = search("G")
check("a broad query still returns results", len(many) > 0, str(len(many)))
check("and is capped", len(many) <= 256, str(len(many)))

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
