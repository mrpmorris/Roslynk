# `extract_method`

Extracts contiguous C# statements or one expression into a method/local function and replaces
the selection with a call. Uses Roslyn's Extract Method and data-flow analysis for parameters,
ref/out, return value, static/async and call-site code. Ignores surrounding selection whitespace.

## Result

Headers, then a blank line and solution-relative changed paths:

- applied; method: final name.
- symbolName: extracted member's full name, usable in name-based tools,
  e.g. N.T.M(int).NewMethod(string) for a local function.
- kind: Method|LocalFunction; signature: new declaration header.
- call: statement containing the call; status follows the shared protocol.

## Validation and errors

- NotSupported: Roslyn cannot extract, warns of possible behavior changes, or extraction adds
  compile errors (listed). Also generated .g.cs targets, including Razor output.
- Conflict: methodName would bind a call to a different member, or Razor text mismatches.
- Stale: a target file changed since computation.
- NotFound: documentPath is not a solution-compiled .cs/.razor/.cshtml file.
- Invalid: empty/out-of-range selection or invalid methodName.

Razor selections outside C# (@code or expressions) return NotSupported. Shared mapping rules apply;
inserting a method outside @code may be unmappable (NotSupported). Validation occurs before saving.
