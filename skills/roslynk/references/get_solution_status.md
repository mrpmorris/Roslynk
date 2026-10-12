# `get_solution_status`

Lists loaded solutions after a blank line, one <solutionId>,<status>,<loaded>/<total> line each.
loaded is live project progress while Building; total is '?' until known after initial loading.

Ready solutions include indented loadDiagnostic=<message> lines for load problems such as
unloadable analyzers/generators, project evaluation failures or missing SDKs. These can explain
unexpected compiler errors. Messages are omitted while Building, Updating or Faulted.

Lists at most 20 messages per solution, then loadDiagnosticsTruncated=<n> for the rest.
Set allLoadDiagnostics=true for all messages.
