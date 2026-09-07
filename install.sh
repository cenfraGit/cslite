#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Publish the server and copy it, plus cslite.el, into your Emacs config so the
# config is self-contained and the same init.el works on every machine.
#
#   ./install.sh                 installs into ~/.emacs.d
#   ./install.sh /path/.emacs.d  installs somewhere else
#
# Stop the server in Emacs first (C-c l s) if it is running.
# ---------------------------------------------------------------------------
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
emacsd="${1:-$HOME/.emacs.d}"

if [ ! -d "$emacsd" ]; then
    echo "No Emacs configuration at $emacsd"
    exit 1
fi

echo "Publishing..."
(cd "$repo" && dotnet publish -c Release -o dist --nologo -v q)

echo "Copying the server to $emacsd/cslite ..."
mkdir -p "$emacsd/cslite"
cp -r "$repo/dist/." "$emacsd/cslite/"
chmod +x "$emacsd/cslite/cslite" 2>/dev/null || true

echo "Copying cslite.el to $emacsd/lisp ..."
mkdir -p "$emacsd/lisp"
cp "$repo/emacs/cslite.el" "$emacsd/lisp/"

echo
echo "Done. Restart Emacs, or M-x cslite-restart in a C# buffer."
