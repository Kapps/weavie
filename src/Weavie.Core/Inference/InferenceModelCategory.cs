namespace Weavie.Core.Inference;

/// <summary>The quality/cost class a feature requests without naming a provider-specific model.</summary>
public enum InferenceModelCategory {
	/// <summary>Small, bounded generation and classification work.</summary>
	Utility,

	/// <summary>Critique, diagnosis, and other work that needs stronger judgment.</summary>
	Reasoning,
}

/// <summary>Whether a person directly initiated an inference query.</summary>
public enum InferenceInvocationOrigin {
	/// <summary>The query is part of an explicit user action.</summary>
	UserInitiated,

	/// <summary>The application initiated the query from an observed event.</summary>
	Automatic,
}
