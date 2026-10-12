# `find_definition`

Resolves the symbol used at a source position (file, 1-based line and column) and returns where it
is declared - the 'go to definition' jump, by position. Returns '#fullName' (parameter types
included for a method or indexer, so it can be passed straight to the name-based tools) and
'#kind', plus '#project', '#path' and '#loc' for a source symbol, or '#assembly=<name>' for a
metadata symbol. A position that binds to no symbol in any projection is error=NotFound.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Find a symbol's definition").
