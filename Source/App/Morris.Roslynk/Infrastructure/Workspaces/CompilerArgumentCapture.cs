using System.Runtime.CompilerServices;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// What the design-time build MSBuildWorkspace runs worked out, read from a binary log of it. The compiler arguments
/// are logged as the <c>CscCommandLineArgs</c> output of each <c>Csc</c> task, which a design-time build runs with
/// <c>SkipCompilerExecution=true</c>, so nothing is added to the build. MSBuildWorkspace runs that build in its
/// out-of-process build host, so a log file is the only way out: it is written to a folder of Roslynk's own and
/// deleted as soon as it has been read, since a build log can hold the values of environment variables.
/// <para>
/// Types from <c>Microsoft.Build</c> come from the SDK that <see cref="MsBuildRegistrar"/> registers, so they are only
/// touched in methods that run after registration, never in this class's fields.
/// </para>
/// </summary>
internal sealed class CompilerArgumentCapture : IDisposable
{
	private const string LogName = "load.binlog";

	private readonly string Folder;

	private CompilerArgumentCapture(string folder)
	{
		Folder = folder;
	}

	/// <summary>A capture into a new folder under <paramref name="root"/>.</summary>
	public static CompilerArgumentCapture Begin(string root)
	{
		string folder = Path.Combine(root, "captures", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		return new CompilerArgumentCapture(folder);
	}

	/// <summary>Opens the solution in <paramref name="workspace"/>, logging its design-time build into this capture.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public Task<Solution> OpenSolutionAsync(MSBuildWorkspace workspace, string solutionPath, IProgress<ProjectLoadProgress> progress, CancellationToken cancellationToken) =>
		workspace.OpenSolutionAsync(solutionPath, new BinaryLogger { Parameters = Path.Combine(Folder, LogName) }, progress, cancellationToken);

	/// <summary>
	/// What the load's logs hold. MSBuildWorkspace numbers the log of each build host it starts
	/// (<c>load-1.binlog</c>), so every log in the folder is read.
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public CapturedLoad Read()
	{
		var arguments = new Dictionary<(string ProjectFile, string TargetFramework), string[]>();
		var assetsFiles = new Dictionary<string, string>(StringComparer.Ordinal);
		var sdkFolders = new SortedSet<string>(StringComparer.Ordinal);

		foreach (string log in Directory.EnumerateFiles(Folder, "*.binlog").Order(StringComparer.Ordinal))
		{
			var targetFrameworks = new Dictionary<int, string>();
			var replay = new BinaryLogReplayEventSource { AllowForwardCompatibility = true };
			replay.RecoverableReadError += error =>
			{
				// Unknown unrelated events are safe to skip, but incomplete capture records cannot be cached.
				if (error.ErrorType == ReaderErrorType.UnknownFormatOfEventData
					|| error.RecordKind is BinaryLogRecordKind.ProjectStarted or BinaryLogRecordKind.TaskStarted or BinaryLogRecordKind.TaskParameter)
					throw new InvalidDataException(error.GetFormattedMessage());
			};
			replay.AnyEventRaised += (_, e) =>
			{
				switch (e)
				{
					case ProjectStartedEventArgs { BuildEventContext: { } context } started:
						targetFrameworks[context.ProjectInstanceId] =
							started.GlobalProperties?.TryGetValue("TargetFramework", out string? framework) == true ? framework : "";
						break;

					// The Csc task comes from sdk/<version>/Roslyn: the SDK the design-time build used.
					case TaskStartedEventArgs { TaskName: "Csc", TaskFile: string taskFile }
						when Path.GetDirectoryName(Path.GetDirectoryName(taskFile)) is string sdkFolder
							&& Path.GetFileName(Path.GetDirectoryName(sdkFolder)) == "sdk":
						sdkFolders.Add(FileStamp.NormalizePath(sdkFolder));
						break;

					case TaskParameterEventArgs { Kind: TaskParameterMessageKind.TaskInput, ParameterName: "ProjectAssetsFile", ProjectFile: string assetsProject } input
						when Items(input).FirstOrDefault() is { Length: > 0 } assets:
						assetsFiles.TryAdd(FileStamp.NormalizePath(assetsProject), FileStamp.NormalizePath(assets));
						break;

					case TaskParameterEventArgs { Kind: TaskParameterMessageKind.TaskOutput, ItemType: "CscCommandLineArgs", ProjectFile: string projectFile } output:
						string targetFramework = output.BuildEventContext is { } outputContext
							&& targetFrameworks.TryGetValue(outputContext.ProjectInstanceId, out string? known) ? known : "";
						arguments[(FileStamp.NormalizePath(projectFile), targetFramework)] = Items(output);
						break;
				}
			};
			replay.Replay(log);
		}

		return new CapturedLoad(
			[.. arguments.Select(pair => new CapturedArguments(
				pair.Key.ProjectFile,
				pair.Key.TargetFramework,
				assetsFiles.GetValueOrDefault(pair.Key.ProjectFile),
				pair.Value))],
			sdkFolders);
	}

	private static string[] Items(TaskParameterEventArgs parameter) =>
		[.. parameter.Items.Cast<object>().Select(item => item is ITaskItem taskItem ? taskItem.ItemSpec : item.ToString() ?? "")];

	public void Dispose()
	{
		try
		{
			Directory.Delete(Folder, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Left for the cache's sweep of old capture folders.
		}
	}

	/// <summary>Writes beside the target and moves into place, so a concurrent reader never sees half a file.</summary>
	internal static void AtomicWrite(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temp = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(temp, text);
		File.Move(temp, path, overwrite: true);
	}
}

/// <summary>
/// What one load's design-time build worked out: the compiler arguments of each project and target framework, and the
/// SDK folders it ran from. The log has no imports or environment reads for the projects MSBuildWorkspace opens: its
/// build host evaluates them before the build it logs, so <see cref="BuildInputs"/> names those from the files.
/// </summary>
internal sealed record CapturedLoad(
	IReadOnlyList<CapturedArguments> Arguments,
	IReadOnlySet<string> SdkFolders);

/// <summary>
/// The compiler arguments one design-time build of a project handed the compiler, with its restore output. The target
/// framework is empty for a project that targets one.
/// </summary>
internal sealed record CapturedArguments(
	string ProjectFile,
	string TargetFramework,
	string? ProjectAssetsFile,
	string[] Arguments);
