# Tests

Three suites, none of which need a test framework installed.

## protocol_test.py

Drives the server over stdio the way an editor would, and checks the handshake,
diagnostics, hover, cross-project go-to-definition, member completion, syntax
errors, percent-encoded URIs and clean shutdown.

    python tests/protocol_test.py <sample-project-dir> dist/cslite.exe

The sample directory needs to be a restored C# project. Any two-project solution
works; go-to-definition assertions assume a project reference.

## references_rename_test.py

Find-references and rename across a project reference: that both ends of a
reference agree, that includeDeclaration is honoured, that the rename edits are
ordered and non-overlapping, and that renaming a symbol from a referenced
assembly is refused.

    python tests/references_rename_test.py <sample-project-dir> dist/cslite.exe

## signature_help_test.py

Parameter labels and their offsets, active-parameter tracking across commas,
overload ordering, constructors, `params` arrays, half-typed calls with no
closing paren, and returning nothing outside a call.

    python tests/signature_help_test.py <fixture-dir> dist/cslite.exe

## generator_lock_test.py

The regression test for the whole point of this server: it confirms that a
source generator's DLL stays writable, and that the generator project still
rebuilds, while the server has the workspace open.

    python tests/generator_lock_test.py <generator-project-dir> dist/cslite.exe

## emacs-test.el

The real integration test: a live Eglot session in batch mode. Checks project
detection, connection, capability negotiation, diagnostics reaching Flymake,
completion through `completion-at-point`, `xref` definitions, and eldoc hover.

    emacs -Q --batch -l tests/emacs-test.el

Edit `test-repo` and `test-sandbox` at the top of the file to match your paths.

## emacs-refactor-test.el

References and rename driven through xref and `eglot-rename` in a live session.
It edits buffers but never saves them, so the project on disk is untouched.

    CSLITE_SANDBOX=/path/to/project emacs -Q --batch -l tests/emacs-refactor-test.el

Results are written to a file rather than stdout, because a batch Emacs that is
killed mid-run loses whatever is still sitting in its stdout buffer.

## Notes on driving Eglot from batch

Note that `eglot-ensure` defers connecting to `post-command-hook`, which never
runs under `--batch`, and Flymake runs its backends from an idle timer that also
never fires. The test connects directly and calls `flymake-start` itself.
