namespace Weavie.Workflows;

/// <summary>What a run was started with: the user's text and images. Templates read the text as <c>{{run.prompt}}</c>.</summary>
public sealed record WorkflowPrompt {
	/// <summary>The prompt text, or empty.</summary>
	public required string Text { get; init; }

	/// <summary>The prompt's saved images, in the order they were attached.</summary>
	public required IReadOnlyList<WorkflowPromptImage> Images { get; init; }

	/// <summary>A run started without a prompt.</summary>
	public static WorkflowPrompt None { get; } = new() { Text = "", Images = [] };
}

/// <summary>One saved image of a run's prompt.</summary>
public sealed record WorkflowPromptImage {
	/// <summary>The file's path relative to the run's documents directory.</summary>
	public required string File { get; init; }

	/// <summary>The image's MIME type.</summary>
	public required string Mime { get; init; }
}

/// <summary>The prompt a start is given, its images still in memory; starting the run saves them.</summary>
public sealed record WorkflowStartPrompt(string Text, IReadOnlyList<WorkflowStartImage> Images) {
	/// <summary>A start with no prompt.</summary>
	public static WorkflowStartPrompt None { get; } = new("", []);

	/// <summary>Whether there is neither text nor an image.</summary>
	public bool IsEmpty => Text.Length == 0 && Images.Count == 0;
}

/// <summary>One image of a start's prompt: its file extension with the dot, MIME type, and content.</summary>
public sealed record WorkflowStartImage(string Extension, string Mime, byte[] Bytes);

/// <summary>An image a turn carries, by absolute path.</summary>
public sealed record WorkflowTurnImage(string Path, string Mime);
