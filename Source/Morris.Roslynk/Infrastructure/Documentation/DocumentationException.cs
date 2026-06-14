namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>A documented exception: the thrown type (from the <c>cref</c>) and the normalized description.</summary>
public sealed record DocumentationException(string Type, string Text);
