using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Workflows;

/// <summary>
/// A session worktree's workflow run, persisted to <c>~/.weavie/workspaces/&lt;id&gt;/workflow-run.json</c>. An unreadable
/// file is backed up to <c>.bad</c> and reported through <see cref="LoadFailure"/> so the loss is surfaced to the user.
/// </summary>
public sealed partial class WorkflowRunStore : JsonDocumentStore {
	private WorkflowRun? _run;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public WorkflowRunStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Why the persisted run couldn't be loaded, or <see langword="null"/>.</summary>
	public string? LoadFailure { get; private set; }

	/// <summary>The persisted run, or <see langword="null"/>.</summary>
	public WorkflowRun? Run {
		get {
			lock (Gate) {
				return _run;
			}
		}
	}

	/// <summary>Persists <paramref name="run"/> (or clears it).</summary>
	public void Save(WorkflowRun? run) {
		lock (Gate) {
			_run = run;
			PersistLocked();
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) =>
		_run = text is null ? null : JsonSerializer.Deserialize(text, WorkflowRunJson.Default.RunDocument)?.Run;

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new RunDocument { Run = _run }, WorkflowRunJson.Default.RunDocument);

	/// <inheritdoc/>
	protected override void OnUnusable(string? text, Exception cause) {
		base.OnUnusable(text, cause);
		LoadFailure = $"The saved workflow run in {FilePath} couldn't be read ({cause.Message}); it was set aside.";
	}

	private sealed class RunDocument {
		[JsonPropertyName("run")]
		public WorkflowRun? Run { get; set; }
	}

	[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(RunDocument))]
	private sealed partial class WorkflowRunJson : JsonSerializerContext;
}
