# `reload_solution`

Forces a fresh from-disk reload of an already-open solution, re-running MSBuild evaluation. The
file watcher reloads automatically on most edits, so this is the manual backstop: use it when the
model may be stale and the watcher missed a change (dropped events, or a
network/WSL/container filesystem), after an out-of-band change the incremental model cannot absorb
(a dotnet restore, an SDK/global.json or Directory.Build.props change, a branch switch), or to
retry a Faulted load. Returns header-only 'solutionId', 'status', 'projects'. Returns immediately;
the previous snapshot keeps serving reads (status Building) until the fresh one is ready. No
effect on files. A failed reload is returned as a Faulted error. Never call this on your own
initiative; suggest it to the user when the model may be stale.
