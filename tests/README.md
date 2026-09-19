# Tests

Eleven suites, none of which need a test framework installed.

## Fixtures

Every suite runs against a real C# project, so generate them first:

    python tests/make_fixtures.py fixtures

That writes four projects and builds each one, so `obj/` holds the artifacts
cslite reads instead of running MSBuild itself. The sandbox deliberately fails
to compile, which is what gives the diagnostics tests something to find.

Then point each suite at the fixture it wants; every command below assumes
`fixtures/` and a published `dist/`.

## protocol_test.py

Drives the server over stdio the way an editor would, and checks the handshake,
diagnostics, hover, cross-project go-to-definition, member completion, syntax
errors, percent-encoded URIs and clean shutdown.

    python tests/protocol_test.py fixtures/sample dist/cslite.exe

## references_rename_test.py

Find-references and rename across a project reference: that both ends of a
reference agree, that includeDeclaration is honoured, that the rename edits are
ordered and non-overlapping, and that renaming a symbol from a referenced
assembly is refused.

    python tests/references_rename_test.py fixtures/sample dist/cslite.exe

## signature_help_test.py

Parameter labels and their offsets, active-parameter tracking across commas,
overload ordering, constructors, `params` arrays, half-typed calls with no
closing paren, and returning nothing outside a call.

    python tests/signature_help_test.py fixtures/sighelp dist/cslite.exe

## framework_reference_test.py

That a project needing a second shared framework gets it. The Web SDK declares
Microsoft.AspNetCore.App alongside the base framework; resolving only the base
one leaves every ASP.NET type unresolved. Also checks that reference packs are
matched to the project's target framework rather than the newest installed.

    python tests/framework_reference_test.py fixtures/web dist/cslite.exe

## document_symbol_test.py

Every kind of declaration in one file -- namespaces, types, members, operators,
indexers, multi-variable fields -- with the nesting, the range invariants LSP
requires, and that the outline survives a syntax error.

    python tests/document_symbol_test.py fixtures/outline dist/cslite.exe

## code_action_test.py

That an error offers a fix, that the fix arrives with its edit attached, and
that applying the edit actually clears the error. Also that correct code offers
nothing.

    python tests/code_action_test.py fixtures/sandbox dist/cslite.exe

## generator_lock_test.py

The regression test for the whole point of this server: it confirms that a
source generator's DLL stays writable, and that the generator project still
rebuilds, while the server has the workspace open.

    python tests/generator_lock_test.py fixtures/genlock dist/cslite.exe

## watchdog_test.py

That the server exits when the editor that started it disappears, stays running
while the editor lives, and behaves normally when no process id is given. Takes
about a minute, because the watchdog polls every ten seconds.

    python tests/watchdog_test.py fixtures/sample dist/cslite.exe

## emacs-test.el

The real integration test: a live Eglot session in batch mode. Checks project
detection, connection, capability negotiation, diagnostics reaching Flymake,
completion through `completion-at-point`, `xref` definitions, and eldoc hover.

    CSLITE_SANDBOX=fixtures/sandbox emacs -Q --batch -l tests/emacs-test.el

The repository root is derived from the test file's own location, so moving the
checkout does not break it; `CSLITE_REPO` overrides that if needed.

## emacs-refactor-test.el

References and rename driven through xref and `eglot-rename` in a live session.
It edits buffers but never saves them, so the project on disk is untouched.

    CSLITE_SANDBOX=fixtures/sandbox emacs -Q --batch -l tests/emacs-refactor-test.el

Results are written to a file rather than stdout, because a batch Emacs that is
killed mid-run loses whatever is still sitting in its stdout buffer.

## emacs-imenu-test.el

That the outline reaches `imenu`, which is what makes `consult-imenu` work.

    CSLITE_OUTLINE=fixtures/outline emacs -Q --batch -l tests/emacs-imenu-test.el

## Notes on driving Eglot from batch

Note that `eglot-ensure` defers connecting to `post-command-hook`, which never
runs under `--batch`, and Flymake runs its backends from an idle timer that also
never fires. The test connects directly and calls `flymake-start` itself.
