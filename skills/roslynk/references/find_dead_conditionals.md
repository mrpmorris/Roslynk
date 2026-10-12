# `find_dead_conditionals`

SupportsPaging=Y

Flags #if / #elif / #else branches whose region is never compiled in any known configuration - a
likely typo'd or stale symbol, or intentionally-disabled code. Each condition is evaluated against
the configurations the solution actually builds (each project's symbols, and the same set minus
DEBUG), across all target frameworks; a branch taken by none is reported, grouped by file. Also
use find_dead_code to find unreferenced symbols.
```
  #deadConditionals=<n>

  <relative/forward-slash/path.cs>
  \t<line:col>,<directive>,<condition>
```
where directive is if|elif|else and condition is the raw #if expression ('(else)' for #else).
A branch defined only in a configuration not loaded here (for example a target framework whose
workload is missing, or a CI-injected define) may be a false positive, so treat the result as
'possible' dead code.
