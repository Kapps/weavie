using System.Text.Json;

namespace Weavie.Core.Sessions;

/// <summary>The two stages of a revision-guarded session deletion.</summary>
public enum DeleteSessionOperation {
	/// <summary>Inspect the exact target and issue a revision.</summary>
	Preview,

	/// <summary>Confirm one issued revision with explicit loss consent.</summary>
	Confirm,
}

/// <summary>One parsed Delete Session command invocation.</summary>
public sealed record DeleteSessionInvocation {
	/// <summary>The requested protocol stage.</summary>
	public required DeleteSessionOperation Operation { get; init; }

	/// <summary>The target session id; null means the invoking session.</summary>
	public string? Id { get; init; }

	/// <summary>The required confirmation fields for <see cref="DeleteSessionOperation.Confirm"/>.</summary>
	public DeleteSessionConfirmation? Confirmation { get; init; }
}

/// <summary>Strict parsing shared by session-owned and host-owned Delete Session entry points.</summary>
public static class DeleteSessionProtocol {
	/// <summary>Parses a JSON object, rejecting missing or mismatched fields.</summary>
	public static DeleteSessionInvocation Parse(string? argsJson) {
		using var document = JsonDocument.Parse(argsJson ?? "{}");
		return Parse(document.RootElement);
	}

	/// <summary>Parses a JSON object, rejecting missing or mismatched fields.</summary>
	public static DeleteSessionInvocation Parse(JsonElement args) {
		if (args.ValueKind != JsonValueKind.Object) {
			throw new JsonException("Arguments must be a JSON object.");
		}

		string operation = RequiredString(args, "operation");
		string? id = OptionalString(args, "id");
		return operation switch {
			"preview" => new DeleteSessionInvocation {
				Operation = DeleteSessionOperation.Preview,
				Id = id,
			},
			"confirm" => new DeleteSessionInvocation {
				Operation = DeleteSessionOperation.Confirm,
				Id = id,
				Confirmation = new DeleteSessionConfirmation {
					Revision = RequiredString(args, "revision"),
					ForceWorktree = RequiredBoolean(args, "forceWorktree"),
					DiscardDrafts = RequiredBoolean(args, "discardDrafts"),
				},
			},
			_ => throw new JsonException("'operation' must be 'preview' or 'confirm'."),
		};
	}

	private static string RequiredString(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) {
			throw new JsonException($"'{name}' must be a string.");
		}
		string value = property.GetString()!;
		if (string.IsNullOrWhiteSpace(value)) {
			throw new JsonException($"'{name}' must not be blank.");
		}
		return value;
	}

	private static string? OptionalString(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var property)) {
			return null;
		}
		if (property.ValueKind != JsonValueKind.String) {
			throw new JsonException($"'{name}' must be a string.");
		}
		string? value = property.GetString();
		if (string.IsNullOrWhiteSpace(value)) {
			throw new JsonException($"'{name}' must not be blank.");
		}
		return value;
	}

	private static bool RequiredBoolean(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var property)
			|| property.ValueKind is not JsonValueKind.True and not JsonValueKind.False) {
			throw new JsonException($"'{name}' must be a boolean.");
		}
		return property.GetBoolean();
	}
}

/// <summary>The git worktree data a session deletion would remove.</summary>
public sealed record DeleteWorktreeRisk {
	/// <summary><c>clean</c>, <c>untracked</c>, or <c>modified</c>.</summary>
	public required string State { get; init; }

	/// <summary>Whether removing the checkout would leave its commits unreachable.</summary>
	public required bool Branchless { get; init; }

	/// <summary>The first changed paths shown in the confirmation.</summary>
	public required IReadOnlyList<string> ChangedFiles { get; init; }

	/// <summary>The full number of changed paths.</summary>
	public required int ChangedCount { get; init; }
}

/// <summary>One non-empty untitled buffer a session deletion would discard.</summary>
public sealed record ScratchDraftInfo {
	/// <summary>The exact scratch path, used by the owning session's Save flow.</summary>
	public required string Path { get; init; }

	/// <summary>The untitled file name shown to the user.</summary>
	public required string Name { get; init; }
}

/// <summary>An authoritative, revision-bound preview of everything a session deletion would remove.</summary>
public sealed record DeleteSessionPreview {
	/// <summary>Opaque host-issued revision required to confirm this exact loss set.</summary>
	public required string Revision { get; init; }

	/// <summary>The stable session label.</summary>
	public required string Label { get; init; }

	/// <summary>Whether deletion removes a git worktree rather than only the workspace session.</summary>
	public required bool RemovesCheckout { get; init; }

	/// <summary>The independent git worktree risk.</summary>
	public required DeleteWorktreeRisk Worktree { get; init; }

	/// <summary>Every non-empty untitled buffer owned by the target session.</summary>
	public required IReadOnlyList<ScratchDraftInfo> Drafts { get; init; }
}

/// <summary>Explicit consent to one exact <see cref="DeleteSessionPreview"/>.</summary>
public sealed record DeleteSessionConfirmation {
	/// <summary>The preview revision being confirmed.</summary>
	public required string Revision { get; init; }

	/// <summary>Whether the caller consents to losing current worktree changes or unreachable commits.</summary>
	public required bool ForceWorktree { get; init; }

	/// <summary>Whether the caller consents to discarding the previewed untitled buffers.</summary>
	public required bool DiscardDrafts { get; init; }
}
