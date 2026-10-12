namespace Weavie.Core.Processes;

/// <summary>
/// One <see cref="ChildTemp"/> directory, deleted once its owner has let go and every child started in it has been
/// reaped. <see cref="OwnedProcess.Start(System.Diagnostics.ProcessStartInfo)"/> gives each child a scope of its
/// own; an owner whose children share state through temp across runs (an agent's terminal commands) shares one.
/// </summary>
public sealed class ChildTempScope : IDisposable {
	private readonly Lock _gate = new();
	private int _holders = 1;
	private bool _disposed;

	private ChildTempScope(string directory) {
		Directory = directory;
	}

	/// <summary>Creates a fresh directory under this process's root.</summary>
	public static ChildTempScope Create() => new(ChildTemp.CreateDirectory());

	/// <summary>The directory children see as their system temp path.</summary>
	public string Directory { get; }

	internal void Acquire() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			_holders++;
		}
	}

	internal void Release() {
		lock (_gate) {
			if (--_holders > 0) return;
		}
		ChildTemp.Delete(Directory);
	}

	/// <summary>Lets go of the directory: no new child may start in it, and it goes when the last one is reaped.</summary>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) return;
			_disposed = true;
		}
		Release();
	}
}
