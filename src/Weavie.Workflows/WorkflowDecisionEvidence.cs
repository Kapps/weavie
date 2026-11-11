namespace Weavie.Workflows;

/// <summary>Reads the current text of a run's files.</summary>
public interface IWorkflowFileReader {
	/// <summary>Reads <paramref name="path"/> within <paramref name="files"/>, or explains why its text is unavailable.</summary>
	WorkflowFileRead Read(WorkflowFileScope files, string path);
}

/// <summary>The current text of a file or a safe reason it could not be read.</summary>
public abstract record WorkflowFileRead {
	/// <summary>The complete decoded text, including empty text.</summary>
	public sealed record Text(string Contents) : WorkflowFileRead;

	/// <summary>A concise failure reason without file contents.</summary>
	public sealed record Unreadable(string Reason) : WorkflowFileRead;
}
