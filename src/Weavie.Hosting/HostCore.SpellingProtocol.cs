using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Configuration;
using Weavie.Core.Json;
using Weavie.Core.Spelling;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private static readonly JsonSerializerOptions SpellJsonOptions = new() {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private sealed record SpellDocumentChangedRequest(
		string Path,
		string Content,
		long DocumentRevision);

	private sealed record SpellSuggestRequest(string RequestId, string Word);

	private sealed record SpellAddWordRequest(string RequestId, string Word, string Scope);

	private readonly record struct SpellDiagnostic(
		int Line,
		int StartColumn,
		int EndColumn,
		string Word);

	private readonly record struct SpellProjection(
		string SessionId,
		string? Epoch,
		long Revision,
		string? PageId);

	private sealed record SpellDiagnosticsEnvelope(
		string Type,
		string Path,
		long DocumentRevision,
		string Locale,
		IReadOnlyList<SpellDiagnostic> Issues,
		string? Error,
		string SessionId,
		string? ProjectionEpoch,
		long ProjectionRevision,
		string? ProjectionPageId);

	private static bool TryReadSpellDocumentChanged(
		JsonElement root,
		[NotNullWhen(true)] out SpellDocumentChangedRequest? request,
		out string error) {
		request = null;
		if (!TryRequiredString(root, "path", out string? path)
			|| !TryString(root, "content", out string? content)
			|| !root.TryGetProperty("documentRevision", out var documentRevisionElement)
			|| !documentRevisionElement.TryGetInt64(out long documentRevision)
			|| documentRevision < 0) {
			error = "The spell document update is missing a required field.";
			return false;
		}

		request = new SpellDocumentChangedRequest(path, content, documentRevision);
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

	private void PostSpellDiagnostics(
		SpellDocumentChangedRequest request,
		SpellProjection projection,
		string locale,
		IReadOnlyList<SpellDiagnostic> diagnostics,
		string? error) {
		_bridge.PostToWeb(JsonSerializer.Serialize(
			new SpellDiagnosticsEnvelope(
				"spell-diagnostics",
				request.Path,
				request.DocumentRevision,
				locale,
				diagnostics,
				error,
				projection.SessionId,
				projection.Epoch,
				projection.Revision,
				projection.PageId),
			SpellJsonOptions));
	}

	private void PostSpellDictionaryChanged(HostSession session) {
		if (!TryCurrentSpellProjection(session, out var projection)) {
			return;
		}

		DispatchEditorProjection(session, () => {
			if (HasCurrentSpellProjection(session, projection)) {
				_bridge.PostToWeb(JsonSerializer.Serialize(new {
					type = "spell-dictionary-changed",
					sessionId = projection.SessionId,
					projectionEpoch = projection.Epoch,
					projectionRevision = projection.Revision,
					projectionPageId = projection.PageId,
				}, SpellJsonOptions));
			}
		});
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
		DispatchEditorProjection(session, () => {
			if (HasCurrentSpellProjection(session, projection)) {
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
		});
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
		DispatchEditorProjection(session, () => {
			if (HasCurrentSpellProjection(session, projection)) {
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
		});
	}

	private static SpellProjection ProjectionFor(HostSession session, JsonElement root) => new(
		session.Id,
		root.GetStringOrNull("projectionEpoch"),
		ProjectionRevision(root),
		root.GetStringOrNull("projectionPageId"));

	private bool TryCurrentSpellProjection(HostSession session, out SpellProjection projection) {
		if (!ReferenceEquals(_editorProjectionSession, session) || !IsActiveSession(session)) {
			projection = new SpellProjection(string.Empty, null, -1, null);
			return false;
		}

		if (_editorProjectionState is EditorProjectionState.Offered or EditorProjectionState.Mounted) {
			projection = new SpellProjection(
				session.Id,
				_editorProjectionEpoch,
				_editorProjectionRevision,
				_editorProjectionPageId);
			return true;
		}

		if (_editorProjectionState == EditorProjectionState.Legacy) {
			projection = new SpellProjection(session.Id, null, -1, null);
			return true;
		}

		projection = new SpellProjection(string.Empty, null, -1, null);
		return false;
	}

	private bool HasCurrentSpellProjection(HostSession session, SpellProjection projection) =>
		TryCurrentSpellProjection(session, out var current) && current == projection;
}
