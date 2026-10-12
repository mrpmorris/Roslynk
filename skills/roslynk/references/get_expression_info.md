# `get_expression_info`

Returns what the compiler says about the expression at a source position (file, 1-based line and
column): its type, the symbol it binds to (with the overload chosen), nullability, constant value
and implicit conversion. Use it before editing code whose meaning is not obvious from the text
(var, overloads, implicit conversions, target-typed new, nullable flow). A position on a member
name or a called method reports the whole access or invocation, so an invocation's type is its
return type and its symbol the selected overload.
The result is header only: 'expr' (the expression text, cut at 200 characters with '...'),
'exprKind' (the Roslyn syntax kind), 'path' and 'loc' of the expression; 'type' and
'convertedType' (fully qualified, with '?' for a nullable annotation), 'nullability' as
<annotation>/<flowState> (e.g. NotAnnotated/NotNull, Annotated/MaybeNull; None/None when nullable
analysis is disabled); 'conversion' (e.g. 'implicit numeric', 'implicit user-defined <method>', or
'identity'); 'constant' (a C# literal, or 'null'); 'symbol' and 'symbolKind' for the bound symbol,
whose name can be sent straight to the name-based tools for a type or member (a local, parameter
or range variable reports its bare name); 'instantiation' when the symbol is a constructed generic
or reduced extension method; 'origin' (source|metadata) with 'symbolPath'/'symbolLoc' for source
or 'assembly' for metadata, and 'doc' (the XML summary, when available). A fact the compiler does
not supply is reported as '{Unavailable}' rather than guessed: 'type={Unavailable}' for a method
group or namespace, 'constant={Unavailable}' for a non-constant, 'symbol={Unavailable}' for an
expression that binds to no symbol, in which case 'candidateReason' (e.g.
OverloadResolutionFailure) and a pipe-delimited 'candidates' list appear when the compiler has
candidates. A position on the name a declaration introduces (a local, parameter, field, property,
method, type, foreach or pattern variable) reports the declared symbol instead, like an editor
hover: 'declaration=Y', 'expr' is the name, 'exprKind' the declaring syntax kind, 'type' the
declared type (a method's return type), and 'nullability' the declared annotation with the
initializer's flow state (None without one). A position on neither (whitespace, a comment, a
keyword, punctuation), or a file not in the solution, is error=NotFound. Works in .razor/.cshtml
files, with positions in the Razor source.
