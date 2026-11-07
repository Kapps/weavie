using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Workflows;

/// <summary>Where a session's run is persisted; the owner chooses the medium.</summary>
public interface IWorkflowRunStore {
	/// <summary>The persisted run, or <see langword="null"/>.</summary>
	WorkflowRun? Run { get; }

	/// <summary>Why the persisted run couldn't be loaded, or <see langword="null"/>.</summary>
	string? LoadFailure { get; }

	/// <summary>Persists <paramref name="run"/> (or clears it).</summary>
	void Save(WorkflowRun? run);
}

/// <summary>The persisted form of a run: <c>{"run": …}</c>, with <c>null</c> for none.</summary>
public static partial class WorkflowRunDocument {
	/// <summary>The run <paramref name="text"/> holds, or <see langword="null"/>; throws when it isn't a run document.</summary>
	public static WorkflowRun? Read(string text) => JsonSerializer.Deserialize(text, RunDocumentJson.Default.RunDocument)?.Run;

	/// <summary>The document text for <paramref name="run"/>.</summary>
	public static string Write(WorkflowRun? run) =>
		JsonSerializer.Serialize(new RunDocument { Run = run }, RunDocumentJson.Default.RunDocument);

	internal sealed class RunDocument {
		[JsonPropertyName("run")]
		public WorkflowRun? Run { get; set; }
	}

	[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(RunDocument))]
	internal sealed partial class RunDocumentJson : JsonSerializerContext;
}
