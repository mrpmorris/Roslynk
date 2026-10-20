namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>The watcher attached to an instance; owns the errors it met that a reload would not fix.</summary>
public interface IInstanceWatcher : IDisposable
{
	WatchDiagnostics Diagnostics { get; }
}
