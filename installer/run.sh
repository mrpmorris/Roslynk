#!/usr/bin/env bash
# Run Roslynk as a foreground console app on Linux / WSL / macOS.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/Source/App/Morris.Roslynk.Mcp/Morris.Roslynk.Mcp.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
	echo "dotnet is not on PATH. Install the .NET 10 SDK first." >&2
	exit 1
fi

exec dotnet run --project "$PROJECT" "$@"