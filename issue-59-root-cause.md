# Root cause — issue #59 (`rename_symbol` fails on members referenced from Razor `@bind-` expressions)

## Verified root cause

The hypothesis in the repro was right, with one refinement. The Razor compiler expands
`@bind-Value="Model.InstallDate"` into **four** generated references to `InstallDate`, but only **one** of
them carries a `#line` mapping back to the `.razor` file. Generated output (net8, SDK 10.0.400):

```csharp
global::__Blazor...TypeInference.CreateInputDate_0(__builder, 0, 1,
#line (4,25)-(4,42) "...\Dialog.razor"          // <-- MAPPED: the attribute value span
Model.InstallDate

#line default
#line hidden
            , 2, global::...EventCallback.Factory.Create(this,
                global::...RuntimeHelpers.CreateInferredEventCallback(this,
                    __value => Model.InstallDate = __value,   // <-- UNMAPPED (setter lambda write)
                    Model.InstallDate),                        // <-- UNMAPPED (setter lambda read)
            3, () => Model.InstallDate);                       // <-- UNMAPPED (ValueExpression lambda)
```

`rename_symbol` computes the rename against the generated `.g.cs` document, then
`RazorChangeMapper.MapChangesAsync` maps each text change back to the `.razor` source via
`GetMappedLineSpan`. The mapped attribute-value edit went through fine; the first edit landing in the
`#line hidden` scaffolding had `HasMappedPath == false` and was rejected as `Unmappable`, aborting the
whole rename (`.cs` edits included) with:

```
error=NotSupported
errorMessage=A change in '...\obj\RoslynkRazorGenerated\...\Foo_razor.g.cs' has no source mapping back to a .razor/.cshtml file; the edit was not applied.
```

`checkOnly=true` failed the same way because mapping happens before the preview/write split.

Same shape for every bind form (counts = mapped/unmapped occurrences of the bound expression):

| Form | Mapped | Unmapped scaffolding |
|---|---|---|
| `@bind-Value="Expr"` | 1 | 3 |
| `@bind-Value:get="Expr" @bind-Value:set="H"` | 2 (value, handler) | 2 |
| `<input @bind="Expr" />` (element) | 1 | 2 |
| `@bind-Value="Expr" @bind-Value:after="H"` | 3 | 3 (split around the handler's mapped span) |

## Fix

`RazorChangeMapper` collects the mapped regions (the `#line`-directed C# spans) in generated order and maps
all ordinary edits first. An unmapped edit is accepted only if one of the (at most 6) nearest preceding
regions already carries a mapped edit with the identical old and new text; it is then dropped, because it is a
copy of the user-written edit (so `ProjectionRenamer` sees one `.razor` edit per user-written span). Dropping
cannot lose a razor edit: every occurrence in razor source is itself mapped. Anything else (e.g. the generated
class declaration when renaming a component) throws `Unmappable`, aborting before any write.

An earlier iteration attributed the edit to the unique occurrence of its text inside the preceding region. That
failed for `Pick(Model.Tag, Model.Tag)` and for `:after` (the handler's mapped span sits between value and
`ValueExpression`), and could mis-attribute edits after a large `@code` region; it was replaced.

## Tests

`RazorRenameTests` against `TestFixtures/BindSolution`: plain-class `@bind-Value`, issue 59 nested ViewModel,
`:get`/`:set`, `@bind` + `@bind:event`, `:after`, member bound twice plus plain markup plus same-named
`Resources.Code`, repeated member inside one expression, rename from the razor position (via
`get_expression_info`), and atomicity (partial component rename -> `NotSupported`, no file changed incl. `.cs`).
Each bind test runs checkOnly first (no disk change), asserts the write lists the same files, that only the
listed files changed on disk, and that `get_diagnostics` reports `errors=0`. Eight of the new tests fail on the
old mapper; the atomicity test guards unchanged behaviour. Regression for `@code`/markup/`.cshtml` renames:
existing `RazorRenameTests` and `RazorSourceToolTests`.
