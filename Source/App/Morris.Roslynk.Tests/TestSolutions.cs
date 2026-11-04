using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Locates the checked-in fixture solutions under <c>TestFixtures/</c> and restores them on demand, so
/// the loading tests work from a fresh clone where <c>obj/</c> is not committed.
/// </summary>
internal static class TestSolutions
{
	private static readonly Lazy<string> SimpleSolution = new(() => Prepare("SimpleSolution", "SimpleSolution.slnx"));
	private static readonly Lazy<string> BrokenSolution = new(() => Prepare("BrokenSolution", "BrokenSolution.slnx"));
	private static readonly Lazy<string> RazorSolutionPath = new(() => Prepare("RazorSolution", "RazorSolution.slnx"));
	private static readonly Lazy<string> RazorInjectSolutionPath = new(() => Prepare("RazorInjectSolution", "RazorInjectSolution.slnx"));
	private static readonly Lazy<string> BindSolutionPath = new(() => Prepare("BindSolution", "BindSolution.slnx"));
	private static readonly Lazy<string> ReferencesSolutionPath = new(() => Prepare("ReferencesSolution", "ReferencesSolution.slnx"));
	private static readonly Lazy<string> CodeStyleSolutionPath = new(() => Prepare("CodeStyleSolution", "CodeStyleSolution.slnx"));
	private static readonly Lazy<string> ConditionalSolutionPath = new(() => Prepare("ConditionalSolution", "ConditionalSolution.slnx"));
	private static readonly Lazy<string> LocalFunctionSolutionPath = new(() => Prepare("LocalFunctionSolution", "LocalFunctionSolution.slnx"));
	private static readonly Lazy<string> MultiTargetSolutionPath = new(() => Prepare("MultiTargetSolution", "src", "MultiTarget.slnx"));
	private static readonly Lazy<string> ExpressionSolutionPath = new(() => Prepare("ExpressionSolution", "ExpressionSolution.slnx"));
	private static readonly Lazy<string> AccessSolutionPath = new(() => Prepare("AccessSolution", "AccessSolution.slnx"));
	private static readonly Lazy<string> DisposeSolutionPath = new(() => Prepare("DisposeSolution", "DisposeSolution.slnx"));
	private static readonly Lazy<string> RazorMultiProjectSolutionPath = new(() => Prepare("RazorMultiProjectSolution", "RazorMultiProjectSolution.slnx"));
	private static readonly Lazy<string> GeneratorSolutionPath = new(() =>
	{
		string path = Prepare("GeneratorSolution", "GeneratorSolution.slnx");

		// The consumer references the generator project as an analyzer only; the workspace's design-time
		// build never compiles it, so the generator DLL must be built before the solution loads.
		Build(Path.Combine(Path.GetDirectoryName(path)!, "GeneratorLib", "GeneratorLib.csproj"));
		return path;
	});
	private static readonly Lazy<string> LargeTypeSolutionPath = new(() =>
	{
		string path = Prepare("LargeTypeSolution", "LargeTypeSolution.slnx");

		// Big.cs and LongLine.cs are deterministic and huge, so they are generated into the prepared
		// fixture (gitignored) rather than committed. Written before the first load, temp-then-move, so
		// two test assemblies preparing concurrently never expose a partial file.
		WriteLargeTypeGeneratedSources(Path.Combine(Path.GetDirectoryName(path)!, "LargeTypeLib"));
		return path;
	});

	// Fixtures whose projects carry a PackageReference/FrameworkReference or a netstandard target:
	// MSBuildWorkspace's design-time build resolves those from obj/project.assets.json, so the fixture
	// must be restored. A package-free single-TFM fixture loads cleanly without any restore.
	private static readonly HashSet<string> FixturesNeedingRestore = new(StringComparer.OrdinalIgnoreCase)
	{
		"RazorSolution",
		"RazorInjectSolution",
		"BindSolution",
		"RazorMultiProjectSolution",
		"CodeStyleSolution",
		"GeneratorSolution",
		"MultiTargetSolution",
	};

	private static readonly ConcurrentDictionary<string, Lazy<bool>> SourceRestores = new(StringComparer.OrdinalIgnoreCase);

	// One registry for the whole test process. Committed fixtures are never written to, so every test
	// that only reads one shares this registry's loaded snapshot (and Roslyn's compilation and
	// SymbolFinder caches) instead of paying an MSBuild load, a first compilation and a declaration
	// index of its own. Never dispose it: disposing would tear down instances other tests are using.
	private static readonly InstanceRegistry SharedRegistry = new();

	/// <summary>
	/// The process-wide registry, with <paramref name="solutionPath"/> loaded and ready. Use this from
	/// a test that only reads a committed fixture; do not dispose the returned registry.
	/// </summary>
	public static async Task<InstanceRegistry> SharedAsync(string solutionPath)
	{
		await SharedRegistry.GetOrAddAsync(solutionPath);
		return SharedRegistry;
	}

	/// <summary>
	/// The process-wide registry's instance for <paramref name="solutionPath"/>, loaded and ready. For a
	/// test that reads the snapshot directly (<see cref="RoslynInstance.CurrentSolution"/>) rather than
	/// through a tool; do not dispose the instance.
	/// </summary>
	public static async Task<RoslynInstance> SharedInstanceAsync(string solutionPath) =>
		await SharedRegistry.GetOrAddAsync(solutionPath);

	/// <summary>A clean single-project solution.</summary>
	public static string Simple => SimpleSolution.Value;

	/// <summary>
	/// The same clean solution copied to a scratch directory, for tests that add source files. It carries
	/// every semicolon-terminated type shape (positional records, a record struct, a bodiless class and
	/// interface) that the committed fixture cannot, because the fixture is compiled with warnings as
	/// errors and an unreferenced bodiless type is reported as unused.
	/// <para>
	/// Prepared once per process and shared: every caller only reads it. Do not write to the returned
	/// directory — tests that need to change files must call <see cref="CreateScratchSimpleSolution"/>.
	/// </para>
	/// </summary>
	public static string CreateScratchSimpleSolutionWithBodilessTypes() => BodilessTypesSolution.Value;

	private static readonly Lazy<string> BodilessTypesSolution = new(() =>
	{
		string path = CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(path)!, "SimpleLibrary");
		File.WriteAllText(Path.Combine(library, "BodilessTypes.cs"), BodilessTypesSource);
		Restore(path);
		return path;
	});

	/// <summary>
	/// A positional record whose properties are declared by its parameter list, beside an ordinary
	/// property of the same name. Read-only tests share one loaded instance of it instead of loading a
	/// copy each; see <see cref="SharedInstanceAsync"/>. Prepared once per process; do not write to the
	/// returned directory.
	/// </summary>
	public static string CreateScratchSimpleSolutionWithPositionalRecord() => PositionalRecordSolution.Value;

	private static readonly Lazy<string> PositionalRecordSolution = new(() =>
	{
		string path = CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(path)!, "SimpleLibrary");
		File.WriteAllText(Path.Combine(library, "PositionalRecord.cs"), PositionalRecordSource);
		Restore(path);
		return path;
	});

	public const string PositionalRecordSource = """
		namespace Acme.Web.Pages.Orders;

		public partial class OrderPage
		{
			private sealed record Batch(List<string> Items, Dictionary<string, int> Targets);

			private sealed class Holder
			{
				private int Targets { get; set; }
			}
		}
		""";

	private const string BodilessTypesSource = """
		namespace SimpleLibrary;

		public sealed record Batch(List<string> Items, Dictionary<string, int> Targets);

		public record struct Point(int X, int Y);

		public readonly record struct Size(double Width, double Height);

		public class BodilessClass;

		public interface IBodiless;

		internal static class BodilessTypes
		{
			public static readonly Type[] All = [typeof(Batch), typeof(Point), typeof(Size), typeof(BodilessClass), typeof(IBodiless)];

			public static string Describe() => string.Join(", ", All.Select(type => type.Name));
		}
		""";

	/// <summary>A single-project solution containing a deliberate CS0029 compile error.</summary>
	public static string Broken => BrokenSolution.Value;

	/// <summary>A Razor Class Library with a component whose handler is wired only in markup.</summary>
	public static string Razor => RazorSolutionPath.Value;

	/// <summary>
	/// A Razor Class Library whose component has an <c>@inject</c> property (mapped only as far as its
	/// type and name tokens) and markup lines carrying expressions, beside an <c>@code</c> block.
	/// </summary>
	public static string RazorInject => RazorInjectSolutionPath.Value;

	/// <summary>
	/// A Razor Class Library whose Dialog.razor binds a nested-ViewModel property with
	/// <c>@bind-Value="Model.InstallDate"</c> (the issue 59 shape), also using the property in plain
	/// markup, next to a same-named unrelated symbol (Languages.Machines.InstallDate).
	/// </summary>
	public static string Bind => BindSolutionPath.Value;

	/// <summary>A two-file solution with an interface referenced many ways, for testing reference grouping.</summary>
	public static string References => ReferencesSolutionPath.Value;

	/// <summary>
	/// A single-project solution with <c>EnforceCodeStyleInBuild</c> on - so the IDE analyzers are
	/// referenced - containing an unnecessary using with a comment above it.
	/// </summary>
	public static string CodeStyle => CodeStyleSolutionPath.Value;

	/// <summary>A single-project solution whose method is called in both the #if DEBUG and #else branches.</summary>
	public static string Conditional => ConditionalSolutionPath.Value;

	/// <summary>
	/// A multi-targeted (net8.0, netstandard2.0) library whose <c>Api.Accepts(Type)</c> parameter type binds in
	/// net8.0 only.
	/// </summary>
	public static string MultiTarget => MultiTargetSolutionPath.Value;

	/// <summary>A consumer using a type emitted by a project-referenced source generator (built on first use).</summary>
	public static string Generator => GeneratorSolutionPath.Value;

	/// <summary>
	/// A single-project solution for the output-budget work: <see cref="BigDeclaration"/> (3,000 one-line
	/// methods, ~164KB) in a gitignored Big.cs generated at prepare time, a 20,000-character single line in
	/// LongLine.cs, and a System.Text.Json partial context whose generated part is a real source-generated
	/// declaration beside a tiny hand-written one.
	/// </summary>
	public static string LargeType => LargeTypeSolutionPath.Value;

	/// <summary>
	/// The exact text get_symbol_body returns for the fixture's Repro.Big declaration: the whole class,
	/// 163,912 characters over 3,003 lines (the file adds the namespace line, a blank line and the closing
	/// newline). Tests reassemble it byte-exactly when walking pages.
	/// </summary>
	/// <summary>
	/// The exact text get_symbol_body returns for the fixture's Repro.Big declaration: the whole class,
	/// 163,912 characters over 3,003 lines (the file adds the namespace line, a blank line and the closing
	/// newline). Tests reassemble it byte-exactly when walking pages.
	/// </summary>
	public static string BigDeclaration
	{
		get
		{
			var builder = new StringBuilder("public class Big\r\n{\r\n");
			for (int index = 0; index < MethodCount; index++)
				builder.Append(BigMethodLine(index));
			builder.Append('}');
			return builder.ToString();
		}
	}

	private const int MethodCount = 3_000;

	private static string BigMethodLine(int index) =>
		$"    public int Method{index:D4}(int value) => value + {index};\r\n";

	private static void WriteLargeTypeGeneratedSources(string projectDirectory)
	{
		var big = new StringBuilder("namespace Repro;\r\n\r\npublic class Big\r\n{\r\n");
		for (int index = 0; index < MethodCount; index++)
			big.Append(BigMethodLine(index));
		big.Append("}\r\n");
		WriteGeneratedSource(Path.Combine(projectDirectory, "Big.cs"), big.ToString());

		var longLine = new StringBuilder("namespace Repro;\r\n\r\npublic class LongLine\r\n{\r\n    public const string Value = \"");
		longLine.Append('a', 20_000);
		longLine.Append("\";\r\n}\r\n");
		WriteGeneratedSource(Path.Combine(projectDirectory, "LongLine.cs"), longLine.ToString());
	}

	/// <summary>Temp-then-move so two test assemblies preparing the fixture concurrently never expose a partial file.</summary>
	private static void WriteGeneratedSource(string path, string content)
	{
		string temp = path + ".roslynk.tmp";
		File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		File.Move(temp, path, overwrite: true);
	}

	private static string Prepare(params string[] relativeParts)
	{
		string root = FindTestFixturesRoot();
		string path = Path.Combine([root, .. relativeParts]);
		if (FixturesNeedingRestore.Contains(relativeParts[0]))
			EnsureSourceRestored(relativeParts[0], Path.Combine(root, relativeParts[0]), Path.Combine(relativeParts[1..]));

		return path;
	}

	private static string FindTestFixturesRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "TestFixtures");
			if (Directory.Exists(candidate))
				return candidate;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the TestFixtures folder above the test assembly.");
	}

	// One project at a time: a parallel restore of a solution whose projects reference each other can write the same
	// project's restore files twice and fail with "already exists", which happens under load.
	private static void Restore(string solutionPath) => RunDotnet("restore --disable-parallel", solutionPath);

	/// <summary>
	/// Restores a fixture once per process. Every scratch copy of it then clones that restore output
	/// instead of spawning a restore of its own, which is what a copy paid for before.
	/// </summary>
	private static void EnsureSourceRestored(string fixtureName, string sourceDir, string solutionFile) =>
		_ = SourceRestores.GetOrAdd(
			fixtureName,
			name => new Lazy<bool>(
				() =>
				{
					Restore(Path.Combine(sourceDir, solutionFile));
					return true;
				},
				LazyThreadSafetyMode.ExecutionAndPublication))
			.Value;

	/// <summary>
	/// Copies the files a restore writes to the top of each <c>obj</c> folder into a scratch copy,
	/// rewriting the fixture's absolute path to the copy's, so the copy has an assets file without
	/// running a restore. A copy that is built by a test still re-restores: the copied
	/// <c>project.nuget.cache</c> carries the old paths, so NuGet decides it is stale.
	/// </summary>
	private static void CloneRestoreOutputs(string sourceDir, string destDir)
	{
		foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(sourceDir, file);
			string? parent = Path.GetDirectoryName(relative);
			if (parent is null || !string.Equals(Path.GetFileName(parent), "obj", StringComparison.OrdinalIgnoreCase))
				continue;

			if (!IsRestoreOutput(Path.GetFileName(file)))
				continue;

			string text = File.ReadAllText(file);
			if (text.Contains(sourceDir, StringComparison.OrdinalIgnoreCase))
				text = text.Replace(sourceDir, destDir, StringComparison.OrdinalIgnoreCase);

			string target = Path.Combine(destDir, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
	}

	private static bool IsRestoreOutput(string name) =>
		name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
		|| name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
		|| name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
		|| name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase);

	private static void Build(string projectPath) => RunDotnet("build", projectPath);

	private static void RunDotnet(string verb, string path)
	{
		var startInfo = new ProcessStartInfo("dotnet", $"{verb} \"{path}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Failed to start 'dotnet {verb}'.");
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"'dotnet {verb}' on '{path}' failed:\n{process.StandardOutput.ReadToEnd()}\n{process.StandardError.ReadToEnd()}");
	}

	/// <summary>
	/// Copies the SimpleSolution fixture (source only) to a fresh temp directory and restores it, so
	/// write tests can modify files without dirtying the committed fixture.
	/// </summary>
	public static string CreateScratchSimpleSolution() => CreateScratch("SimpleSolution", "SimpleSolution.slnx");

	/// <summary>A writable scratch copy of the CodeStyleSolution fixture, for tests that apply analyzer fixes.</summary>
	public static string CreateScratchCodeStyleSolution() => CreateScratch("CodeStyleSolution", "CodeStyleSolution.slnx");

	/// <summary>A writable scratch copy of the ConditionalSolution fixture, for tests that rename/edit it.</summary>
	public static string CreateScratchConditionalSolution() => CreateScratch("ConditionalSolution", "ConditionalSolution.slnx");

	/// <summary>A writable scratch copy of the RazorSolution fixture, for tests that rename/edit its .razor files.</summary>
	public static string CreateScratchRazorSolution() => CreateScratch("RazorSolution", "RazorSolution.slnx");

	/// <summary>A writable scratch copy of the BindSolution fixture, for tests that rename through @bind expansions.</summary>
	public static string CreateScratchBindSolution() => CreateScratch("BindSolution", "BindSolution.slnx");

	/// <summary>
	/// An MVC view (Views/Home/Index.cshtml) with a used and an unused @using, a CS0219 unused local in its
	/// @functions block, and a _ViewImports.cshtml whose unused @using is shared and must be left alone.
	/// </summary>
	public static string Cshtml => CshtmlSolutionPath.Value;

	/// <summary>
	/// A scratch RazorSolution copy with an unused @using and an unused local added to Counter.razor,
	/// prepared once and shared by the tests that only read it.
	/// </summary>
	public static string RazorWithUnusedCode => RazorWithUnusedCodePath.Value;

	private static readonly Lazy<string> CshtmlSolutionPath = new(() => Prepare("CshtmlSolution", "CshtmlSolution.slnx"));

	private static readonly Lazy<string> RazorWithUnusedCodePath = new(() =>
	{
		string solutionPath = CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");
		string counter = File.ReadAllText(counterPath);
		string newline = counter.Contains("\r\n") ? "\r\n" : "\n";
		counter = counter
			.Replace("@using Microsoft.AspNetCore.Components.Web" + newline, "@using Microsoft.AspNetCore.Components.Web" + newline + "@using System.CodeDom" + newline)
			.Replace("\t\tCurrentCount++;", "\t\tint unused = 1;" + newline + "\t\tCurrentCount++;");
		File.WriteAllText(counterPath, counter);
		return solutionPath;
	});

	/// <summary>A writable scratch copy of the CshtmlSolution fixture: an MVC view (Views/Home/Index.cshtml) with a used
	/// and an unused @using, a CS0219 unused local in its @functions block, and a _ViewImports.cshtml whose
	/// unused @using is shared and must be left alone.
	/// </summary>
	public static string CreateScratchCshtmlSolution() => CreateScratch("CshtmlSolution", "CshtmlSolution.slnx");

	/// <summary>
	/// A nested class (ParentClass.Widget) whose Method1 declares a local function holding another local function,
	/// and whose overloaded Method2 declares a same-named local function in each overload.
	/// </summary>
	public static string LocalFunctions => LocalFunctionSolutionPath.Value;

	/// <summary>
	/// Expressions exercising get_expression_info: overloads, a constant, implicit numeric and user-defined
	/// conversions, a generic call, a reduced extension method, nullable flow, and an unresolvable overload call.
	/// </summary>
	public static string Expressions => ExpressionSolutionPath.Value;

	/// <summary>A single-project solution touching fields, properties and parameters in every read/write form.</summary>
	public static string Access => AccessSolutionPath.Value;

	/// <summary>
	/// A solution whose disposable types are used in every disposal shape, plus a non-IDisposable type with
	/// a Dispose method, so a plain local declaration can be told apart from a real disposal.
	/// </summary>
	public static string Dispose => DisposeSolutionPath.Value;

	/// <summary>
	/// Two Razor projects, the dependent one listed first: App's Page.razor uses Lib's Widget component, whose
	/// <c>Count</c> parameter and a <c>StateHasChanged</c> call live in the code-behind Widget.razor.cs. Lib is
	/// multi-targeted (net8.0;net10.0) and has an _Imports.razor.
	/// </summary>
	public static string RazorMultiProject => RazorMultiProjectSolutionPath.Value;

	/// <summary>A writable scratch copy of the RazorMultiProjectSolution fixture.</summary>
	public static string CreateScratchRazorMultiProjectSolution() => CreateScratch("RazorMultiProjectSolution", "RazorMultiProjectSolution.slnx");

	/// <summary>A writable scratch copy of the MultiTargetSolution fixture (solution under <c>src/</c>), for tests that edit it.</summary>
	public static string CreateScratchMultiTargetSolution() => CreateScratch("MultiTargetSolution", Path.Combine("src", "MultiTarget.slnx"));

	/// <summary>A writable scratch copy of the LocalFunctionSolution fixture, for tests that rename or edit it.</summary>
	public static string CreateScratchLocalFunctionSolution() => CreateScratch("LocalFunctionSolution", "LocalFunctionSolution.slnx");

	/// <summary>
	/// A scratch copy of the GeneratorSolution fixture whose generator DLL has deliberately NOT been
	/// built, for tests asserting how an unloadable analyzer reference is reported.
	/// </summary>
	public static string CreateScratchGeneratorSolutionWithoutBuiltGenerator() => CreateScratch("GeneratorSolution", "GeneratorSolution.slnx");

	/// <summary>
	/// A scratch GeneratorSolution copy with its generator built, prepared once and shared by the tests
	/// that only assert a write tool refuses a generated-only symbol.
	/// </summary>
	public static string GeneratorWithBuiltGenerator => GeneratorWithBuiltGeneratorPath.Value;

	private static readonly Lazy<string> GeneratorWithBuiltGeneratorPath = new(CreateScratchGeneratorSolution);

	/// <summary>A writable scratch copy of the GeneratorSolution fixture with its generator DLL built, for tests that edit generator inputs.</summary>
	public static string CreateScratchGeneratorSolution()
	{
		string path = CreateScratch("GeneratorSolution", "GeneratorSolution.slnx");
		_ = SourceGeneratorBuilt.Value;
		CopyDirectory(
			Path.Combine(FindTestFixturesRoot(), "GeneratorSolution", "GeneratorLib", "bin"),
			Path.Combine(Path.GetDirectoryName(path)!, "GeneratorLib", "bin"));
		return path;
	}

	/// <summary>
	/// Builds the fixture's generator once per process. A copy that needs a built generator gets the
	/// output copied into it rather than paying for a build of its own.
	/// </summary>
	private static readonly Lazy<bool> SourceGeneratorBuilt = new(() =>
	{
		Build(Path.Combine(FindTestFixturesRoot(), "GeneratorSolution", "GeneratorLib", "GeneratorLib.csproj"));
		return true;
	});

	private static void CopyDirectory(string sourceDir, string destDir)
	{
		foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
		{
			string target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target, overwrite: true);
		}
	}

	/// <summary>Builds (Debug) the generator project of a GeneratorSolution scratch copy, as a user would after load.</summary>
	public static void BuildScratchGenerator(string solutionPath) =>
		Build(Path.Combine(Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "GeneratorLib.csproj"));

	/// <summary>A scratch copy of a fixture, restored and not built, at <paramref name="solutionFile"/> within it.</summary>
	public static string CreateScratchFixture(string fixtureName, string solutionFile) => CreateScratch(fixtureName, solutionFile);

	/// <summary>
	/// A two-project solution written to a fresh temp directory and restored, not built: Lib targets net8.0 and
	/// net10.0, with RootNamespace Linked.Library, and App (net10.0) references it and compiles Shared/Shared.cs,
	/// outside its folder, linked as Common/Shared.cs. With
	/// <paramref name="withNoTargetsProject"/>, a third project uses the Microsoft.Build.NoTargets SDK, which compiles
	/// nothing.
	/// </summary>
	public static string CreateScratchLinkedSolution(bool withNoTargetsProject = false)
	{
		string root = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), "LinkedSolution");
		ScratchDirectories.Track(root);
		void Write(string relative, string text)
		{
			string path = Path.Combine(root, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
		}

		Write("Lib/Lib.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
			    <Nullable>enable</Nullable>
			    <RootNamespace>Linked.Library</RootNamespace>
			  </PropertyGroup>
			</Project>
			""");
		Write("Lib/Greeter.cs", "namespace Linked.Library;\n\npublic static class Greeter\n{\n\tpublic static string Greet(string name) => $\"Hello, {name}\";\n}\n");
		Write("App/App.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <OutputType>Exe</OutputType>
			    <ImplicitUsings>enable</ImplicitUsings>
			  </PropertyGroup>
			  <ItemGroup>
			    <ProjectReference Include="../Lib/Lib.csproj" />
			    <Compile Include="../Shared/Shared.cs" Link="Common/Shared.cs" />
			  </ItemGroup>
			</Project>
			""");
		Write("Shared/Shared.cs", "namespace Linked;\n\ninternal static class Shared;\n");
		Write("App/Program.cs", "Console.WriteLine(Linked.Library.Greeter.Greet(\"world\"));\n");
		Write("LinkedSolution.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");

		if (withNoTargetsProject)
		{
			Write("Tools/Tools.csproj", """
				<Project Sdk="Microsoft.Build.NoTargets/3.7.0">
				  <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
				</Project>
				""");
			Write("LinkedSolution.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n  <Project Path=\"Tools/Tools.csproj\" />\n</Solution>\n");
		}

		string solutionPath = Path.Combine(root, "LinkedSolution.slnx");
		Restore(solutionPath);
		return solutionPath;
	}

	private static string CreateScratch(string fixtureName, string solutionFile)
	{
		string sourceDir = Path.Combine(FindTestFixturesRoot(), fixtureName);
		string destDir = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), fixtureName);
		ScratchDirectories.Track(destDir);
		CopyExcludingBuildOutput(sourceDir, destDir);

		string solutionPath = Path.Combine(destDir, solutionFile);

		// One restore per fixture, then a clone of its restore output for every copy, rather than a
		// restore process per copy.
		EnsureSourceRestored(fixtureName, sourceDir, solutionFile);
		CloneRestoreOutputs(sourceDir, destDir);

		return solutionPath;
	}

	private static void CopyExcludingBuildOutput(string sourceDir, string destDir)
	{
		foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(sourceDir, file);
			if (relative.Contains($"obj{Path.DirectorySeparatorChar}") || relative.Contains($"bin{Path.DirectorySeparatorChar}"))
				continue;

			string target = Path.Combine(destDir, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target, overwrite: true);
		}
	}
}
