using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Features.MultiQuery;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>
/// The catalog guard: pins catalog, enum, tool constants, core signatures and the published schema to each
/// other, so drift between them fails here rather than in an agent transcript.
/// </summary>
public class MultiQueryCatalogTests
{
	[Fact]
	public void WhenTheCatalogIsEnumerated_ThenItHoldsExactlyTheTwelveQueryTools()
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
	public void WhenAnotherToolIsPublished_ThenItIsAbsentFromTheEnum()
	{
		// A newly added tool must never silently appear in the multi_query surface: the enum is exactly the
		// 11, and every other [McpServerTool] name in the assembly is unrepresentable.
		var assemblyToolNames = typeof(MultiQueryTool).Assembly
			.GetTypes()
			.SelectMany(type => type.GetMethods())
			.Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
			.OfType<McpServerToolAttribute>()
			.Select(attribute => attribute.Name)
			.Where(name => name is not null)
			.Select(name => name!)
			.ToHashSet(StringComparer.Ordinal);

		var enumNames = Enum.GetNames<MultiQueryOp>().ToHashSet(StringComparer.Ordinal);
		var forbiddenRequired = new[]
		{
			"apply_patch", "rename_symbol", "apply_code_action", "apply_code_fix",
			"change_signature", "remove_unused_usings", "get_diagnostics", "get_solution_status",
			"open_solution", "reload_solution", "get_code_actions", "multi_query",
		};

		foreach (string excluded in forbiddenRequired)
		{
			Assert.True(
				assemblyToolNames.Contains(excluded),
				$"'{excluded}' was expected to be a published tool but was not found.");
			Assert.False(
				enumNames.Contains(excluded),
				$"'{excluded}' must not be representable in MultiQueryOp.");
		}
	}
}
