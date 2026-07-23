using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

/// <summary>Persists one opaque review checkpoint document for a worktree.</summary>
public interface IReviewCheckpointStore {
	/// <summary>Loads the document, or null when no review has been checkpointed.</summary>
	string? Load();

	/// <summary>Atomically replaces the document.</summary>
	void Save(string document);

	/// <summary>Deletes the document. A missing document is a no-op.</summary>
	void Clear();
}

/// <summary>A checkpoint store for contexts that intentionally do not persist review state.</summary>
public sealed class NoopReviewCheckpointStore : IReviewCheckpointStore {
	private NoopReviewCheckpointStore() { }

	/// <summary>The shared no-op store.</summary>
	public static NoopReviewCheckpointStore Instance { get; } = new();

	/// <inheritdoc/>
	public string? Load() => null;

	/// <inheritdoc/>
	public void Save(string document) => ArgumentNullException.ThrowIfNull(document);

	/// <inheritdoc/>
	public void Clear() { }
}

/// <summary>An owner-only atomic review checkpoint file.</summary>
public sealed class FileReviewCheckpointStore : IReviewCheckpointStore {
	private readonly object _gate = new();

	/// <summary>Creates a store backed by <paramref name="filePath"/>.</summary>
	public FileReviewCheckpointStore(string filePath) {
		ArgumentException.ThrowIfNullOrEmpty(filePath);
		FilePath = filePath;
	}

	/// <summary>The checkpoint file.</summary>
	public string FilePath { get; }

	/// <inheritdoc/>
	public string? Load() {
		lock (_gate) {
			try {
				return File.ReadAllText(FilePath);
			} catch (FileNotFoundException) {
				return null;
			} catch (DirectoryNotFoundException) {
				return null;
			}
		}
	}

	/// <inheritdoc/>
	public void Save(string document) {
		ArgumentNullException.ThrowIfNull(document);
		lock (_gate) {
			SecureFile.WriteAllTextAtomic(FilePath, document);
		}
	}

	/// <inheritdoc/>
	public void Clear() {
		lock (_gate) {
			try {
				File.Delete(FilePath);
			} catch (DirectoryNotFoundException) {
				// The file is already absent.
			}
		}
	}
}
