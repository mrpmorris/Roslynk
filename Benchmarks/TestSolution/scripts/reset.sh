#!/usr/bin/env bash
# Restores the benchmark solution to its committed state and removes build output.
# Run it only inside a benchmark worktree; it is scoped to this directory.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

git restore -- .
git clean -xdf -- .

echo "TestSolution reset to its committed state: $(pwd)"
