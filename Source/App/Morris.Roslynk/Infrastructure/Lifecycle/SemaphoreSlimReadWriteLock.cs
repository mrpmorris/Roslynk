using System;
using System.Threading;
using System.Threading.Tasks;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// An async reader/writer lock built on <see cref="SemaphoreSlim"/>. Used in place of
/// <see cref="System.Threading.ReaderWriterLockSlim"/> because that type is thread-affine (the releasing
/// thread must be the acquiring thread) and so cannot be held across an <c>await</c>, whereas every reader
/// and writer here spans asynchronous work. Multiple readers run concurrently; a writer is exclusive.
/// It is reader-preferring, so callers must hold the read side only briefly (capture the immutable snapshot,
/// release, then work lock-free) or a steady stream of readers will starve writers.
/// </summary>
public sealed class SemaphoreSlimReadWriteLock : IDisposable
{
	private readonly SemaphoreSlim WriteGate = new(1, 1);
	private readonly SemaphoreSlim ReaderCountGate = new(1, 1);
	private int ReaderCount;
	private bool Disposed;

	/// <summary>Acquires shared (read) access; dispose the result to release.</summary>
	public async Task<IDisposable> AcquireReadAsync(CancellationToken cancellationToken = default)
	{
		await ReaderCountGate.WaitAsync(cancellationToken);
		try
		{
			// The first reader takes the write gate, locking writers out; if that acquire is cancelled the
			// count is left untouched (incremented only after a successful acquire), so no phantom reader.
			if (ReaderCount == 0)
				await WriteGate.WaitAsync(cancellationToken);

			ReaderCount++;
		}
		finally
		{
			ReaderCountGate.Release();
		}

		return new Releaser(this, writer: false);
	}

	/// <summary>Acquires exclusive (write) access; dispose the result to release.</summary>
	public async Task<IDisposable> AcquireWriteAsync(CancellationToken cancellationToken = default)
	{
		await WriteGate.WaitAsync(cancellationToken);
		return new Releaser(this, writer: true);
	}

	private void ReleaseRead()
	{
		// Release must not be cancellable, so no token here; the gate is held only to mutate the count.
		ReaderCountGate.Wait();
		try
		{
			if (--ReaderCount == 0)
				WriteGate.Release();
		}
		finally
		{
			ReaderCountGate.Release();
		}
	}

	private void ReleaseWrite() => WriteGate.Release();

	public void Dispose()
	{
		if (Disposed)
			return;

		Disposed = true;
		WriteGate.Dispose();
		ReaderCountGate.Dispose();
	}

	private sealed class Releaser : IDisposable
	{
		private SemaphoreSlimReadWriteLock? Owner;
		private readonly bool Writer;

		public Releaser(SemaphoreSlimReadWriteLock owner, bool writer)
		{
			Owner = owner;
			Writer = writer;
		}

		public void Dispose()
		{
			SemaphoreSlimReadWriteLock? owner = Interlocked.Exchange(ref Owner, null);
			if (owner is null)
				return;

			if (Writer)
				owner.ReleaseWrite();
			else
				owner.ReleaseRead();
		}
	}
}