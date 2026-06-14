namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// One method parameter: its name and type, whether it is optional and its default, the ref kind
/// (<c>None</c>/<c>Ref</c>/<c>Out</c>/<c>In</c>), and whether it is a <c>params</c> parameter.
/// </summary>
public sealed class ParameterDto
{
	public string Name { get; }
	public string Type { get; }
	public bool IsOptional { get; }
	public string? DefaultValue { get; }
	public string RefKind { get; }
	public bool IsParams { get; }

	public ParameterDto(string name, string type, bool isOptional, string? defaultValue, string refKind, bool isParams)
	{
		Name = name;
		Type = type;
		IsOptional = isOptional;
		DefaultValue = defaultValue;
		RefKind = refKind;
		IsParams = isParams;
	}
}
