using Weavie.Core.FileSystem;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>
/// A session worktree's workflow run, persisted to <c>~/.weavie/workspaces/&lt;id&gt;/workflow-run.json</c>. An unreadable
/// file is backed up to <c>.bad</c> and reported through <see cref="LoadFailure"/> so the loss is surfaced to the user.
/// </summary>
public sealed class WorkflowRunStore : JsonDocumentStore, IWorkflowRunStore {
	private WorkflowRun? _run;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public WorkflowRunStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <inheritdoc/>
	public string? LoadFailure { get; private set; }

	/// <inheritdoc/>
	public WorkflowRun? Run {
		get {
			lock (Gate) {
				return _run;
			}
		}
	}

	/// <inheritdoc/>
	public void Save(WorkflowRun? run) {
		lock (Gate) {
			var previous = _run;
			_run = run;
			try {
				PersistLocked();
			} catch {
				_run = previous;
				throw;
			}
		}
	}

	/// <inheritdoc/>
	protected override void OnPersistFailed(Exception cause) =>
		throw new WorkflowException($"Could not save workflow run in {FilePath}: {cause.Message}");

	/// <inheritdoc/>
	protected override void Restore(string? text) => _run = text is null ? null : WorkflowRunDocument.Read(text);

	/// <inheritdoc/>
	protected override string Render() => WorkflowRunDocument.Write(_run);

	/// <inheritdoc/>
	protected override void OnUnusable(string? text, Exception cause) {
		base.OnUnusable(text, cause);
		LoadFailure = $"The saved workflow run in {FilePath} couldn't be read ({cause.Message}); it was set aside.";
	}
}
