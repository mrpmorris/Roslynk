# `rename_symbol`

Renames a symbol and all its references across the solution; correct across partial classes,
code-behind, every #if/#else branch and every target framework; string literals and comments are
left untouched.
Symbols declared or referenced in .razor/.cshtml files are renamed by rewriting those files directly
- edits computed against the Razor-generated code are mapped back through the compiler's #line
directives, covering @code blocks, markup expressions, and component-attribute usages in other
components' markup. If a Razor edit cannot be mapped and verified, the rename aborts before anything
is written (error=Conflict or error=NotSupported).
A symbol declared only by a source generator is refused (error=NotSupported naming the generator):
its declaration is never written to disk and would be regenerated unchanged, so change the generator
or the code that drives it instead; a partial member or type with a hand-written declaration is
renamable, since the generator follows the hand-written name.
Returns 'applied' (N for a checkOnly preview), 'resolvedSymbol' (the name as resolved, before the
rename), 'status' header, a blank line, then one solution-relative changed-file path per line.
Errors: an invalid identifier is refused (error=Invalid); an ambiguous name returns one candidate=
line per match, and a name that matches nothing returns fuzzy suggestions - in either case copy a
candidate back verbatim to target a single symbol. If a file the rename changes was edited (on disk
or in the loaded model) after the rename was computed, nothing is written and error=Stale names the
file; edits to other files made in the meantime are kept.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Rename a symbol").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
