namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>A documented parameter: its name and the normalized (markdown-inline) description text.</summary>
public sealed record DocumentationParam(string Name, string Text);
