namespace Morris.Roslynk.Infrastructure.Watching;

/// <summary>
/// A directory to watch and whether to descend into its sub-directories. Project directories are watched
/// recursively (to catch new globbed files); directories that merely host a linked or out-of-tree file
/// are watched shallowly so a file linked from, say, <c>C:\Shared</c> does not drag the whole tree in.
/// </summary>
public sealed record WatchTarget(string Directory, bool Recursive);
