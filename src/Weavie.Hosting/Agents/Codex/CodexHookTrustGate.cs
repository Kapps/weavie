using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Blocks Codex hook-trust bypass when it would also run untrusted non-Weavie hooks.</summary>
internal static class CodexHookTrustGate {
	public static void ThrowIfUnsafe(JsonElement hooksList) {
		if (!hooksList.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
			throw new InvalidOperationException("Codex provider blocked: Codex did not return hook metadata.");
		}

		foreach (var entry in data.EnumerateArray()) {
			ThrowIfErrors(entry);
			foreach (var hook in Hooks(entry)) {
				ThrowIfUnsafeHook(hook);
			}
		}
	}

	private static void ThrowIfUnsafeHook(JsonElement hook) {
		if (!hook.GetBoolOrFalse("enabled")
			|| hook.GetBoolOrFalse("isManaged")
			|| hook.GetStringOrEmpty("trustStatus") == "managed"
			|| hook.GetStringOrEmpty("source") == "sessionFlags"
			|| hook.GetStringOrEmpty("trustStatus") == "trusted") {
			return;
		}

		string source = hook.GetStringOrEmpty("source");
		string status = hook.GetStringOrEmpty("trustStatus");
		string command = hook.GetStringOrEmpty("command");
		throw new InvalidOperationException(
			$"Codex provider blocked: Weavie's hook-trust bypass would also run a {status} {source} hook"
			+ (command.Length == 0 ? "." : $" ({command}).")
			+ " Trust or disable that hook in Codex before using native Codex in Weavie.");
	}

	private static void ThrowIfErrors(JsonElement entry) {
		if (!entry.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) {
			return;
		}

		var messages = new List<string>();
		foreach (var error in errors.EnumerateArray()) {
			string message = error.GetStringOrEmpty("message");
			string path = error.GetStringOrEmpty("path");
			if (message.Length > 0 || path.Length > 0) {
				messages.Add(path.Length == 0 ? message : $"{message} ({path})");
			}
		}

		throw new InvalidOperationException(
			"Codex provider blocked: Codex reported hook metadata errors"
			+ (messages.Count == 0 ? "." : ": " + string.Join("; ", messages)));
	}

	private static IEnumerable<JsonElement> Hooks(JsonElement entry) {
		if (!entry.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array) {
			yield break;
		}

		foreach (var hook in hooks.EnumerateArray()) {
			yield return hook;
		}
	}
}
