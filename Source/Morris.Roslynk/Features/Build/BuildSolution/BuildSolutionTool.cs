using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Morris.Roslynk.Features.Build.BuildSolution;

[McpServerToolType]
public sealed partial class BuildSolutionTool
{
	public const string BuildSolutionName = "build_solution";

	[McpServerTool(
		Name = BuildSolutionName,
		Title = "Build the solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = true)]
	[Description(
		"""
		Runs a full 'dotnet build' of the solution out-of-process and returns whether it succeeded with
		error/warning counts and the first error messages. Slower than get_diagnostics (which is an
		in-process compile); use it for full verification.
		""")]
	public async Task<BuildSolutionResponse> BuildSolution(
		[Description("Solution handle returned by open_solution (the .sln/.slnx path).")] string solutionId)
	{
		var startInfo = new ProcessStartInfo("dotnet", $"build \"{solutionId}\" --nologo")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start 'dotnet build'.");

		string output = await process.StandardOutput.ReadToEndAsync();
		_ = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		int errors = ParseCount(output, ErrorCountRegex());
		int warnings = ParseCount(output, WarningCountRegex());

		string[] errorMessages = output
			.Split('\n')
			.Where(line => line.Contains(": error ", StringComparison.Ordinal))
			.Select(line => line.Trim())
			.Distinct(StringComparer.Ordinal)
			.Take(20)
			.ToArray();

		return new BuildSolutionResponse(process.ExitCode == 0, errors, warnings, errorMessages);
	}

	private static int ParseCount(string output, Regex regex)
	{
		Match match = regex.Match(output);
		return match.Success ? int.Parse(match.Groups[1].Value) : 0;
	}

	[GeneratedRegex(@"(\d+)\s+Error\(s\)")]
	private static partial Regex ErrorCountRegex();

	[GeneratedRegex(@"(\d+)\s+Warning\(s\)")]
	private static partial Regex WarningCountRegex();
}
