using System;
using System.Threading.Tasks;

namespace DisposeLib;

public sealed class Resource : IDisposable
{
	public void Dispose() { }
}

public sealed class AsyncResource : IAsyncDisposable
{
	public ValueTask DisposeAsync() => default;
}

/// <summary>
/// A type with a Dispose method that does not implement IDisposable, so the method is an ordinary
/// dead-code candidate rather than an excluded interface implementation.
/// </summary>
public sealed class PlainResource
{
	public void Dispose() { }
}

/// <summary>Same shape, but its Dispose is called, so it must not be reported as dead.</summary>
public sealed class CalledResource
{
	public void Dispose() { }
}

public static class Scenarios
{
	public static void DeclaresWithoutUsing()
	{
		Resource resource = new Resource();
		GC.KeepAlive(resource);
	}

	public static void DeclaresPlainWithoutUsing()
	{
		PlainResource resource = new PlainResource();
		GC.KeepAlive(resource);
	}

	public static void CallsCalledDispose()
	{
		CalledResource resource = new CalledResource();
		resource.Dispose();
	}

	public static void UsesUsing()
	{
		using (Resource resource = new Resource())
		{
		}
	}

	public static void CallsDispose()
	{
		Resource resource = new Resource();
		resource.Dispose();
	}

	public static void DeclaresAndUsesUsing()
	{
		Resource kept = new Resource();
		using (Resource used = new Resource())
		{
		}

		GC.KeepAlive(kept);
	}

	public static async Task UsesAwaitUsingAsync()
	{
		await using (AsyncResource resource = new AsyncResource())
		{
		}
	}
}
