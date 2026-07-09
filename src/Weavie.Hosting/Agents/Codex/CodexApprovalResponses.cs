using System.Text.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Builds app-server responses for user-resolved Codex server requests.</summary>
internal static class CodexApprovalResponses {
	public static object Build(CodexServerRequest request, string decision) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrEmpty(decision);
		return request.Method switch {
			"item/commandExecution/requestApproval" => new { decision },
			"item/fileChange/requestApproval" => new { decision },
			"item/permissions/requestApproval" => PermissionResponse(request.Message, decision),
			"mcpServer/elicitation/request" => new { action = ElicitationAction(decision) },
			_ => throw new InvalidOperationException($"Codex request '{request.Method}' is not an approval request."),
		};
	}

	public static bool CanResolve(string method) =>
		method is "item/commandExecution/requestApproval"
			or "item/fileChange/requestApproval"
			or "item/permissions/requestApproval"
			or "mcpServer/elicitation/request";

	private static object PermissionResponse(JsonElement message, string decision) {
		string scope = string.Equals(decision, "acceptForSession", StringComparison.Ordinal) ? "session" : "turn";
		if (IsAccepted(decision)
			&& message.TryGetProperty("params", out var parameters)
			&& parameters.TryGetProperty("permissions", out var permissions)) {
			return new { permissions, scope };
		}

		return new { permissions = new { }, scope };
	}

	private static string ElicitationAction(string decision) =>
		decision switch {
			"acceptForSession" => "accept",
			"accept" => "accept",
			"decline" => "decline",
			"cancel" => "cancel",
			_ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unsupported Codex elicitation decision."),
		};

	private static bool IsAccepted(string decision) =>
		string.Equals(decision, "accept", StringComparison.Ordinal)
			|| string.Equals(decision, "acceptForSession", StringComparison.Ordinal);
}
