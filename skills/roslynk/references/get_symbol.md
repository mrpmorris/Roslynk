# `get_symbol`

Returns a symbol's declaration without its implementation, resolved by fully-qualified name; its
metadata signature when no source is available. A single source match returns '#project=',
'#path=', '#loc=' headers, a blank line, then the verbatim declaration cut before its body (the
opening brace or '=>'); the declaration line conveys accessibility, kind, return type, name and
parameters, so those are not repeated. A metadata symbol instead returns '#source=metadata',
'#kind', '#signature', '#assembly'.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Get symbol details").
