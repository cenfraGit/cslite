#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Open Emacs with the throwaway cslite config and a sample C# file.
# Your real ~/.emacs.d is not loaded.
#
#   ./try.sh                  opens the sandbox project
#   ./try.sh path/to/File.cs  opens that file instead
# ---------------------------------------------------------------------------
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config="$repo/emacs/test-config"

if [ ! -x "$repo/dist/cslite" ]; then
    echo "The server is not built yet. Run this first:"
    echo "    dotnet publish -c Release -o dist"
    exit 1
fi

target="${1:-$HOME/cslite-sandbox/Program.cs}"

if [ ! -f "$target" ]; then
    echo "No such file: $target"
    echo "Pass a .cs file, or create the sandbox with:"
    echo "    dotnet new console -o \"$HOME/cslite-sandbox\""
    exit 1
fi

echo "Config: $config"
echo "File:   $target"
exec emacs --init-directory="$config" "$target"
