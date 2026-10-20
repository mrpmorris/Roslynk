using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Tests.Features.MultiQuery;

namespace Morris.Roslynk.Tests.Features.Symbols;

/// <summary>
/// Issue #118 acceptance through the published tools: a uniquely-matching partially-qualified name
/// resolves, get_symbol/get_symbol_body echo the canonical resolvedSymbol, several suffix matches stay
/// Ambiguous with suffix-only candidates, and a fully-qualified request keeps its previous output.
/// </summary>
public class SuffixResolutionFeatureTests : IClassFixture<SuffixResolutionFeatureTests.Fixture>
{
	private readonly Fixture Subject;

	public SuffixResolutionFeatureTests(Fixture fixture)
	{
		Subject = fixture;
	}

	[Fact]
	public async Task WhenAPartiallyQualifiedNameUniquelyMatchesBySuffix_ThenGetSymbolEchoesResolvedSymbol()
	{
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "SomeUIHelper.Grid_CustomizeElement");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=Beta.SomeUIHelper.Grid_CustomizeElement(int)\n", result);
		Assert.Contains("public static int Grid_CustomizeElement(int value)", result);
	}

	[Fact]
	public async Task WhenAPartiallyQualifiedNameUniquelyMatchesBySuffix_ThenGetSymbolBodyEchoesResolvedSymbol()
	{
		string result = await Subject.Bodies.GetSymbolBody(Subject.SolutionPath, "SomeUIHelper.Grid_CustomizeElement");

		Assert.DoesNotContain("error=", result);
		Assert.StartsWith("resolvedSymbol=Beta.SomeUIHelper.Grid_CustomizeElement(int)\n", result);
		Assert.Contains("public static int Grid_CustomizeElement(int value) => value;", result);
	}

	[Fact]
	public async Task WhenSeveralSymbolsShareTheSuffix_ThenGetSymbolReturnsAmbiguousSuffixCandidates()
	{
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "UIHelper.Grid_CustomizeElement");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=Alpha.Alpha.UIHelper.Grid_CustomizeElement(int)\n", result);
		Assert.Contains("candidate=Alpha.UIHelper.Grid_CustomizeElement(int)\n", result);
		Assert.DoesNotContain("SomeUIHelper", result);
	}

	[Fact]
	public async Task WhenAFullyQualifiedNameIsGiven_ThenGetSymbolAddsNoResolvedSymbolHeader()
	{
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "Alpha.UIHelper.Grid_CustomizeElement");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("resolvedSymbol=", result);
	}

	[Fact]
	public async Task WhenAPartiallyQualifiedTypeNameUniquelyMatchesBySuffix_ThenGetMembersEchoesResolvedType()
	{
		string result = await Subject.Members.GetMembers(Subject.SolutionPath, "SomeUIHelper");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedType=Beta.SomeUIHelper", result);
	}

	[Fact]
	public async Task WhenMultiQueryUsesASuffixName_ThenItsSlotEchoesResolvedSymbol()
	{
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(Subject.Registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, Subject.Registry);

		string envelope = await subject.MultiQuery(
			Subject.SolutionPath,
			[new(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SomeUIHelper.Grid_CustomizeElement"))))]);

		Assert.Contains("slot=1 tool=get_symbol_body", envelope);
		Assert.Contains("resolvedSymbol=Beta.SomeUIHelper.Grid_CustomizeElement(int)", envelope);
		Assert.Contains("public static int Grid_CustomizeElement(int value) => value;", envelope);
	}

	public sealed class Fixture : IAsyncLifetime
	{
		public InstanceRegistry Registry { get; private set; } = null!;

		public string SolutionPath { get; private set; } = null!;

		public GetSymbolTool Symbols { get; private set; } = null!;

		public GetSymbolBodyTool Bodies { get; private set; } = null!;

		public GetMembersTool Members { get; private set; } = null!;

		private const string Probe = """
			namespace Alpha
			{
				public class UIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}

			namespace Alpha.Alpha
			{
				public class UIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}

			namespace Beta
			{
				public class SomeUIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}
			""";

		public async Task InitializeAsync()
		{
			Registry = new InstanceRegistry();
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			await File.WriteAllTextAsync(Path.Combine(library, "SuffixProbe.cs"), Probe);
			await Registry.GetOrAddAsync(SolutionPath);

			var resolver = new SymbolResolver();
			var projections = new ProjectionService();
			Symbols = new GetSymbolTool(Registry, resolver, projections);
			Bodies = new GetSymbolBodyTool(Registry, resolver, projections);
			Members = new GetMembersTool(Registry, resolver, projections);
		}

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}
