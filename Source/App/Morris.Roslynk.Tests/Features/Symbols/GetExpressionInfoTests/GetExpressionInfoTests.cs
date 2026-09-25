using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Features.Symbols.GetExpressionInfo;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetExpressionInfoTests;

public class GetExpressionInfoTests
{
	private static string SamplesPath => Path.Combine(Path.GetDirectoryName(TestSolutions.Expressions)!, "ExpressionLib", "Samples.cs");

	[Fact]
	public async Task WhenPositionedOnAnOverloadedCall_ThenTheSelectedOverloadAndReturnTypeAreReported()
	{
		string intCall = await QueryAsync("Format(42)");
		Assert.DoesNotContain("error=", intCall);
		Assert.Contains("expr=Formatter.Format(42)\n", intCall);
		Assert.Contains("exprKind=InvocationExpression\n", intCall);
		Assert.Contains("type=string\n", intCall);
		Assert.Contains("symbol=ExpressionLib.Formatter.Format(int)\n", intCall);
		Assert.Contains("symbolKind=method\n", intCall);
		Assert.Contains("origin=source\n", intCall);
		Assert.Contains("symbolPath=ExpressionLib/Samples.cs\n", intCall);
		Assert.Contains("doc=Formats an Int32 value.\n", intCall);
		Assert.Contains("constant=none\n", intCall);

		string stringCall = await QueryAsync("Format(\"x\")");
		Assert.Contains("symbol=ExpressionLib.Formatter.Format(string)\n", stringCall);
	}

	[Fact]
	public async Task WhenPositionedOnVar_ThenTheInferredTypeIsReported()
	{
		string result = await QueryAsync("var text");

		Assert.Contains("exprKind=IdentifierName\n", result);
		// 'var' is always inferred nullable-annotated for a reference type.
		Assert.Contains("type=string?\n", result);
		Assert.Contains("symbol=System.String\n", result);
		Assert.Contains("origin=metadata\n", result);
	}

	[Fact]
	public async Task WhenPositionedOnAConstantWidenedToLong_ThenTheConstantAndImplicitConversionAreReported()
	{
		string result = await QueryAsync("Width;", offset: 0);

		Assert.Contains("expr=Formatter.Width\n", result);
		Assert.Contains("symbol=ExpressionLib.Formatter.Width\n", result);
		Assert.Contains("symbolKind=field\n", result);
		Assert.Contains("type=int\n", result);
		Assert.Contains("convertedType=long\n", result);
		Assert.Contains("constant=40\n", result);
		Assert.Matches("conversion=implicit[^\n]* numeric", result);
	}

	[Fact]
	public async Task WhenPositionedOnAUserDefinedConversionSource_ThenTheOperatorIsReported()
	{
		string result = await QueryAsync("Meters(3)");

		Assert.Contains("exprKind=ObjectCreationExpression\n", result);
		Assert.Contains("type=ExpressionLib.Meters\n", result);
		Assert.Contains("convertedType=double\n", result);
		Assert.Contains("symbol=ExpressionLib.Meters.Meters(double)\n", result);
		Assert.Contains("conversion=implicit user-defined ExpressionLib.Meters.implicit operator double(ExpressionLib.Meters)", result);
	}

	[Fact]
	public async Task WhenPositionedOnAGenericOrExtensionCall_ThenTheDefinitionAndItsInstantiationAreReported()
	{
		string generic = await QueryAsync("Echo(text)");
		Assert.Contains("symbol=ExpressionLib.Formatter.Echo<T>(T)\n", generic);
		Assert.Contains("instantiation=", generic);
		Assert.Contains("type=string\n", generic);

		string extension = await QueryAsync("Twice()");
		Assert.Contains("symbol=ExpressionLib.Formatter.Twice(int)\n", extension);
		Assert.Contains("instantiation=", extension);
		Assert.Contains("type=int\n", extension);
	}

	[Fact]
	public async Task WhenPositionedOnANullableProperty_ThenTheFlowStateReflectsTheNullCheck()
	{
		string checkedAccess = await QueryAsync("MaybeName.Length");
		Assert.Contains("expr=MaybeName\n", checkedAccess);
		Assert.Contains("symbol=ExpressionLib.Samples.MaybeName\n", checkedAccess);
		Assert.Contains("/NotNull\n", checkedAccess);

		string unchecked_ = await QueryAsync("MaybeName;");
		Assert.Contains("/MaybeNull\n", unchecked_);
	}

	[Fact]
	public async Task WhenTheCallCannotBeResolved_ThenNoSymbolIsGuessedAndCandidatesAreListed()
	{
		string result = await QueryAsync("Format(1.5)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("symbol=none\n", result);
		Assert.Contains("candidateReason=OverloadResolutionFailure\n", result);
		Assert.Contains("ExpressionLib.Formatter.Format(int)", result);
		Assert.Contains("ExpressionLib.Formatter.Format(string)", result);
	}

	[Fact]
	public async Task WhenPositionedOnAVarLocalsName_ThenTheDeclaredLocalAndItsInferredTypeAreReported()
	{
		string result = await QueryAsync("text =");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("expr=text\n", result);
		Assert.Contains("exprKind=VariableDeclarator\n", result);
		Assert.Contains("declaration=Y\n", result);
		Assert.Contains("type=string?\n", result);
		Assert.Contains("nullability=Annotated/NotNull\n", result);
		Assert.Contains("symbol=text\n", result);
		Assert.Contains("symbolKind=local\n", result);
		Assert.Contains("origin=source\n", result);
	}

	[Fact]
	public async Task WhenPositionedOnAConstantFieldsOrMethodsName_ThenTheDeclaredMemberIsReported()
	{
		string field = await QueryAsync("Width =");
		Assert.Contains("declaration=Y\n", field);
		Assert.Contains("type=int\n", field);
		Assert.Contains("constant=40\n", field);
		Assert.Contains("symbol=ExpressionLib.Formatter.Width\n", field);

		string method = await QueryAsync("Run()");
		Assert.Contains("declaration=Y\n", method);
		Assert.Contains("exprKind=MethodDeclaration\n", method);
		Assert.Contains("type=void\n", method);
		Assert.Contains("symbol=ExpressionLib.Samples.Run()\n", method);
		Assert.Contains("symbolKind=method\n", method);
	}

	[Fact]
	public async Task WhenPositionedOnAKeyword_ThenNotFoundIsReported()
	{
		string result = await QueryAsync("public void Run");

		Assert.Contains("error=NotFound", result);
		Assert.Contains("keywords", result);
	}

	[Fact]
	public async Task WhenPositionedInARazorCodeBlock_ThenTheExpressionIsReportedAtItsRazorLocation()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Razor);
		var subject = new GetExpressionInfoTool(registry, new ProjectionService());
		string counterPath = Path.Combine(Path.GetDirectoryName(TestSolutions.Razor)!, "RazorLib", "Counter.razor");

		// Line 15 is 'CurrentCount = StartAt;'; column 18 is inside 'StartAt'.
		string result = await subject.GetExpressionInfo(TestSolutions.Razor, counterPath, 15, 18);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("expr=StartAt\n", result);
		Assert.Contains("type=int\n", result);
		Assert.Contains(".Counter.StartAt\n", result);
		Assert.Contains("path=RazorLib/Counter.razor\n", result);
		Assert.Contains("loc=15:", result);
	}

	[Fact]
	public async Task WhenTheFileIsNotInTheSolution_ThenNotFoundIsReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Expressions);
		var subject = new GetExpressionInfoTool(registry, new ProjectionService());

		string result = await subject.GetExpressionInfo(TestSolutions.Expressions, "Missing.cs", 1, 1);

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetExpressionInfoTool(registry, new ProjectionService());

		string result = await subject.GetExpressionInfo(TestSolutions.Expressions, SamplesPath, 1, 1);

		Assert.Contains("error=Indexing", result);

		await registry.GetOrAddAsync(TestSolutions.Expressions);
	}

	[Fact]
	public async Task WhenRunThroughMultiQuery_ThenEachSlotReportsItsExpression()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Expressions);
		var provider = new ServiceCollection()
			.AddSingleton(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		(int intLine, int intColumn) = await PositionOfAsync("Format(42)", 0);
		(int stringLine, int stringColumn) = await PositionOfAsync("Format(\"x\")", 0);
		var operations = new List<MultiQueryOperation>
		{
			new(MultiQueryOp.get_expression_info, Args(SamplesPath, intLine, intColumn)),
			new(MultiQueryOp.get_expression_info, Args(SamplesPath, stringLine, stringColumn)),
		};

		string envelope = await subject.MultiQuery(TestSolutions.Expressions, operations);

		Assert.Contains("slot=1 tool=get_expression_info", envelope);
		Assert.Contains("symbol=ExpressionLib.Formatter.Format(int)", envelope);
		Assert.Contains("symbol=ExpressionLib.Formatter.Format(string)", envelope);
	}

	private static IReadOnlyDictionary<string, JsonElement> Args(string filePath, int line, int column) =>
		new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["filePath"] = JsonSerializer.SerializeToElement(filePath),
			["line"] = JsonSerializer.SerializeToElement(line),
			["column"] = JsonSerializer.SerializeToElement(column),
		};

	private static async Task<string> QueryAsync(string anchor, int offset = 0)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Expressions);
		var subject = new GetExpressionInfoTool(registry, new ProjectionService());

		(int line, int column) = await PositionOfAsync(anchor, offset);
		return await subject.GetExpressionInfo(TestSolutions.Expressions, SamplesPath, line, column);
	}

	/// <summary>1-based line and column of <paramref name="offset"/> characters into the only occurrence of <paramref name="anchor"/>.</summary>
	private static async Task<(int Line, int Column)> PositionOfAsync(string anchor, int offset)
	{
		string text = await File.ReadAllTextAsync(SamplesPath);
		int index = text.IndexOf(anchor, StringComparison.Ordinal);
		Assert.True(index >= 0 && text.IndexOf(anchor, index + 1, StringComparison.Ordinal) < 0, $"Anchor '{anchor}' must occur exactly once.");
		index += offset;

		int line = 1 + text[..index].Count(c => c == '\n');
		int column = index - (text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1) + 1;
		return (line, column);
	}
}
