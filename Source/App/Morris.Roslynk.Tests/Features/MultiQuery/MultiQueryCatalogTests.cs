using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>
/// The catalog guard: pins catalog, enum, tool constants, core signatures and the published schema to each
/// other, so drift between them fails here rather than in an agent transcript.
/// </summary>
public class MultiQueryCatalogTests
{
	[Fact]
	public void WhenTheCatalogIsEnumerated_ThenItHoldsExactlyTheFifteenQueryTools()
	{
		string[] expected =
		[
			"get_symbol",
			"get_symbol_body",
			"get_members",
			"find_definition",
			"find_implementations",
			"find_references",
			"get_callers",
			"get_callees",
			"search_symbols",
			"get_type_hierarchy",
			"find_dead_code",
			"find_dead_conditionals",
			"get_expression_info",
			"find_reads",
			"find_writes",
		];

		Assert.Equal(
			expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
			MultiQueryCatalog.Entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());
	}

	[Fact]
	public void WhenTheEnumIsEnumerated_ThenItsMembersMatchTheCatalogExactly()
	{
		string[] enumNames = Enum.GetNames<MultiQueryOp>().OrderBy(name => name, StringComparer.Ordinal).ToArray();
		string[] catalogNames = MultiQueryCatalog.Entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

		Assert.Equal(catalogNames, enumNames);
	}

	[Fact]
	public void WhenAToolGainsAMultiQueryableCore_ThenTheCoreResolvesWithThePinnedParameters()
	{
		foreach ((string toolName, MultiQueryCatalog.OpEntry entry) in MultiQueryCatalog.Entries)
		{
			MethodInfo core = entry.Resolve();

			// The seam: every core takes the pinned model and its instance, and never acquires either.
			ParameterInfo[] parameters = core.GetParameters();
			Assert.True(
				parameters.FirstOrDefault(p => p.ParameterType == typeof(Morris.Roslynk.Infrastructure.Lifecycle.SolutionModel)) is not null,
				$"{toolName}: core '{core.Name}' does not declare a SolutionModel parameter.");
			Assert.True(
				parameters.FirstOrDefault(p => p.ParameterType == typeof(Morris.Roslynk.Infrastructure.Lifecycle.RoslynInstance)) is not null,
				$"{toolName}: core '{core.Name}' does not declare a RoslynInstance parameter.");
			Assert.True(
				parameters.Last().ParameterType == typeof(CancellationToken),
				$"{toolName}: core '{core.Name}' does not end with a CancellationToken.");
		}
	}

	[Fact]
	public void WhenAnEnumMemberIsRenamedOrAToolConstantMoves_ThenTheCatalogAndTheConstantsStillAgree()
	{
		// The enum member names must equal each tool's published name constant, so a rename that misses one
		// side fails here.
		foreach ((string toolName, MultiQueryCatalog.OpEntry entry) in MultiQueryCatalog.Entries)
		{
			object? constant = entry.ToolType
				.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
				.FirstOrDefault(field => field.IsLiteral && field.FieldType == typeof(string) && (string?)field.GetRawConstantValue() == toolName)
				?.GetRawConstantValue();

			Assert.True(
				constant is string,
				$"'{toolName}' is in the catalog but no public const string equals it on {entry.ToolType.Name}.");
		}
	}

	[Fact]
	public void WhenACoreIsInTheCatalog_ThenItsBindableParametersMatchThePublicToolMethod()
	{
		// AGENTS.md's rule that public/core bindable parameter names and defaults agree, pinned for every
		// catalog entry: the public [McpServerTool] method's caller-facing parameters (solutionId and the
		// cancellation token aside) must match the core's (the binder-supplied types aside) by name, type
		// and default, so a batched call binds exactly what a single call binds.
		foreach ((string toolName, MultiQueryCatalog.OpEntry entry) in MultiQueryCatalog.Entries)
		{
			MethodInfo core = entry.Resolve();
			MethodInfo publicMethod = entry.ToolType
				.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
				.Single(method => method.GetCustomAttribute<McpServerToolAttribute>() is { } attribute && attribute.Name == toolName);

			var publicParameters = publicMethod.GetParameters()
				.Where(parameter => parameter.Name != "solutionId" && parameter.ParameterType != typeof(CancellationToken))
				.ToDictionary(parameter => parameter.Name!, StringComparer.Ordinal);
			var coreParameters = core.GetParameters()
				.Where(parameter => !MultiQueryCatalog.IsSupplied(parameter.ParameterType))
				.ToDictionary(parameter => parameter.Name!, StringComparer.Ordinal);

			Assert.True(
				publicParameters.Keys.Order(StringComparer.Ordinal).SequenceEqual(coreParameters.Keys.Order(StringComparer.Ordinal)),
				$"{toolName}: public parameters [{string.Join(", ", publicParameters.Keys)}] != core parameters [{string.Join(", ", coreParameters.Keys)}].");

			foreach ((string name, ParameterInfo publicParameter) in publicParameters)
			{
				ParameterInfo coreParameter = coreParameters[name];
				Assert.True(publicParameter.ParameterType == coreParameter.ParameterType, $"{toolName}.{name}: parameter type differs between the public method and the core.");
				Assert.True(publicParameter.HasDefaultValue == coreParameter.HasDefaultValue, $"{toolName}.{name}: optionality differs between the public method and the core.");
				if (publicParameter.HasDefaultValue)
					Assert.Equal(publicParameter.DefaultValue, coreParameter.DefaultValue);
			}
		}
	}

	[Fact]
	public async Task WhenAResponseBudgetIsPassedAsAnArgument_ThenItIsRejectedAsUnknown()
	{
		// The slot allowance is binder-supplied: sending it as an argument is an ordinary unknown-key error.
		var entry = MultiQueryCatalog.Entries[GetSymbolBodyTool.GetSymbolBodyName];
		var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["budget"] = MultiQueryTestHelpers.Json(10_000) };

		var exception = await Assert.ThrowsAsync<ArgumentException>(() => MultiQueryCatalog.InvokeCoreAsync(
			CreateBinderProvider(),
			entry,
			model: null!,
			instance: null!,
			allowance: null,
			arguments,
			CancellationToken.None));

		Assert.Contains("'budget' is not a parameter of 'get_symbol_body'", exception.Message);
	}

	[Fact]
	public async Task WhenMaxLinesIsPassedAsAString_ThenTheBinderCoercesIt()
	{
		var entry = MultiQueryCatalog.Entries[GetSymbolBodyTool.GetSymbolBodyName];
		var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["symbolName"] = MultiQueryTestHelpers.Json("Repro.Big"),
			["maxLines"] = MultiQueryTestHelpers.Json("3"),
		};

		// The coercion succeeds: the core resolves and runs (failing on the null model afterwards), which
		// proves "3" reached the int parameter as 3 rather than being rejected at binding.
		await Assert.ThrowsAsync<NullReferenceException>(() => MultiQueryCatalog.InvokeCoreAsync(
			CreateBinderProvider(),
			entry,
			model: null!,
			instance: null!,
			allowance: null,
			arguments,
			CancellationToken.None));
	}

	private static IServiceProvider CreateBinderProvider()
	{
		var registry = new InstanceRegistry();
		return new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
	}
}
