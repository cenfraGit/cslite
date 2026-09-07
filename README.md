# cslite

A small C# language server for Emacs, written in C#.

It gives you diagnostics, hover, go-to-definition, completion, signature help,
find-references and rename, and nothing else. It is about 2,200 lines of code
that you can read in an afternoon.

## What "simple" means here

These are constraints, not aspirations. Each one is a thing the server refuses
to do, and the reason it stays small.

- **No MSBuild.** Projects are discovered by reading the directory tree and the
  csproj XML. Nothing here can start a build, evaluate a target, or hang.
- **No analyzers, and no running source generators.** See below — this is the
  main reason this server exists.
- **Full document sync.** The editor sends the whole buffer on every change.
- **No configuration file.** A directory goes in, a language server comes out.
- **One thread.** Each message is handled to completion before the next is read,
  so there is no race between an edit and the analysis of that edit.
- **No caching layer** beyond what Roslyn does internally.

The C# understanding itself comes from Roslyn, the same compiler `dotnet build`
uses. Writing a C# parser by hand would make the dependency list shorter and the
feature list far poorer.

## Source generators do not get locked

Most C# language servers load your analyzer and source generator assemblies into
their own process in order to run them. On Windows that takes a file lock, and
your next `dotnet build` of the generator project fails with "being used by
another process" until you restart the editor.

cslite never loads those assemblies. Roslyn memory-maps reference metadata and
reads it; no generator code is ever executed. The DLL stays writable.

Instead of running your generators, cslite reads what they last wrote. Add this
to any project with a generator:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

The compiler then writes generated sources under `obj/<Config>/<Tfm>/generated/`,
and cslite picks them up as ordinary files. The same mechanism gives you implicit
usings for free, by reading the `*.g.cs` the build already emitted.

The trade-off is honest: generated code is as fresh as your last build. Change a
generator, rebuild, and the editor sees the new output.

## Building

Requires the .NET SDK 8 or newer.

```bash
dotnet publish -c Release -o dist
```

That produces `dist/cslite` (`dist/cslite.exe` on Windows) plus its
dependencies, around 38 MB, mostly Roslyn.

You do not have to leave it there. `install.cmd` (Windows) or `install.sh`
(Linux) publishes and then copies both the binary and `cslite.el` into your
Emacs configuration:

    <your .emacs.d>/lisp/cslite.el   the glue, committed with your config
    <your .emacs.d>/cslite/          the binary, per machine

That way your init.el never refers to this checkout, so the same configuration
works on every machine and the repository can live anywhere. The server is
looked for in `cslite-directory` (default `.emacs.d/cslite/`), then on `PATH`,
and `M-x cslite-where` reports what it found. Setting `cslite-executable`
overrides the search if you would rather point at a checkout directly.

The server locks its own DLL while running, so stop it in Emacs
(`M-x eglot-shutdown`) before reinstalling.

The server targets `net8.0`, so it runs on any machine with a .NET 8 or newer
runtime. Windows and Linux are both supported; there is no platform-specific
code, only stdio.

## Emacs

Requires Emacs 29 or newer, which ships `csharp-mode` and Eglot. Tested against
Emacs 30.2.

After running the installer, the whole configuration is:

```elisp
(add-to-list 'load-path (expand-file-name "lisp" user-emacs-directory))
(require 'cslite)
(cslite-setup)
```

`emacs/init-snippet.el` is a fuller block, in `use-package` form, with Flymake
and Eglot keybindings. Copy it into your init.el as is; it contains no paths.

`cslite-setup` does two things: registers the server with Eglot, and teaches
`project.el` to treat a directory containing a `.sln`, `.slnx` or `.csproj` as
a project root. That second part matters — without it, project.el only
recognises version-controlled directories, and Eglot would hand the server the
wrong root.

It registers as a fallback, so a tree that *is* under version control keeps the
built-in backend and the `git ls-files` listing that goes with it. Only a
directory git knows nothing about is anchored on its build files instead.

> **On Windows, be careful with `~` in any path you add yourself.** Emacs sets
> `HOME` to `AppData\Roaming` there, so `~` is not your user folder. Paths
> derived from `user-emacs-directory`, as above, are unaffected.

Useful settings:

```elisp
(setq cslite-executable "/path/to/dist/cslite")  ; if it is not on PATH
(setq cslite-verbose t)                          ; debug-level logging
(setq cslite-log-file "/path/to/cslite.log")     ; log to a file as well
(setq cslite-auto-start nil)                     ; do not start automatically
```

Server logs always go to stderr, which Eglot collects into a buffer whose name
ends in `stderr`. That buffer is the first place to look when something is off.
`M-x eglot-events-buffer` shows the protocol traffic itself.

## Trying it out without touching your config

`emacs/test-config/` is a throwaway Emacs configuration that loads nothing but
this server. Point Emacs at it with `--init-directory` (Emacs 29+) and your real
`.emacs.d` is neither read nor modified:

```bash
emacs --init-directory=/path/to/LSP/emacs/test-config /path/to/Program.cs
```

The config works out the repository root from its own location, so there is no
path inside it to edit. `try.cmd` (Windows) and `try.sh` (Linux) wrap that call,
find Emacs, and default to a sandbox project.

Once you are happy with it, move on to `emacs/init-snippet.el` for your real
config.

## Trying it out

Any console project will do:

```bash
dotnet new console -o ~/cslite-sandbox
```

Open one of its `.cs` files. Eglot should report `Connected!` in the echo area
within a second or two; the workspace finishes loading shortly after, and
diagnostics appear once it does. Then check the four features:

| What | How |
|---|---|
| Diagnostics | Write `int n = "text";` and wait for the underline |
| Hover | Put point on a method name; eldoc shows the signature |
| Definition | `M-.` on a symbol, `M-,` to come back |
| Completion | Type a `.` after a variable, then `C-M-i` |
| References | `M-?` on a symbol |
| Rename | `M-x eglot-rename` |
| Signature help | Type `(` after a method name and wait for eldoc |

Completion needs a front end to pop up on its own. With `company` or `corfu` a
menu appears as you type; with neither, `C-M-i` (`completion-at-point`) opens
the `*Completions*` buffer. The throwaway config also turns on
`completion-preview-mode`, built in since Emacs 30, which shows the best match
inline.

Hover is sent as markdown or as plain text depending on what the editor asked
for in `textDocument.hover.contentFormat`. Eglot only accepts markdown when
`markdown-mode` is installed, so without it you get an unfenced signature that
reads correctly in the one-line echo area. `(setq eldoc-echo-area-use-multiline-p t)`
shows the documentation underneath it as well.

If nothing happens, look at the stderr buffer first. `no project can host ...`
means the root was wrong; `loaded 0 document(s)` means no `.cs` files were
found under it.

## Measured behaviour

On a synthetic 500-file, 39,000-line project (Windows, .NET 10 runtime):

| | |
|---|---|
| handshake | 206 ms |
| memory after handshake | 27 MB |
| workspace load and first diagnostics | 2.9 s |
| edit to diagnostics, steady state | 27–75 ms |
| completion, 4,109 items, warm | 31 ms |
| memory, steady state | ~165 MB |

The first completion in a session costs about half a second of JIT warm-up.
Memory is dominated by Roslyn holding the compilation; that is the price of real
type analysis, and it is the one axis on which this server is not light.

## What works

- **Diagnostics** — the same errors and warnings the compiler produces, on every
  keystroke, for the file you are editing.
- **Hover** — full signature plus the `<summary>` from the doc comment. Hovering
  a constructor shows the type's documentation.
- **Go to definition** — including across project references, because all
  projects in the tree are loaded into one solution.
- **Completion** — Roslyn's own completion, so member access, extension methods
  and keywords all behave correctly. This works for types from NuGet packages
  and the framework too: only *navigating* to their source is unavailable.
- **Signature help** — typing `(` shows the parameters, with the one you are
  on highlighted. Overloads are all offered, ordered by parameter count, and it
  keeps working while the call is still half-typed and does not yet compile.
- **Find references** — `M-?`, across every project in the tree.
- **Rename** — `M-x eglot-rename`, across every file and project at once.
  Renaming a symbol that lives in a referenced assembly is refused with an
  explanation rather than a broken edit.

## What does not work yet

- Document and workspace symbols.
- Code actions and quick fixes.
- Decompiling into metadata: go-to-definition on a framework type finds nothing,
  because there is no source to jump to.
- Incremental sync, formatting, semantic highlighting.
- Watching the filesystem. The project layout is read once at startup, so after
  adding a file, adding a NuGet package, or editing a csproj, reconnect with
  `M-x cslite-restart`.

## Known limitations

The csproj reader is deliberately shallow. It reads `OutputType`, `LangVersion`,
`Nullable`, `AllowUnsafeBlocks`, `DefineConstants` and `ProjectReference`, and
ignores everything else — imports, conditions, `Directory.Build.props`, custom
targets. Unusual build logic shows up as slightly wrong diagnostics rather than a
broken editor.

Preprocessor symbols are fixed to a Debug build (`DEBUG` and `TRACE` plus
whatever the csproj defines unconditionally). Code inside a `#if RELEASE` block
will be analysed as inactive.

NuGet references come from `obj/project.assets.json`. A project that has never
been restored will show unresolved-type errors until you run `dotnet restore`.

## Layout

| File | Purpose |
|---|---|
| `src/Program.cs` | Entry point; hands stdout to the protocol and redirects `Console.Out` to stderr |
| `src/MessageStream.cs` | LSP base protocol framing |
| `src/Protocol.cs` | The wire types |
| `src/Server.cs` | Message loop and method dispatch |
| `src/ProjectLoader.cs` | Finds projects and their files without MSBuild |
| `src/References.cs` | Resolves framework and NuGet assemblies |
| `src/Workspace.cs` | Owns the Roslyn solution and applies edits |
| `src/Features.cs` | Diagnostics, hover, definition, completion |
| `src/Conversions.cs` | URI and position conversions |
| `emacs/cslite.el` | Eglot and project.el integration |
