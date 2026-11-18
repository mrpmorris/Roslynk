using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>
/// One assembled plugin pipeline per test class: a staged plugins root, the real assembly loader and
/// filesystem, a capturing logger, and a tool sink the test can inspect. Each test stages its own
/// plugin folder. The non-collectible load contexts keep emitted DLLs mapped until the process exits,
/// so cleanup tolerates undeletable folders, the same way the previous loader tests did.
/// </summary>
public abstract class PluginLoaderHarness : IDisposable
{
	public string Root { get; }
	public CapturingLogger<PluginLoader> Logger { get; } = new();
	public ListPluginToolSink Sink { get; } = new();
	public LoadedPluginRegistry Loaded { get; } = new();
	public InstanceRegistry Registry { get; }
	public TimeSpan? StartupBudget { get; set; }
	internal PluginToolTracker Tracker { get; } = new();

	public PluginLoaderHarness()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslynk-loader-tests-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Root);
		Registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.FromSeconds(30)));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The non-collectible load context keeps the entry DLL mapped until the process exits,
			// so a folder with a loaded plugin cannot be deleted from within the test run.
		}
	}

	/// <summary>Emits a plugin into `{name}/` under the root and returns its candidate.</summary>
	public PluginCandidate Stage(string name, string source, bool toolReference = false, bool roslynReference = false, IReadOnlyList<(string AssemblyName, string Source)>? extraAssemblies = null)
	{
		string folder = Path.Combine(Root, name);
		_ = Directory.CreateDirectory(folder);
		string entryPath = TestPluginCompiler.Emit(folder, $"{name}.Roslynk.Plugin", source,
			toolReference: toolReference,
			roslynReference: roslynReference,
			extraAssemblies: extraAssemblies);
		return new PluginCandidate(name, folder, entryPath);
	}

	public PluginLoader CreateLoader() =>
		new(
			new DefaultFileSystem(),
			new PluginAssemblyLoader(),
			new PluginToolFactory(new ResponseBudget(80_000), Tracker, NullLoggerFactory.Instance),
			Sink,
			new PluginContext(
				Registry,
				new LoadWaitTimeout(TimeSpan.FromSeconds(30)),
				new ResponseBudget(80_000),
				NullLoggerFactory.Instance,
				new PluginShutdownState()),
			new SolutionEventPublisher(new CapturingLogger<SolutionEventPublisher>()),
			Loaded,
			Registry,
			StartupBudget,
			runtimeDirectory: AppContext.BaseDirectory,
			logger: Logger);

	public async Task<LoadedPlugin?> LoadAsync(PluginCandidate candidate)
	{
		LoadedPlugin? loaded = await CreateLoader().LoadAsync(candidate, CancellationToken.None);
		if (loaded is not null)
			Assert.Contains(loaded, Loaded.All());
		else
			Assert.Empty(Loaded.All());
		return loaded;
	}

	public (string? Stage, string Message, Exception? Exception) SingleError()
	{
		var (level, message, exception) = Assert.Single(Logger.Records, record => record.Level == LogLevel.Error);
		return (ExtractStage(message), message, exception);
	}

	private static string? ExtractStage(string message)
	{
		const string marker = "[stage=";
		int start = message.IndexOf(marker, StringComparison.Ordinal);
		if (start < 0)
			return null;
		int end = message.IndexOf(']', start);
		string token = message[(start + marker.Length)..end];
		return token == "(none)" ? null : token;
	}
}
