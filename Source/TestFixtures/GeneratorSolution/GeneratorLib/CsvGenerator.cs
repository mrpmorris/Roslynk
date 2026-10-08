using Microsoft.CodeAnalysis;

namespace GeneratorLib;

/// <summary>Turns each .csv additional file into a class holding its first line, so a test can see the generator rerun.</summary>
[Generator]
public sealed class CsvGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<(string Name, string FirstLine)> files = context.AdditionalTextsProvider
			.Where(static file => file.Path.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase))
			.Select(static (file, cancellationToken) =>
			{
				string text = file.GetText(cancellationToken)?.ToString() ?? "";
				string firstLine = text.Split('\n')[0].TrimEnd('\r');
				return (System.IO.Path.GetFileNameWithoutExtension(file.Path), firstLine);
			});

		context.RegisterSourceOutput(files, static (output, file) =>
			output.AddSource(
				$"{file.Name}.csv.g.cs",
				$"namespace GeneratedNamespace {{ public static class {file.Name}Csv {{ public const string FirstLine = \"{file.FirstLine.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"; }} }}"));
	}
}
