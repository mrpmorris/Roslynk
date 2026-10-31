using System.Diagnostics;
using System.Text;

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

	/// <summary>A clean single-project solution.</summary>
	public static string Simple => SimpleSolution.Value;

	/// <summary>
	/// The same clean solution copied to a scratch directory, for tests that add source files. It carries
	/// every semicolon-terminated type shape (positional records, a record struct, a bodiless class and
	/// interface) that the committed fixture cannot, because the fixture is compiled with warnings as
	/// errors and an unreferenced bodiless type is reported as unused.
	/// </summary>
	public static string CreateScratchSimpleSolutionWithBodilessTypes()
	{
		string path = CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(path)!, "SimpleLibrary");
		File.WriteAllText(Path.Combine(library, "BodilessTypes.cs"), BodilessTypesSource);
		Restore(path);
		return path;
	}

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
		string path = Path.Combine([FindTestFixturesRoot(), .. relativeParts]);
		Restore(path);
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
	/// A writable scratch copy of the CshtmlSolution fixture: an MVC view (Views/Home/Index.cshtml) with a used
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

	/// <summary>A writable scratch copy of the GeneratorSolution fixture with its generator DLL built, for tests that edit generator inputs.</summary>
	public static string CreateScratchGeneratorSolution()
	{
		string path = CreateScratch("GeneratorSolution", "GeneratorSolution.slnx");
		BuildScratchGenerator(path);
		return path;
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
		CopyExcludingBuildOutput(sourceDir, destDir);

		string solutionPath = Path.Combine(destDir, solutionFile);
		Restore(solutionPath);
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
