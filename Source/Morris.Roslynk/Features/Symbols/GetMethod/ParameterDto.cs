namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// One method parameter: its name and type, whether it is optional and its default, the ref kind
/// (<c>None</c>/<c>Ref</c>/<c>Out</c>/<c>In</c>), and whether it is a <c>params</c> parameter.
/// </summary>
public sealed record ParameterDto(string Name, string Type, bool IsOptional, string? DefaultValue, string RefKind, bool IsParams);
