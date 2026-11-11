namespace Weavie.Core.FileSystem;

/// <summary>
/// A store whose data the user accepted, so an unusable file is never reset or backed up: the failure is held and
/// thrown from the first use, and a failed write leaves memory and disk agreeing.
/// </summary>
/// <typeparam name="TState">The loaded state, replaced whole on every change.</typeparam>
public abstract class StrictJsonDocumentStore<TState> : JsonDocumentStore where TState : class {
	private Exception? _loadFailure;
	private TState? _state;

	/// <summary>Creates a store over <paramref name="path"/>; the subclass constructor calls <see cref="JsonDocumentStore.Load"/>.</summary>
	/// <param name="fileSystem">The filesystem the store reads and writes through.</param>
	/// <param name="path">The backing file.</param>
	protected StrictJsonDocumentStore(IFileSystem fileSystem, string path) : base(fileSystem, path) { }

	/// <summary>What the file holds, for failure messages, e.g. <c>ACP control defaults</c>.</summary>
	protected abstract string Subject { get; }

	/// <summary>The state with nothing saved.</summary>
	protected abstract TState Empty();

	/// <summary>Reads persisted <paramref name="text"/>; throws <see cref="System.Text.Json.JsonException"/> when it is unusable.</summary>
	protected abstract TState Read(string text);

	/// <summary>Renders <paramref name="state"/> for persisting.</summary>
	protected abstract string Write(TState state);

	/// <summary>The store's failure type for <paramref name="message"/>.</summary>
	protected abstract Exception Failure(string message, Exception cause);

	/// <summary>The loaded state; throws the held load failure. Read under <see cref="JsonDocumentStore.Gate"/>.</summary>
	protected TState Current => _loadFailure is null ? _state! : throw _loadFailure;

	/// <summary>Persists <paramref name="next"/> and adopts it only once the write succeeded. Call under <see cref="JsonDocumentStore.Gate"/>.</summary>
	protected void Commit(TState next) {
		var previous = Current;
		_state = next;
		try {
			PersistLocked();
		} catch (Exception) {
			_state = previous;
			throw;
		}
	}

	/// <inheritdoc/>
	protected sealed override void Restore(string? text) => _state = text is null ? Empty() : Read(text);

	/// <inheritdoc/>
	protected sealed override string Render() => Write(_state!);

	/// <inheritdoc/>
	protected sealed override void OnUnusable(string? text, Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		_state = Empty();
		_loadFailure = Failure($"{Subject} could not be loaded from '{FilePath}': {cause.Message}", cause);
	}

	/// <inheritdoc/>
	protected sealed override void OnPersistFailed(Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		throw Failure($"{Subject} could not be persisted to '{FilePath}': {cause.Message}", cause);
	}
}
