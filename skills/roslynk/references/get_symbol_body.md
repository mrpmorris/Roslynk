# `get_symbol_body`

SupportsPaging=Y

Returns verbatim declarations of methods, constructors, properties, indexers, events, fields,
local functions or types. Preserves indentation, line endings and spacing; Razor bodies are
generated C#.

## Result

One declaration: project, path and loc headers, a blank line, then source text.
Partial types/methods: parts=<n>, then blocks headed
part=<n>,project=...,path=...,loc=...,lines=<count>. Includes generated implementations
(e.g. GeneratedRegex, LoggerMessage, LibraryImport).

## Errors

Ambiguous names/overloads return candidates. Symbols without source (namespaces or metadata-only
symbols) return NotSupported. Metadata spellings of source types (Ns.Box`1, N.Outer+Inner)
return their source.

## Paging exceptions

startItem counts returned source lines, including leading trivia when requested; count is total
lines. Keep includeLeadingTrivia and part unchanged. startItem past the end returns Invalid with
count. It pages one part; using it with multiple parts without part returns Invalid.

Parts exceeding the budget are listed without text as omitted=Y; headers include truncated=Y
and omittedParts=<k>. Fetch omitted parts with part=<n>, then page normally.
