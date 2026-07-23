using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Changes;
using Weavie.Core.Configuration;
using Weavie.Core.Json;
using Weavie.Core.Spelling;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private static readonly JsonSerializerOptions SpellJsonOptions = new() {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private sealed record SpellCheckRequest(
		string RequestId,
		string ModelEpoch,
		string LanguageId,
		IReadOnlyList<SpellCheckLine> Lines);

	private sealed record SpellSuggestRequest(string RequestId, string Word);

	private sealed record SpellAddWordRequest(string RequestId, string Word, string Scope);

	private sealed record SpellRestoreRequest(string RequestId, string ModelEpoch, string Path);

	private readonly record struct SpellProjection(
		string SessionId,
		string? Epoch,
		long Revision,
		string? PageId);

	private static bool TryReadSpellCheckRequest(
		JsonElement root,
		[NotNullWhen(true)] out SpellCheckRequest? request,
		out string error) {
		request = null;
		if (!TryRequiredString(root, "requestId", out string? requestId)
			|| !TryRequiredString(root, "modelEpoch", out string? modelEpoch)
			|| !TryRequiredString(root, "path", out _)
			|| !TryRequiredString(root, "languageId", out string? languageId)) {
			error = "The spell-check request is missing a required field.";
			return false;
		}

		if (!root.TryGetProperty("lines", out var linesElement)
			|| linesElement.ValueKind != JsonValueKind.Array) {
			error = "The spell-check request has no line list.";
			return false;
		}

		var anchors = new HashSet<string>(StringComparer.Ordinal);
		var lines = new List<SpellCheckLine>();
		foreach (var line in linesElement.EnumerateArray()) {
			if (!TryRequiredString(line, "anchorId", out string? anchorId)
				|| !TryString(line, "text", out string? text)) {
				error = "A spell-check line is malformed.";
				return false;
			}

			if (!anchors.Add(anchorId)) {
				error = "A spell-check request repeated a line anchor.";
				return false;
			}

			lines.Add(new SpellCheckLine(anchorId, text));
		}

		request = new SpellCheckRequest(requestId, modelEpoch, languageId, lines);
		error = string.Empty;
		return true;
	}

	private static bool TryReadSpellSuggestRequest(
		JsonElement root,
		[NotNullWhen(true)] out SpellSuggestRequest? request,
		out string error) {
		request = null;
		if (!TryRequiredString(root, "requestId", out string? requestId)
			|| !TryRequiredString(root, "word", out string? word)) {
			error = "The spelling suggestion request is missing a required field.";
			return false;
		}

		request = new SpellSuggestRequest(requestId, word);
		error = string.Empty;
		return true;
	}

	private static bool TryReadSpellAddWordRequest(
		JsonElement root,
		[NotNullWhen(true)] out SpellAddWordRequest? request,
		out string error) {
		request = null;
		if (!TryRequiredString(root, "requestId", out string? requestId)
			|| !TryRequiredString(root, "word", out string? word)
			|| !TryRequiredString(root, "scope", out string? scope)) {
			error = "The dictionary request is missing a required field.";
			return false;
		}

		if (scope is not "project" and not "user") {
			error = "The dictionary scope must be 'project' or 'user'.";
			return false;
		}

		request = new SpellAddWordRequest(requestId, word, scope);
		error = string.Empty;
		return true;
	}

	private static bool TryReadSpellRestoreRequest(
		JsonElement root,
		[NotNullWhen(true)] out SpellRestoreRequest? request,
		out string error) {
		request = null;
		if (!TryRequiredString(root, "requestId", out string? requestId)
			|| !TryRequiredString(root, "modelEpoch", out string? modelEpoch)
			|| !TryRequiredString(root, "path", out string? path)) {
			error = "The spelling restore request is missing a required field.";
			return false;
		}

		request = new SpellRestoreRequest(requestId, modelEpoch, path);
		error = string.Empty;
		return true;
	}

	private static bool TryRequiredString(
		JsonElement element,
		string name,
		[NotNullWhen(true)] out string? value) =>
		TryString(element, name, out value) && !string.IsNullOrWhiteSpace(value);

	private static bool TryString(JsonElement element, string name, [NotNullWhen(true)] out string? value) {
		if (element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(name, out var property)
			&& property.ValueKind == JsonValueKind.String) {
			value = property.GetString();
			return value is not null;
		}

		value = null;
		return false;
	}

	private void PostSpellCheckError(HostSession session, JsonElement root, string error) {
		var request = new SpellCheckRequest(
			root.GetStringOrEmpty("requestId"),
			root.GetStringOrEmpty("modelEpoch"),
			root.GetStringOrEmpty("languageId"),
			[]);
		PostSpellCheckResult(
			session,
			root,
			request,
			_settings.RequireString(SpellSettings.Locale),
			[],
			error);
	}

	private void PostSpellCheckResult(
		HostSession session,
		JsonElement root,
		SpellCheckRequest request,
		string locale,
		IReadOnlyList<SpellCheckLineResult> lines,
		string? error) {
		var projection = ProjectionFor(session, root);
		var issues = lines.SelectMany(line => line.Issues.Select(issue => new {
			anchorId = line.AnchorId,
			startColumn = issue.Start + 1,
			endColumn = issue.Start + issue.Length + 1,
			word = issue.Word,
		}));
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "spell-check-result",
			requestId = request.RequestId,
			modelEpoch = request.ModelEpoch,
			locale,
			issues,
			error,
			sessionId = projection.SessionId,
			projectionEpoch = projection.Epoch,
			projectionRevision = projection.Revision,
			projectionPageId = projection.PageId,
		}, SpellJsonOptions));
	}

	private void PostSpellSuggestError(HostSession session, JsonElement root, string error) {
		var request = new SpellSuggestRequest(root.GetStringOrEmpty("requestId"), root.GetStringOrEmpty("word"));
		PostSpellSuggestResult(session, root, request, _settings.RequireString(SpellSettings.Locale), [], error);
	}

	private void PostSpellSuggestResult(
		HostSession session,
		JsonElement root,
		SpellSuggestRequest request,
		string locale,
		IReadOnlyList<string> suggestions,
		string? error) {
		var projection = ProjectionFor(session, root);
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "spell-suggest-result",
			requestId = request.RequestId,
			locale,
			suggestions,
			error,
			sessionId = projection.SessionId,
			projectionEpoch = projection.Epoch,
			projectionRevision = projection.Revision,
			projectionPageId = projection.PageId,
		}, SpellJsonOptions));
	}

	private void PostSpellAddWordError(HostSession session, JsonElement root, string error) {
		var request = new SpellAddWordRequest(
			root.GetStringOrEmpty("requestId"),
			root.GetStringOrEmpty("word"),
			root.GetStringOrEmpty("scope"));
		PostSpellAddWordResult(session, root, request, ok: false, error);
	}

	private void PostSpellAddWordResult(
		HostSession session,
		JsonElement root,
		SpellAddWordRequest request,
		bool ok,
		string? error) {
		var projection = ProjectionFor(session, root);
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "spell-add-word-result",
			requestId = request.RequestId,
			word = request.Word,
			scope = request.Scope,
			ok,
			error,
			sessionId = projection.SessionId,
			projectionEpoch = projection.Epoch,
			projectionRevision = projection.Revision,
			projectionPageId = projection.PageId,
		}, SpellJsonOptions));
	}

	private void PostSpellRestoreResult(
		HostSession session,
		JsonElement root,
		SpellRestoreRequest request,
		AuthoredLineSnapshot? snapshot) {
		var projection = ProjectionFor(session, root);
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "spell-restore-result",
			requestId = request.RequestId,
			modelEpoch = request.ModelEpoch,
			version = snapshot?.Version ?? 0,
			lines = snapshot?.Lines.Select(line => new { line = line.Number, text = line.Text }) ?? [],
			sessionId = projection.SessionId,
			projectionEpoch = projection.Epoch,
			projectionRevision = projection.Revision,
			projectionPageId = projection.PageId,
		}, SpellJsonOptions));
	}

	private void PostSpellDictionaryChanged(HostSession session, string scope) {
		if (!TryCurrentSpellProjection(session, out var projection)) {
			return;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "spell-dictionary-changed",
			scope,
			sessionId = projection.SessionId,
			projectionEpoch = projection.Epoch,
			projectionRevision = projection.Revision,
			projectionPageId = projection.PageId,
		}, SpellJsonOptions));
	}

	private static SpellProjection ProjectionFor(HostSession session, JsonElement root) => new(
		session.Id,
		root.GetStringOrNull("projectionEpoch"),
		ProjectionRevision(root),
		root.GetStringOrNull("projectionPageId"));

	private bool TryCurrentSpellProjection(HostSession session, out SpellProjection projection) {
		if (_editorProjectionState == EditorProjectionState.Mounted
			&& ReferenceEquals(_editorProjectionSession, session)
			&& IsActiveSession(session)) {
			projection = new SpellProjection(session.Id, _editorProjectionEpoch, _editorProjectionRevision, _editorProjectionPageId);
			return true;
		}

		projection = new SpellProjection(string.Empty, null, -1, null);
		return false;
	}
}
