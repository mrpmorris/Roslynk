# `get_type_hierarchy`

SupportsPaging=Y

Returns a type's base-type chain, implemented interfaces and known derived types. The body has up
to three sections (base, interfaces, derived); an empty section is omitted, and each entry is a
'<typeKind>,<fully-qualified name>' where typeKind is class|struct|interface|enum|delegate:
```
  resolvedType=<fully-qualified type>

  base
  \t<typeKind>,<fully-qualified name>
  interfaces
  \t<typeKind>,<fully-qualified name>
  derived
  \t<typeKind>,<fully-qualified name>
```
The chain comes from the compiler, including base types defined in referenced assemblies.
