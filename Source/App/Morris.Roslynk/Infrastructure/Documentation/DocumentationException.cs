namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>A documented exception: the thrown type (from the <c>cref</c>) and the normalized description.</summary>
public sealed class DocumentationException
{
	public string Type { get; }
	public string Text { get; }

	public DocumentationException(string type, string text)
	{
		Type = type;
		Text = text;
	}
}
