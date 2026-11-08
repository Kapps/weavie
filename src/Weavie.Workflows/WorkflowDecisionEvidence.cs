namespace Weavie.Workflows;

/// <summary>Reads current file text from the owning session's worktree.</summary>
public interface IWorkflowFileReader {
	/// <summary>Reads one normalized relative path, or explains why its text is unavailable.</summary>
	WorkflowFileRead Read(string relativePath);
}

/// <summary>The current text of a file or a safe reason it could not be read.</summary>
public abstract record WorkflowFileRead {
	/// <summary>The complete decoded text, including empty text.</summary>
	public sealed record Text(string Contents) : WorkflowFileRead;

	/// <summary>A concise failure reason without file contents.</summary>
	public sealed record Unreadable(string Reason) : WorkflowFileRead;
}
