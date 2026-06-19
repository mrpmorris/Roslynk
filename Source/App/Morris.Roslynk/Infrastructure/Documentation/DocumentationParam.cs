namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>A documented parameter: its name and the normalized (markdown-inline) description text.</summary>
public sealed class DocumentationParam
{
	public string Name { get; }
	public string Text { get; }

	public DocumentationParam(string name, string text)
	{
		Name = name;
		Text = text;
	}
}
