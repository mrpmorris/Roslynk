# `reload_solution`

Re-evaluates an open solution from disk using MSBuild. Never initiate it; suggest it for stale
models after missed watcher events (e.g. network/WSL/container filesystems), branch switches,
restore, SDK/global.json/build-property changes, or to retry a Faulted load.

Returns immediately with solutionId, status and projects headers. The previous snapshot serves
reads with status Building until replacement is ready. Files are unchanged; failed reloads
return Faulted.
