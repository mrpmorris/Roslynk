# `find_definition`

Resolves the symbol used at a source position (file, 1-based line and column) and returns where it
is declared. Returns '#fullName' (parameter types
included for a method or indexer, so it can be passed straight to the name-based tools) and
'#kind', plus '#project', '#path' and '#loc' for a source symbol, or '#assembly=<name>' for a
metadata symbol. A position that binds to no symbol in any projection is error=NotFound.
