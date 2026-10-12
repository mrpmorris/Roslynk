# `get_symbol_body`

Returns the verbatim source text of a symbol's declaration - a method, constructor, property,
indexer, event, field, local function or type. Use this to see what a member actually does;
get_symbol gives only the signature, this the whole declaration including its body.
A symbol with one declaration returns 'project=', 'path=' and 'loc=' headers, a blank line, then
the source text exactly as it appears in the file - original indentation, line endings and
spacing, never reformatted; for a Razor member that text is the generated C#. Paging is by lines
of the returned text (its first comment line with includeLeadingTrivia), not by result items: a
page that is not the whole text adds truncated=Y, count=<total lines> and nextStartItem=<k>: call
again with startItem=<k>, keeping includeLeadingTrivia and part unchanged. A single line longer
than the page is delivered as far as it fits and named partialItem=<n>; the rest of that line is
not retrievable. A startItem past the end is error=Invalid naming count. A partial type or partial
method declared in several places returns a 'parts=<n>' header and one block per part: a
'part=<n>,project=...,path=...,loc=...,lines=<count>' line and the verbatim text, including an
implementation supplied by a source generator (e.g. [GeneratedRegex], [LoggerMessage],
[LibraryImport]). When the parts do not all fit within maxSize and the budget they are listed
without their text, their line ending ',omitted=Y', and the headers add truncated=Y and
omittedParts=<k>: fetch it with part=<n>, which returns that part alone, paged as above.
startItem pages one part; combining it with several parts and no part= is error=Invalid.
A name matching several distinct symbols (overloads included) returns error=Ambiguous with
candidate names; a symbol with no source declaration (a namespace, or a referenced-assembly symbol
- a metadata spelling such as 'Ns.Box`1' or 'N.Outer+Inner' of a type the solution declares in
source returns that source) returns error=NotSupported.
