using Microsoft.CodeAnalysis;

namespace GeneratorLib;

/// <summary>Emits a partial type with generated-only members (and a local function inside one) plus a namespace nothing hand-written declares, so a search that matches no hand-written name still finds them.</summary>
[Generator]
public sealed class ShapeSourceGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context) =>
		context.RegisterPostInitializationOutput(static postInit =>
			postInit.AddSource(
				"Shape.g.cs",
				"""
				namespace ConsumerLib
				{
					public partial class Shape
					{
						public int ReadArea() => 1;

						public string Describe()
						{
							return Format(ReadArea());

							static string Format(int area) => area.ToString();
						}

						public sealed class Cache
						{
							public bool TryResolve(string key) => key.Length > 0;
						}
					}
				}

				namespace GeneratedOnly
				{
					public static class OnlyGenerated
					{
						public const int Answer = 42;
					}
				}
				"""));
}
