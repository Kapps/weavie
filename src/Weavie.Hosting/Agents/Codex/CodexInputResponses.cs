namespace Weavie.Hosting.Agents.Codex;

/// <summary>Builds app-server responses for structured Codex user-input requests.</summary>
internal static class CodexInputResponses {
	public static object Build(IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentNullException.ThrowIfNull(answers);
		var payload = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var answer in answers) {
			if (answer.Key.Length == 0) {
				continue;
			}

			payload[answer.Key] = new { answers = answer.Value };
		}

		return new { answers = payload };
	}

	public static bool CanResolve(string method) =>
		string.Equals(method, "item/tool/requestUserInput", StringComparison.Ordinal);
}
