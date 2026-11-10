namespace Weavie.Core.Inference.Decisions;

/// <summary>The decision provider ids <c>inference.decisionProvider</c> accepts.</summary>
public static class DecisionProviderIds {
	/// <summary>The configured inference agent.</summary>
	public const string Agent = "agent";

	/// <summary>OpenAI Decisions.</summary>
	public const string OpenAi = "openai";

	/// <summary>TypeSafe Jev.</summary>
	public const string TypeSafe = "typesafe";

	/// <summary>Every id, agent first.</summary>
	public static IReadOnlyList<string> All { get; } = [Agent, OpenAi, TypeSafe];
}
