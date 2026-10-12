# `get_diagnostics`

SupportsPaging=Y

Returns compiler and analyzer diagnostics. Use summaryOnly=true for an overview; request details
when individual locations are needed. Analyzers run by default; includeAnalyzers=false is a
faster compiler-only pass. Scheduling/caching guidance is in SKILL.md.

## Result

Severity counts are always included. Details nest by project, folder, file and severity:
```
  errors=<n>
  warnings=<n>
  infos=<n>
  hidden=<n>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<errors|warnings|infos|hidden>
  \t\t\t\t<id>,<line:col>,<message>
```
message is the trailing free-text field.

summaryOnly=true returns one <id>,<count>,<project>,<path>,<line:col>,<message> line per id,
ordered by descending count then id. Includes all severities unless an include flag is set.

## Filters and coverage

Filters combine after compilation and reuse cached results. Counts describe the filtered set;
active filters appear as filter=<parameter>:<value>. An empty filtered result does not establish
that the whole solution is clean.

Unknown projectName/filePath returns NotFound with candidates. Known projects/files without
diagnostics return zero counts. Multi-targeted projects cover loaded frameworks.
RZxxxx diagnostics point to Razor source. Private fixer-trigger IDs accompanying public rules
are omitted; fix the public ID instead (e.g. IDE0005).
