using Microsoft.CodeAnalysis;

namespace GeneratorLib;

[Generator]
public sealed class HelloGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context) =>
		context.RegisterPostInitializationOutput(static postInit =>
			postInit.AddSource(
				"Hello.g.cs",
				"namespace GeneratedNamespace { public static class Hello { public const string Greeting = \"Hello from the generator\"; } }"));
}
