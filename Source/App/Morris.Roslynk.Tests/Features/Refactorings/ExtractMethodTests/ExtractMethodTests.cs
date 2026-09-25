using Morris.Roslynk.Features.Refactorings.ExtractMethod;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.Refactorings.ExtractMethodTests;

public class ExtractMethodTests
{
	private const string Content =
		"namespace SimpleLibrary;\r\n\r\n" +
		"public class Greeter : IGreeter\r\n{\r\n" +
		"\tpublic string Greet(string name) => $\"Hello, {name}!\";\r\n\r\n" +
		"\tpublic int Sum(int a, int b)\r\n\t{\r\n" +
		"\t\tint total = a + b;\r\n" +
		"\t\ttotal *= 2;\r\n" +
		"\t\treturn total;\r\n" +
		"\t}\r\n\r\n" +
		"\tpublic async System.Threading.Tasks.Task<int> LaterAsync(int value)\r\n\t{\r\n" +
		"\t\tawait System.Threading.Tasks.Task.Delay(value);\r\n" +
		"\t\treturn value;\r\n" +
		"\t}\r\n}\r\n";

	[Fact]
	public async Task WhenExtractingStatementsWithAName_ThenTheMethodIsCreatedAndCalled()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int endLine) = Lines("int total = a + b;", "total *= 2;");

		string result = await subject.ExtractMethod(solutionPath, greeter, startLine, 1, endLine + 1, 1, methodName: "DoubleSum");

		Assert.Contains("applied=Y", result);
		Assert.Contains("method=DoubleSum", result);
		Assert.Contains("kind=Method", result);
		Assert.Contains("signature=private static int DoubleSum(int a, int b)", result);
		Assert.Contains("call=int total = DoubleSum(a, b);", result);
		Assert.Contains("Greeter.cs", result);

		string written = await File.ReadAllTextAsync(greeter);
		Assert.Contains("DoubleSum(a, b)", written);
		Assert.Contains("private static int DoubleSum(int a, int b)", written);
		Assert.DoesNotContain("NewMethod", written);
		Assert.Matches(@"int total = DoubleSum\(a, b\);\r?\n\s*return total;", written);
		Assert.Contains("\tpublic string Greet(string name) => $\"Hello, {name}!\";\r\n", written);

		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Assert.Contains("DoubleSum", (await instance.CurrentSolution.Projects.SelectMany(project => project.Documents)
			.First(document => document.FilePath == greeter).GetTextAsync()).ToString());
	}

	[Fact]
	public async Task WhenCheckOnly_ThenThePreviewIsReturnedAndNothingIsWritten()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int endLine) = Lines("int total = a + b;", "total *= 2;");

		string result = await subject.ExtractMethod(solutionPath, greeter, startLine, 1, endLine + 1, 1, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("method=NewMethod", result);
		Assert.Contains("Greeter.cs", result);
		Assert.Equal(Content, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenExtractingAnExpression_ThenTheExpressionIsReplacedByTheCall()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int line, int column) = Position("a + b");

		string result = await subject.ExtractMethod(solutionPath, greeter, line, column, line, column + "a + b".Length, methodName: "Add");

		Assert.Contains("applied=Y", result);
		Assert.Contains("call=int total = Add(a, b);", result);
	}

	[Fact]
	public async Task WhenAskedForALocalFunction_ThenALocalFunctionIsCreated()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int endLine) = Lines("int total = a + b;", "total *= 2;");

		string result = await subject.ExtractMethod(solutionPath, greeter, startLine, 1, endLine + 1, 1, methodName: "Local", asLocalFunction: true);

		Assert.Contains("applied=Y", result);
		Assert.Contains("kind=LocalFunction", result);
		Assert.Contains("signature=static int Local(int a, int b)", result);
	}

	[Fact]
	public async Task WhenTheSelectionAwaits_ThenTheExtractedMethodIsAsync()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int line, _) = Position("await System.Threading.Tasks.Task.Delay(value);");

		string result = await subject.ExtractMethod(solutionPath, greeter, line, 1, line + 1, 1, methodName: "WaitAsync");

		Assert.Contains("applied=Y", result);
		Assert.Contains("async", result.Split('\n').First(header => header.StartsWith("signature=", StringComparison.Ordinal)));
		Assert.Contains("await WaitAsync(value);", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheSelectionCannotBeExtracted_ThenNotSupportedIsReturnedAndNothingIsWritten()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int endLine) = Lines("return total;", "await System.Threading.Tasks.Task.Delay(value);");

		// The selection spans the end of one method and the start of another.
		string result = await subject.ExtractMethod(solutionPath, greeter, startLine, 1, endLine + 1, 1);

		Assert.Contains("error=NotSupported", result);
		Assert.Equal(Content, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheNameWouldIntroduceACompileError_ThenNothingIsWritten()
	{
		(string solutionPath, string greeter) = CreateScenario();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int endLine) = Lines("int total = a + b;", "total *= 2;");

		// A member cannot share its enclosing type's name (CS0542).
		string result = await subject.ExtractMethod(solutionPath, greeter, startLine, 1, endLine + 1, 1, methodName: "Greeter");

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("CS0542", result);
		Assert.Equal(Content, await File.ReadAllTextAsync(greeter));
	}

	[Theory]
	[InlineData("class")]
	[InlineData("1abc")]
	[InlineData("two words")]
	public async Task WhenTheMethodNameIsInvalid_ThenInvalidIsReturned(string methodName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());

		string result = await subject.ExtractMethod(TestSolutions.Simple, "SimpleLibrary/Greeter.cs", 1, 1, 1, 2, methodName: methodName);

		Assert.Contains("error=Invalid", result);
	}

	[Fact]
	public async Task WhenTheSelectionIsOutsideTheDocument_ThenInvalidIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());

		string result = await subject.ExtractMethod(TestSolutions.Simple, "SimpleLibrary/Greeter.cs", 1, 1, 9999, 1);

		Assert.Contains("error=Invalid", result);
	}

	[Fact]
	public async Task WhenTheDocumentIsNotInTheSolution_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());

		string result = await subject.ExtractMethod(TestSolutions.Simple, "Nope.cs", 1, 1, 1, 2);

		Assert.Contains("error=NotFound", result);
	}

	private static (string SolutionPath, string Greeter) CreateScenario()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string greeter = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();
		File.WriteAllText(greeter, Content);
		return (solutionPath, greeter);
	}

	private static (int StartLine, int EndLine) Lines(string first, string last) =>
		(Position(first).Line, Position(last).Line);

	private static (int Line, int Column) Position(string fragment)
	{
		int offset = Content.IndexOf(fragment, StringComparison.Ordinal);
		string before = Content[..offset];
		int line = before.Count(character => character == '\n') + 1;
		int column = offset - (before.LastIndexOf('\n') + 1) + 1;
		return (line, column);
	}
}
