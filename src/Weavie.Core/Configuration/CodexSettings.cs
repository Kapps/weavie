namespace Weavie.Core.Configuration;

/// <summary>Settings that determine the defaults for new native Codex sessions.</summary>
public static class CodexSettings {
	/// <summary>The model used for new Codex sessions.</summary>
	public const string Model = "codex.model";

	/// <summary>The reasoning effort used for new Codex sessions.</summary>
	public const string Effort = "codex.effort";

	/// <summary>The service tier used for new Codex sessions.</summary>
	public const string ServiceTier = "codex.serviceTier";

	/// <summary>The sandbox used for new Codex sessions.</summary>
	public const string Sandbox = "codex.sandbox";

	/// <summary>The approval policy used for new Codex sessions.</summary>
	public const string ApprovalPolicy = "codex.approvalPolicy";
}
