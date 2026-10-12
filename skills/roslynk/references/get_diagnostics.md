# `get_diagnostics`

SupportsPaging=Y

Returns compiler and analyzer diagnostics for the solution. Costly: it compiles and analyzes every
project the edits since the last call affect - make all the edits a task needs first and call this
once at the end, not after every edit; a repeat call with nothing changed in between is
near-instant.
Per-severity counts are always in the header so filtering is never silent; diagnostics nest file ->
severity:
```
  errors=<n>
  warnings=<n>
  infos=<n>
  hidden=<n>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<severity>
  \t\t\t\t<id>,<line:col>,<message>
```
where severity is the plural group errors|warnings|infos|hidden and the free-text message is last.
Analyzers (NetAnalyzers / IDE rules) run by default; set includeAnalyzers false for a faster
compiler-only pass.
Filters combine and narrow the result after compilation (a filtered call reuses the cached result
and recompiles nothing). Counts describe the FILTERED set, and every active filter is echoed as a
filter=<parameter>:<value> header before them - an empty body with a filter= header means the
filter matched nothing, not that the solution is clean. With summaryOnly=true the body is one
'<id>,<count>,<project>,<path>,<line:col>,<message>' line per id, ordered by count descending
then id; it covers every severity unless an include flag is set. A projectName or filePath that
is not in the solution is error=NotFound with candidate= values; one that exists but has no
diagnostics returns zero counts.
Multi-targeted projects report diagnostics across their loaded target frameworks. The Razor
compiler's own diagnostics (RZxxxx, e.g. an unclosed @code block) are reported against the
.razor/.cshtml file. Diagnostic ids that exist only to trigger a code fix (they carry no message
and accompany a public rule, as IDE0005's does) are not listed - fix the public id instead.
