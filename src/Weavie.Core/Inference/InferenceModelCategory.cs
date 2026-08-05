namespace Weavie.Core.Inference;

/// <summary>The quality/cost class a feature requests without naming a provider-specific model.</summary>
public enum InferenceModelCategory {
	/// <summary>Small, bounded generation and classification work.</summary>
	Utility,

	/// <summary>Critique, diagnosis, and other work that needs stronger judgment.</summary>
	Reasoning,
}

/// <summary>The kinds of user or workspace data a declared operation may transmit.</summary>
[Flags]
public enum InferenceDataKind {
	/// <summary>User-authored prose.</summary>
	UserText = 1,

	/// <summary>Repository metadata such as branch names.</summary>
	RepositoryMetadata = 2,

	/// <summary>Source code.</summary>
	SourceCode = 4,

	/// <summary>Build, test, lint, or other command output.</summary>
	CommandOutput = 8,

	/// <summary>Output produced by an interactive agent.</summary>
	AgentOutput = 16,
}

/// <summary>Whether a person directly initiated an inference operation.</summary>
public enum InferenceInvocationOrigin {
	/// <summary>The query is part of an explicit user action.</summary>
	UserInitiated,

	/// <summary>The application initiated the query from an observed event.</summary>
	Automatic,
}
