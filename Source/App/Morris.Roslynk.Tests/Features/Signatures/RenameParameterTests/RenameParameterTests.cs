using Morris.Roslynk.Features.Signatures.RenameParameter;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.Signatures.RenameParameterTests;

public class RenameParameterTests
{
	private const string ExtraSource =
		"""
		namespace SimpleLibrary;

		public interface IShape
		{
			int Area(int size);
		}

		public class Square : IShape
		{
			public int Area(int side) => side * side;
		}

		public class Box
		{
			public Box(int width)
			{
				Width = width;
			}

			public int Width { get; }
		}

		public class NamedCaller
		{
			public int Run() => new Calculator().Add(a: 1, b: 2) + new Box(width: 3).Width;
		}
		""";

	[Fact]
	public async Task WhenRenamingAParameterOfOneOverload_ThenItsDeclarationBodyAndParamRefChangeButTheOtherOverloadDoesNot()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add(int)", "amount", "value");

			Assert.Contains("applied=Y", result);
			Assert.Contains("resolvedMethod=SimpleLibrary.Ledger.Add(int)", result);
			Assert.Contains("parameter=amount", result);
			Assert.Contains("renamedMembers=1", result);
			Assert.Contains("Ledger.cs", result);
			string text = await File.ReadAllTextAsync(FindFile(solutionPath, "Ledger.cs"));
			Assert.Contains("<paramref name=\"value\"/>", text);
			Assert.Contains("public int Add(int value)", text);
			Assert.Contains("Total += value;", text);
			Assert.Contains("public int Add(int amount, int times)", text);
			Assert.Contains("Add(amount);", text);
		}
	}

	[Fact]
	public async Task WhenRenamingAnInterfaceMemberParameter_ThenTheImplementationIsRenamedToo()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.IGreeter.Greet", "name", "recipient");

			Assert.Contains("applied=Y", result);
			Assert.Contains("renamedMembers=2", result);
			Assert.DoesNotContain("unchangedRelated", result);
			string contract = await File.ReadAllTextAsync(FindFile(solutionPath, "IGreeter.cs"));
			Assert.Contains("<paramref name=\"recipient\"/>", contract);
			Assert.Contains("<param name=\"recipient\">", contract);
			Assert.Contains("string Greet(string recipient);", contract);
			string implementation = await File.ReadAllTextAsync(FindFile(solutionPath, "Greeter.cs"));
			Assert.Contains("Greet(string recipient) => $\"Hello, {recipient}!\"", implementation);
		}
	}

	[Fact]
	public async Task WhenRenamingAnImplementationParameter_ThenTheInterfaceMemberIsRenamedToo()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Greeter.Greet", "name", "recipient");

			Assert.Contains("renamedMembers=2", result);
			Assert.Contains("string Greet(string recipient);", await File.ReadAllTextAsync(FindFile(solutionPath, "IGreeter.cs")));
		}
	}

	[Fact]
	public async Task WhenARelatedDeclarationUsesADifferentName_ThenItIsLeftAloneAndReported()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.IShape.Area", "size", "length");

			Assert.Contains("applied=Y", result);
			Assert.Contains("renamedMembers=1", result);
			Assert.Contains("unchangedRelated=SimpleLibrary.Square.Area", result);
			string text = await File.ReadAllTextAsync(FindFile(solutionPath, "Extra.cs"));
			Assert.Contains("int Area(int length);", text);
			Assert.Contains("public int Area(int side) => side * side;", text);
		}
	}

	[Fact]
	public async Task WhenAParameterIsPassedAsANamedArgument_ThenTheCallSiteIsUpdated()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Calculator.Add", "a", "left");

			Assert.Contains("applied=Y", result);
			Assert.Contains("return left + b;", await File.ReadAllTextAsync(FindFile(solutionPath, "Calculator.cs")));
			Assert.Contains("Add(left: 1, b: 2)", await File.ReadAllTextAsync(FindFile(solutionPath, "Extra.cs")));
		}
	}

	[Fact]
	public async Task WhenRenamingAConstructorParameter_ThenTheConstructorAndNamedArgumentAreUpdated()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Box.Box(int)", "width", "size");

			Assert.Contains("applied=Y", result);
			string text = await File.ReadAllTextAsync(FindFile(solutionPath, "Extra.cs"));
			Assert.Contains("public Box(int size)", text);
			Assert.Contains("Width = size;", text);
			Assert.Contains("new Box(size: 3)", text);
		}
	}

	[Fact]
	public async Task WhenCheckOnly_ThenTheChangedFilesAreListedAndNothingIsWritten()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string greeter = FindFile(solutionPath, "Greeter.cs");
			string before = await File.ReadAllTextAsync(greeter);

			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.IGreeter.Greet", "name", "recipient", checkOnly: true);

			Assert.Contains("applied=N", result);
			Assert.Contains("IGreeter.cs", result);
			Assert.Contains("Greeter.cs", result);
			Assert.Equal(before, await File.ReadAllTextAsync(greeter));
		}
	}

	[Fact]
	public async Task WhenTheNewNameIsAnotherParameter_ThenItIsAConflictAndNothingIsWritten()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string ledger = FindFile(solutionPath, "Ledger.cs");
			string before = await File.ReadAllTextAsync(ledger);

			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add(int, int)", "amount", "times");

			Assert.Contains("error=Conflict", result);
			Assert.Contains("a parameter named 'times'", result);
			Assert.Equal(before, await File.ReadAllTextAsync(ledger));
		}
	}

	[Fact]
	public async Task WhenTheNewNameIsALocal_ThenItIsAConflict()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add(int, int)", "times", "index");

			Assert.Contains("error=Conflict", result);
			Assert.Contains("a local variable named 'index'", result);
		}
	}

	[Fact]
	public async Task WhenTheParameterDoesNotExist_ThenItIsNotFoundListingTheParameters()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add(int, int)", "count", "total");

			Assert.Contains("error=NotFound", result);
			Assert.Contains("amount, times", result);
		}
	}

	[Fact]
	public async Task WhenTheMethodNameIsAmbiguous_ThenEachOverloadIsACandidate()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add", "amount", "value");

			Assert.Contains("error=Ambiguous", result);
			Assert.Contains("SimpleLibrary.Ledger.Add(int)", result);
			Assert.Contains("SimpleLibrary.Ledger.Add(int, int)", result);
		}
	}

	[Fact]
	public async Task WhenTheSymbolIsNotAMethod_ThenItIsNotSupported()
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Total", "value", "amount");

			Assert.Contains("error=NotSupported", result);
		}
	}

	[Theory]
	[InlineData("1bad")]
	[InlineData("amount")]
	public async Task WhenTheNewNameIsInvalidOrUnchanged_ThenItIsInvalid(string newName)
	{
		(string solutionPath, RenameParameterTool subject, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await subject.RenameParameter(solutionPath, "SimpleLibrary.Ledger.Add(int)", "amount", newName);

			Assert.Contains("error=Invalid", result);
		}
	}

	private static async Task<(string SolutionPath, RenameParameterTool Subject, InstanceRegistry Registry)> CreateAsync()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(FindFile(solutionPath, "Ledger.cs"))!, "Extra.cs"), ExtraSource);
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());
		return (solutionPath, subject, registry);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.GetFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories)
			.Single(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
