using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// The analyzer DLLs a load could not take as final, kept fresh without a reload. Two kinds: a reference the
/// design-time build resolved to a file that did not exist (typically a generator project referenced as an
/// analyzer and not yet built for the loaded configuration), which the load had to skip; and a DLL built by a
/// project of the solution itself, whose rebuild changes its bits. Each is compared by file stamp (write time
/// and length, as the shadow loader keys its copies) on use rather than on watcher events: build output lives
/// under bin/obj, which the watcher ignores, and can lie outside every watched directory (an artifacts layout,
/// a generator outside the solution), and a build may finish while the solution is still loading.
/// </summary>
public sealed class TrackedAnalyzers
{
	public static readonly TrackedAnalyzers None = new([]);

	private readonly IReadOnlyList<TrackedAnalyzer> Entries;
	private readonly object Gate = new();
	private Task LastRefresh = Task.CompletedTask;

	internal TrackedAnalyzers(IReadOnlyList<TrackedAnalyzer> entries)
	{
		Entries = entries;
	}

	/// <summary>
	/// The messages that still apply: a skipped reference whose DLL is still missing, and the last load
	/// failure of a tracked DLL. A message goes away once its DLL is built and loads.
	/// </summary>
	public IEnumerable<string> Messages
	{
		get
		{
			foreach (TrackedAnalyzer entry in Entries)
			{
				if (entry.Message is string message)
					yield return message;
			}
		}
	}

	/// <summary>
	/// Compares each tracked DLL's stamp with the one last attached and, for every DLL that was built or
	/// rebuilt since, loads a fresh shadow-copied reference and has <paramref name="apply"/> swap it into the
	/// projects that reference the path. Completes when the swap is published, so the caller's next read sees
	/// it. Concurrent callers that find nothing new await the refresh in flight rather than read around it.
	/// Best effort and never throws for a DLL: a load that throws or a failed swap restores the previous stamps,
	/// so the next call retries, and a load failure is reported as the entry's message meanwhile.
	/// </summary>
	public Task RefreshAsync(Func<Func<Solution, Solution>, Task> apply)
	{
		ArgumentNullException.ThrowIfNull(apply);
		if (Entries.Count == 0)
			return Task.CompletedTask;

		lock (Gate)
		{
			var loaded = new List<Loaded>();
			foreach (TrackedAnalyzer entry in Entries)
			{
				string? stamp = FileStamp.Of(entry.FullPath);
				if (stamp is null || string.Equals(stamp, entry.Stamp, StringComparison.Ordinal))
					continue;

				string? previous = entry.Stamp;
				AnalyzerFileReference reference;
				try
				{
					reference = entry.Load(stamp);
				}
				catch (Exception exception)
				{
					// The loader threw (its copy raced the build still writing the DLL, say): nothing is attached,
					// so the stamp goes back for the next call to retry, and the failure is reported meanwhile
					// rather than left as silent phantom errors.
					entry.Stamp = previous;
					entry.Failure = $"Analyzer load failed for '{entry.FullPath}': {exception.Message}";
					continue;
				}

				loaded.Add(new Loaded(entry, previous, stamp, reference));
			}

			if (loaded.Count == 0)
				return LastRefresh;

			LastRefresh = ApplyAsync(loaded, apply);
			return LastRefresh;
		}
	}

	private async Task ApplyAsync(IReadOnlyList<Loaded> loaded, Func<Func<Solution, Solution>, Task> apply)
	{
		try
		{
			await apply(solution => WithReferences(solution, loaded));
			foreach (Loaded item in loaded)
				item.Entry.Attached = true;
		}
		catch (Exception)
		{
			// The instance is shutting down or the write faulted; the stamps go back so the next use retries.
			lock (Gate)
			{
				foreach (Loaded item in loaded)
				{
					if (string.Equals(item.Entry.Stamp, item.Stamp, StringComparison.Ordinal))
						item.Entry.Stamp = item.PreviousStamp;
				}
			}
		}
	}

	/// <summary>
	/// Every project referencing a loaded path gets its fresh reference: replacing the one it holds (rebuilt
	/// bits need a new reference), or added when the load skipped it. Projects no longer in the solution (a
	/// reload published meanwhile, with new project ids) are left alone, and an unchanged solution is
	/// returned as is.
	/// </summary>
	private static Solution WithReferences(Solution solution, IReadOnlyList<Loaded> loaded)
	{
		Solution updated = solution;
		foreach (Loaded item in loaded)
		{
			foreach (ProjectId projectId in item.Entry.ProjectIds)
			{
				if (updated.GetProject(projectId) is not Project project)
					continue;

				var references = new List<AnalyzerReference>(project.AnalyzerReferences);
				int existing = references.FindIndex(reference =>
					reference is AnalyzerFileReference file && TrackedAnalyzer.PathsEqual(file.FullPath, item.Entry.FullPath));
				if (existing >= 0)
					references[existing] = item.Reference;
				else
					references.Add(item.Reference);

				updated = updated.WithProjectAnalyzerReferences(projectId, references);
			}
		}

		return updated;
	}

	private sealed record Loaded(TrackedAnalyzer Entry, string? PreviousStamp, string Stamp, AnalyzerFileReference Reference);
}

/// <summary>
/// One tracked analyzer DLL path, the projects referencing it and the stamp of the bits last attached (null
/// while the DLL has never been attached). Mutable state is guarded by the owning <see cref="TrackedAnalyzers"/>.
/// </summary>
internal sealed class TrackedAnalyzer
{
	private readonly List<ProjectId> ProjectIdList = [];
	private readonly List<string> ConsumerNames = [];
	private readonly List<string> Languages = [];
	private readonly Func<string, Action<string>, IEnumerable<string>, AnalyzerFileReference> CreateReference;
	private volatile string? StampField;
	private volatile string? FailureField;
	private volatile bool AttachedField;

	public TrackedAnalyzer(
		string fullPath,
		string? producerName,
		Func<string, Action<string>, IEnumerable<string>, AnalyzerFileReference> createReference)
	{
		FullPath = fullPath;
		ProducerName = producerName;
		CreateReference = createReference;
	}

	public string FullPath { get; }

	/// <summary>The solution project whose output the DLL is, or null when no project of the solution builds it.</summary>
	public string? ProducerName { get; }

	public IReadOnlyList<ProjectId> ProjectIds => ProjectIdList;

	/// <summary>The stamp of the bits last loaded, or null when the DLL has never been.</summary>
	public string? Stamp
	{
		get => StampField;
		set => StampField = value;
	}

	/// <summary>The last load failure of the DLL, cleared by the next load.</summary>
	public string? Failure
	{
		get => FailureField;
		set => FailureField = value;
	}

	/// <summary>True once a reference to the DLL is in the published solution.</summary>
	public bool Attached
	{
		get => AttachedField;
		set => AttachedField = value;
	}

	/// <summary>
	/// The message that still applies, if any: the last load failure, or, while the DLL has never been
	/// attached and is still missing, why its generated code is unavailable and what to build.
	/// </summary>
	public string? Message
	{
		get
		{
			if (FailureField is string failure)
				return failure;

			if (Attached || File.Exists(FullPath))
				return null;

			string consumers = string.Join(", ", ConsumerNames.Select(name => $"'{name}'"));
			string noun = ConsumerNames.Count == 1 ? "project" : "projects";
			return ProducerName is null
				? $"Skipped unresolved analyzer in {noun} {consumers}: '{FullPath}' does not exist, so its analyzers and "
					+ "generated code are unavailable. Build or restore what produces it; the next call picks it up without a reload."
				: $"Skipped unresolved analyzer in {noun} {consumers}: '{FullPath}' does not exist, so the generated code of "
					+ $"project '{ProducerName}' is unavailable. Build project '{ProducerName}' in the configuration this path "
					+ "names; the next call picks it up without a reload.";
		}
	}

	/// <summary>Records a project that references the DLL, once.</summary>
	public void AddConsumer(Project project)
	{
		if (ProjectIdList.Contains(project.Id))
			return;

		ProjectIdList.Add(project.Id);
		if (!ConsumerNames.Contains(project.Name, StringComparer.Ordinal))
			ConsumerNames.Add(project.Name);
		if (!Languages.Contains(project.Language, StringComparer.Ordinal))
			Languages.Add(project.Language);
	}

	/// <summary>
	/// Loads a fresh reference to the DLL for every consumer language, recording a failure as this entry's
	/// message rather than dropping it silently, and makes <paramref name="stamp"/> the attached stamp.
	/// </summary>
	public AnalyzerFileReference Load(string stamp)
	{
		FailureField = null;
		Stamp = stamp;
		return CreateReference(FullPath, message =>
		{
			if (string.Equals(Stamp, stamp, StringComparison.Ordinal))
				FailureField = message;
		}, Languages);
	}

	public static bool PathsEqual(string left, string right) =>
		string.Equals(FileStamp.NormalizePath(left), FileStamp.NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>File identity helpers shared by the tracked analyzers.</summary>
internal static class FileStamp
{
	/// <summary>
	/// The file's write time and length, the key the shadow loader copies by, or null when the file is
	/// missing or unreadable.
	/// </summary>
	public static string? Of(string path)
	{
		try
		{
			var info = new FileInfo(path);
			return info.Exists ? string.Concat(info.LastWriteTimeUtc.Ticks.ToString(), "|", info.Length.ToString()) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static string NormalizePath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return path;
		}
	}
}
