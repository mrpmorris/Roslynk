# `get_symbol_body`

SupportsPaging=Y

Returns the verbatim source text of a symbol's declaration - a method, constructor, property,
indexer, event, field, local function or type. Use this to see what a member actually does;
get_symbol gives only the signature, this the whole declaration including its body.
A symbol with one declaration returns 'project=', 'path=' and 'loc=' headers, a blank line, then
the source text exactly as it appears in the file - original indentation, line endings and
spacing, never reformatted; for a Razor member that text is the generated C#. A partial type or partial method declared in several places returns a 'parts=<n>' header and one block per part: a
'part=<n>,project=...,path=...,loc=...,lines=<count>' line and the verbatim text, including an
implementation supplied by a source generator (e.g. [GeneratedRegex], [LoggerMessage],
[LibraryImport]).
A name matching several distinct symbols (overloads included) returns error=Ambiguous with
candidate names; a symbol with no source declaration (a namespace, or a referenced-assembly symbol
- a metadata spelling such as 'Ns.Box`1' or 'N.Outer+Inner' of a type the solution declares in
source returns that source) returns error=NotSupported.

startItem counts lines of returned source text, not result items; count is the total lines,
starting with leading trivia when included. Keep includeLeadingTrivia and part unchanged between
pages. A startItem past the end is error=Invalid naming count. startItem pages one part; using
it with multiple parts without part is error=Invalid. Parts that do not fit are listed without
text with omitted=Y; headers include truncated=Y and omittedParts=<k>. Fetch each omitted part
with part=<n> and page it normally.
