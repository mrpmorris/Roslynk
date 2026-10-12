# `open_solution`

Loads a C# solution (.sln or .slnx) into Roslynk so its projects and code can be queried. Returns
'solutionId', 'status', 'projects', 'loadDiagnostics' headers, a blank line, then one
'<projectPath>,<documentCount>' line per project. Returns immediately: the solution loads in the
background, so status is Building (and the body empty) until it is Ready. Idempotent: opening the
same solution again returns the same instance. A solution file that does not exist returns
immediately as a NotFound error; a failed load is returned as a Faulted error.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Open a C# solution").
