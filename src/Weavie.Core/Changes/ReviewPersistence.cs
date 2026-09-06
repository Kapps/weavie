using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

/// <summary>The durable document owned by one worktree's review.</summary>
public interface IReviewPersistence {
	/// <summary>Reads the last complete review, or null for a new review.</summary>
	string? Read();
	/// <summary>Atomically saves a complete review; failures must reach the caller.</summary>
	void Save(string document);
}

/// <summary>In-memory review storage for isolated trackers.</summary>
public sealed class MemoryReviewPersistence : IReviewPersistence {
	private string? _document;
	/// <inheritdoc />
	public string? Read() => _document;
	/// <inheritdoc />
	public void Save(string document) => _document = document;
}

/// <summary>Atomic review storage with strict, non-destructive failure handling.</summary>
public sealed class ReviewPersistence : JsonDocumentStore, IReviewPersistence {
	private string? _document;
	/// <summary>Opens one worktree's review document.</summary>
	public ReviewPersistence(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <inheritdoc />
	public string? Read() { lock (Gate) return _document; }
	/// <inheritdoc />
	public void Save(string document) {
		lock (Gate) {
			string? previous = _document;
			_document = document;
			try { PersistLocked(() => SecureFile.Restrict(FilePath)); } catch { _document = previous; throw; }
		}
	}
	/// <inheritdoc />
	protected override void Restore(string? text) {
		if (text is not null) { using var parsed = JsonDocument.Parse(text); }
		_document = text;
	}
	/// <inheritdoc />
	protected override string Render() => _document!;
	/// <inheritdoc />
	protected override void OnUnusable(string? text, Exception cause) =>
		throw new IOException($"Could not restore review '{FilePath}'. Its saved state was preserved.", cause);
	/// <inheritdoc />
	protected override void OnPersistFailed(Exception cause) =>
		throw new IOException($"Could not save review '{FilePath}'.", cause);
}
