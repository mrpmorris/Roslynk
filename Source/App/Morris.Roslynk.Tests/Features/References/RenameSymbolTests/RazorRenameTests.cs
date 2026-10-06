using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Features.Symbols.GetExpressionInfo;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

/// <summary>
/// Renaming symbols declared in .razor files: the Renamer's edits land in the Razor-generated .g.cs
/// documents, are mapped back through the #line directives, and are written to the .razor sources.
/// </summary>
public class RazorRenameTests
{
	private static RenameSymbolTool CreateSubject(InstanceRegistry registry) =>
		new(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

	[Fact]
	public async Task WhenRenamingAFieldDeclaredInARazorCodeBlock_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total");

		Assert.Contains("applied=Y", result);
		Assert.Contains("resolvedSymbol=RazorLib.Counter.CurrentCount", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("private int Total;", counter);
		Assert.Contains("Count: @Total", counter);
		Assert.Contains("Total = StartAt;", counter);
		Assert.Contains("Total++;", counter);
		Assert.DoesNotContain("CurrentCount", counter);
	}

	[Fact]
	public async Task WhenRenamingAMethodWiredOnlyInMarkup_ThenTheMarkupAttributeIsRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.IncrementCount", "Bump");

		Assert.Contains("applied=Y", result);

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("@onclick=\"Bump\"", counter);
		Assert.Contains("private void Bump()", counter);
		Assert.DoesNotContain("IncrementCount", counter);
	}

	[Fact]
	public async Task WhenCheckOnlyIsPassed_ThenTheRazorFileIsListedAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("CurrentCount", counter);
		Assert.DoesNotContain("Total", counter);
	}

	[Fact]
	public async Task WhenRenamingAComponentParameter_ThenAttributeUsagesInOtherComponentsAreRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.StartAt", "StartFrom");

		Assert.Contains("applied=Y", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "UsesCounter.razor");

		string counter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Counter.razor"));
		Assert.Contains("public int StartFrom { get; set; }", counter);
		Assert.Contains("Starting from @StartFrom", counter);
		Assert.DoesNotContain("StartAt", counter);

		// The generator emits the attribute name as nameof(...) inside a #line-mapped region, so the
		// markup usage in the consuming component is renamed too.
		string usesCounter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "UsesCounter.razor"));
		Assert.Contains("<Counter StartFrom=\"5\" />", usesCounter);
	}

	[Fact]
	public async Task WhenARazorRenameIsApplied_ThenSubsequentReadsSeeTheNewName()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool rename = CreateSubject(registry);

		string renameResult = await rename.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total");
		Assert.Contains("applied=Y", renameResult);

		var findReferences = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());
		string referencesResult = await findReferences.FindReferences(solutionPath, "RazorLib.Counter.Total");

		Assert.Contains("resolvedSymbol=RazorLib.Counter.Total", referencesResult);
		Assert.Contains(referencesResult.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
	}

	private sealed record BindRename(string Directory, string Preview, string Applied);

	/// <summary>
	/// Runs <c>checkOnly</c> first (asserting it writes nothing), then the real rename (asserting it lists the
	/// same .razor/.cs files and no generated file), and finally asserts the rewritten solution has no errors.
	/// </summary>
	private static async Task<BindRename> RenameBoundMemberAsync(string solutionPath, string symbolName, string newName)
	{
		string directory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BindLib");
		Dictionary<string, string> before = await ReadSourcesAsync(directory);

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string preview = await subject.RenameSymbol(solutionPath, symbolName, newName, checkOnly: true);
		Assert.Contains("applied=N", preview);
		Assert.Equal(before, await ReadSourcesAsync(directory));

		string applied = await subject.RenameSymbol(solutionPath, symbolName, newName);
		Assert.Contains("applied=Y", applied);

		Assert.NotEmpty(ListedSources(applied));
		Assert.Equal(ListedSources(preview), ListedSources(applied));
		Assert.DoesNotContain(applied.Split('\n'), line => line.TrimStart('\t').EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));

		// Only the files the call listed may differ from disk before the call (no generated file is a source of truth).
		Dictionary<string, string> after = await ReadSourcesAsync(directory);
		Assert.Equal(
			ListedSources(applied),
			after.Where(pair => !before.TryGetValue(pair.Key, out string? old) || old != pair.Value)
				.Select(pair => Path.GetFileName(pair.Key))
				.Order(StringComparer.OrdinalIgnoreCase)
				.ToList());

		var diagnostics = new GetDiagnosticsTool(registry, new DiagnosticsService());
		Assert.Contains("errors=0", await diagnostics.GetDiagnostics(solutionPath, includeErrors: true));

		return new BindRename(directory, preview, applied);
	}

	private static List<string> ListedSources(string result) =>
		result.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <summary>Source files under the project, excluding bin/obj (where Razor-generated files live).</summary>
	private static async Task<Dictionary<string, string>> ReadSourcesAsync(string directory)
	{
		var sources = new Dictionary<string, string>();
		foreach (string path in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(directory, path);
			if (relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase))
				continue;
			sources[relative] = await File.ReadAllTextAsync(path);
		}

		return sources;
	}

	private static int Count(string text, string value) =>
		(text.Length - text.Replace(value, "").Length) / value.Length;

	[Fact]
	public async Task WhenRenamingAPlainClassMemberBoundWithBindValue_ThenRazorAndCsAreUpdated()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Name", "Label");

		string plain = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Plain.razor"));
		Assert.Contains("@bind-Value=\"Model.Label\"", plain);
		Assert.Contains("<p>@Model.Label</p>", plain);
		Assert.DoesNotContain("Model.Name", plain);

		string item = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Item.cs"));
		Assert.Contains("public string Label { get; set; }", item);
		Assert.Contains("item.Label", item);
		Assert.Contains("public static string Name =>", item);
	}

	[Fact]
	public async Task WhenRenamingTheNestedViewModelMemberFromIssue59_ThenTheBindDeclarationAndCsReferencesAreRewritten()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Dialog.ViewModel.InstallDate", "InstallOn");

		string dialog = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Dialog.razor"));
		Assert.Contains("public DateTime? InstallOn { get; set; }", dialog);
		Assert.Contains("@bind-Value=\"Model.InstallOn\"", dialog);
		Assert.Contains("<p>@Model.InstallOn</p>", dialog);
		Assert.Contains("@Languages.Machines.InstallDate", dialog);
		Assert.DoesNotContain("Model.InstallDate", dialog);

		string defaults = await File.ReadAllTextAsync(Path.Combine(result.Directory, "DialogDefaults.cs"));
		Assert.Contains("InstallOn = DateTime.Today", defaults);

		Assert.Contains("Dialog.razor", ListedSources(result.Applied));
		Assert.Contains("DialogDefaults.cs", ListedSources(result.Applied));
	}

	[Fact]
	public async Task WhenRenamingAMemberBoundWithGetAndSet_ThenBothRazorExpressionsAreRewritten()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Amount", "Total");

		string razor = await File.ReadAllTextAsync(Path.Combine(result.Directory, "GetSet.razor"));
		Assert.Contains("@bind-Value:get=\"Model.Total\"", razor);
		Assert.Contains("@bind-Value:set=\"OnSet\"", razor);
		Assert.Contains("Model.Total = value", razor);
		Assert.DoesNotContain("Model.Amount", razor);
		Assert.Equal(2, Count(razor, "Model.Total"));
	}

	[Fact]
	public async Task WhenRenamingAMemberBoundWithPlainBindAndEvent_ThenTheRazorBindIsRewritten()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Title", "Heading");

		string razor = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Native.razor"));
		Assert.Contains("@bind=\"Model.Heading\"", razor);
		Assert.Contains("@bind:event=\"oninput\"", razor);
		Assert.DoesNotContain("Model.Title", razor);
	}

	[Fact]
	public async Task WhenRenamingAMemberBoundWithAfter_ThenOnlyTheBoundExpressionIsRewritten()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Level", "Rank");

		string razor = await File.ReadAllTextAsync(Path.Combine(result.Directory, "After.razor"));
		Assert.Contains("@bind-Value=\"Model.Rank\"", razor);
		Assert.Contains("@bind-Value:after=\"Validate\"", razor);
		Assert.DoesNotContain("Model.Level", razor);
	}

	[Fact]
	public async Task WhenAMemberIsBoundTwiceAndAlsoUsedInPlainMarkup_ThenExactlyThreeRazorEditsAreMadeAndTheSameNamedResourceIsUntouched()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Code", "Ref");

		string razor = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Multi.razor"));
		Assert.Equal(3, Count(razor, "Model.Ref"));
		Assert.Equal(0, Count(razor, "Model.Code"));
		Assert.DoesNotContain("RefRef", razor);
		Assert.Contains("<InputDate @bind-Value=Model.Ref />", razor);
		Assert.Contains("<p>@Resources.Code</p>", razor);

		string item = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Item.cs"));
		Assert.Contains("public static string Code =>", item);
	}

	[Fact]
	public async Task WhenTheBoundExpressionRepeatsTheMemberTwice_ThenEveryOccurrenceIsRewrittenOnce()
	{
		BindRename result = await RenameBoundMemberAsync(TestSolutions.CreateScratchBindSolution(), "BindLib.Item.Tag", "Marker");

		string razor = await File.ReadAllTextAsync(Path.Combine(result.Directory, "Repeated.razor"));
		Assert.Contains("Pick(Model.Marker, Model.Marker)", razor);
		Assert.DoesNotContain("Model.Tag", razor);
	}

	[Fact]
	public async Task WhenARenameStartsFromTheRazorBindExpression_ThenItMatchesRenamingBySymbolName()
	{
		string solutionPath = TestSolutions.CreateScratchBindSolution();
		string directory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BindLib");
		string multi = await File.ReadAllTextAsync(Path.Combine(directory, "Multi.razor"));
		string[] lines = multi.Split('\n');
		int lineIndex = Array.FindIndex(lines, line => line.Contains("@bind-Value=\"Model.Code\""));
		int column = lines[lineIndex].IndexOf("Model.Code", StringComparison.Ordinal) + "Model.".Length + 1;

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var expressionInfo = new GetExpressionInfoTool(registry, new ProjectionService());

		string found = await expressionInfo.GetExpressionInfo(solutionPath, "BindLib/Multi.razor", lineIndex + 1, column);
		string fullName = found.Split('\n').Select(line => line.Trim()).Single(line => line.StartsWith("symbol=")).Substring("symbol=".Length);
		Assert.Equal("BindLib.Item.Code", fullName);

		string applied = await CreateSubject(registry).RenameSymbol(solutionPath, fullName, "Ref");
		Assert.Contains("applied=Y", applied);

		string razor = await File.ReadAllTextAsync(Path.Combine(directory, "Multi.razor"));
		Assert.Equal(3, Count(razor, "Model.Ref"));
		Assert.Contains("<p>@Resources.Code</p>", razor);
	}

	[Fact]
	public async Task WhenARenameContainsAnUnexplainableGeneratedEdit_ThenNothingIsWrittenIncludingCs()
	{
		string solutionPath = TestSolutions.CreateScratchBindSolution();
		string directory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BindLib");
		Dictionary<string, string> before = await ReadSourcesAsync(directory);

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		// Panel is a partial component: its .razor.cs declaration is an ordinary edit, but the class
		// declaration generated from Panel.razor has no razor origin, so the whole rename must be refused.
		string preview = await subject.RenameSymbol(solutionPath, "BindLib.Panel", "Frame", checkOnly: true);
		string applied = await subject.RenameSymbol(solutionPath, "BindLib.Panel", "Frame");

		Assert.Contains("error=NotSupported", preview);
		Assert.Contains("error=NotSupported", applied);
		Assert.Equal(before, await ReadSourcesAsync(directory));
	}
}
