# `get_expression_info`

Returns compiler facts at a 1-based source position: type, bound symbol/overload, nullability,
constant value and conversion. Use for var, overloads, implicit conversions, target-typed new and
nullable flow. Positions in .razor/.cshtml refer to the Razor source. A member name or called
method reports the whole access/invocation: its type is the return type, its symbol the overload.

## Result

Header-only:

- expr: expression text, cut at 200 characters with '...'; exprKind: Roslyn syntax kind.
- path, loc: expression location.
- type, convertedType: fully qualified types; '?' marks nullable annotations.
- nullability: annotation/flowState, e.g. NotAnnotated/NotNull or Annotated/MaybeNull;
  None/None when nullable analysis is disabled.
- conversion: e.g. 'implicit numeric', 'implicit user-defined <method>', 'identity'.
- constant: C# literal or 'null'.
- symbol, symbolKind: bound symbol. Type/member names work in name-based tools;
  locals, parameters and range variables use bare names.
- instantiation: present for constructed generics or reduced extension methods.
- origin: source|metadata; source has symbolPath/symbolLoc, metadata has assembly.
- doc: XML summary, when available.

Unavailable facts use '{Unavailable}': type for method groups/namespaces, constant for
non-constants, symbol for unbound expressions. When binding has candidates, candidateReason
(e.g. OverloadResolutionFailure) and pipe-delimited candidates are included.

## Declaration positions and errors

A declared name (local, parameter, field, property, method, type, foreach or pattern variable)
reports declaration=Y; expr is the name, exprKind the declaring syntax kind, type the declared
type (return type for methods), and nullability the declared annotation plus initializer flow
state (None without an initializer).

Whitespace, comments, keywords, punctuation or a file outside the solution return error=NotFound.
