namespace Morris.Roslynk.Infrastructure.Accesses;

/// <summary>
/// How a single source location touches a field, property or parameter. <see cref="Compound"/>,
/// <see cref="Increment"/> and <see cref="Ref"/> both read and write, so they are reported by find_reads
/// and by find_writes.
/// </summary>
public enum AccessKind
{
	Read,
	Assign,
	Compound,
	Increment,
	Ref,
	Out,
	Init,
}

public static class AccessKindText
{
	public static string Of(AccessKind kind) =>
		kind switch
		{
			AccessKind.Read => "read",
			AccessKind.Assign => "assign",
			AccessKind.Compound => "compound",
			AccessKind.Increment => "increment",
			AccessKind.Ref => "ref",
			AccessKind.Out => "out",
			AccessKind.Init => "init",
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
		};

	public static bool IsRead(AccessKind kind) =>
		kind is AccessKind.Read or AccessKind.Compound or AccessKind.Increment or AccessKind.Ref;

	public static bool IsWrite(AccessKind kind) =>
		kind is not AccessKind.Read;
}
