using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.Json;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// Ownership transfer for the page's single editor surface. Durable session state never lives here; this
// state machine fences and orders only the projection of that state into one browser/WebView page.
public sealed partial class HostCore {
	private readonly string _editorProjectionEpoch = Guid.NewGuid().ToString("n");
	private readonly List<Action> _pendingEditorProjection = [];
	private readonly List<TaskCompletionSource> _pendingEditorProjectionMounts = [];
	private long _editorProjectionRevision;
	private HostSession? _editorProjectionSession;
	private string? _editorProjectionPageId;
	private Task _editorProjectionMount = Task.CompletedTask;
	private EditorProjectionState _editorProjectionState;
	private bool _editorProjectionReplayAll;
	private bool _legacyProjectionWasMounted;

	private enum EditorProjectionState {
		Unbound,
		Offered,
		Mounted,
		Legacy,
	}

	private void DispatchEditorProjection(HostSession session, Action push) {
		if (!ReferenceEquals(_editorProjectionSession, session) || !IsActiveSession(session)) {
			return;
		}

		if (_editorProjectionState == EditorProjectionState.Mounted
			|| (_editorProjectionState == EditorProjectionState.Legacy && _legacyProjectionWasMounted)) {
			push();
		} else if (_editorProjectionState is EditorProjectionState.Offered or EditorProjectionState.Legacy) {
			_pendingEditorProjection.Add(push);
		}
	}

	private void ProjectEditorForInternalSwitch(SessionSlot slot, bool replayAll) {
		switch (_editorProjectionState) {
			case EditorProjectionState.Legacy:
				BeginLegacyEditorProjection(slot.Session!, replayAll);
				break;
			case EditorProjectionState.Offered:
			case EditorProjectionState.Mounted:
				BeginEditorProjection(slot, _editorProjectionPageId!, replayAll);
				break;
			case EditorProjectionState.Unbound:
				BindUnboundEditorProjection(slot.Session!);
				break;
		}
	}

	private void BeginEditorProjection(SessionSlot slot, string pageId, bool replayAll) {
		var session = slot.Session ?? throw new InvalidOperationException("An unloaded session cannot own the editor projection.");
		_editorProjectionMount = BeginEditorProjectionMount();
		session.SetEditorOutputActive(false);
		_editorProjectionSession = session;
		_editorProjectionPageId = pageId;
		_editorProjectionState = EditorProjectionState.Offered;
		_editorProjectionReplayAll = replayAll;
		_legacyProjectionWasMounted = false;
		_pendingEditorProjection.Clear();
		long revision = ++_editorProjectionRevision;
		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			session.EditorSession,
			session.FileSystem,
			session.WorkspaceRoot,
			session.Id,
			slot.Id,
			_editorProjectionEpoch,
			revision,
			pageId,
			Log));
	}

	private void BeginLegacyEditorProjection(HostSession session, bool replayAll) {
		_editorProjectionMount = BeginEditorProjectionMount();
		bool alreadyMounted = _editorProjectionState == EditorProjectionState.Legacy
			&& ReferenceEquals(_editorProjectionSession, session);
		if (!alreadyMounted) {
			session.SetEditorOutputActive(false);
		}

		_editorProjectionSession = session;
		_editorProjectionPageId = null;
		_editorProjectionState = EditorProjectionState.Legacy;
		_editorProjectionReplayAll = replayAll;
		_legacyProjectionWasMounted = alreadyMounted;
		_pendingEditorProjection.Clear();
		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			session.EditorSession,
			session.FileSystem,
			session.WorkspaceRoot,
			session.Id,
			Log));

		if (replayAll) {
			if (alreadyMounted) {
				PushReviewStateToWeb();
				session.EditorChannel.Replay();
			}

			return;
		}

		_bridge.PostToWeb(ChangeMessages.TurnReset());
		CompleteEditorProjectionMount(session);
	}

	private void BindUnboundEditorProjection(HostSession session) {
		session.SetEditorOutputActive(false);
		_editorProjectionSession = session;
		_editorProjectionPageId = null;
		_editorProjectionState = EditorProjectionState.Unbound;
		_editorProjectionReplayAll = false;
		_legacyProjectionWasMounted = false;
		_pendingEditorProjection.Clear();
		CompleteEditorProjectionMounts();
	}

	private void MountEditorProjection(JsonElement root) {
		string? sessionId = root.GetStringOrNull("sessionId");
		long revision = ProjectionRevision(root);
		if (!MatchesEditorProjection(root) || _editorProjectionSession is not { } session) {
			Log($"[weavie] rejected stale editor projection mount for session '{sessionId ?? ""}' revision {revision}");
			return;
		}

		if (_editorProjectionState == EditorProjectionState.Mounted) {
			PushReviewStateToWeb();
			session.EditorChannel.Replay();
			return;
		}

		CompleteEditorProjectionMount(session);
	}

	private void MountLegacyEditorProjection() {
		if (_editorProjectionState == EditorProjectionState.Legacy
			&& _editorProjectionReplayAll
			&& _editorProjectionSession is { } session) {
			CompleteEditorProjectionMount(session);
		}
	}

	private void CompleteEditorProjectionMount(HostSession session) {
		bool legacy = _editorProjectionState == EditorProjectionState.Legacy;
		if (!legacy) {
			_editorProjectionState = EditorProjectionState.Mounted;
		}

		var pending = _pendingEditorProjection.ToArray();
		_pendingEditorProjection.Clear();
		foreach (var push in pending) {
			push();
		}

		session.SetEditorOutputActive(true);
		if (_editorProjectionReplayAll) {
			PushReviewStateToWeb();
		} else {
			PushIncomingReviewState();
			SurfaceActiveReviewOnSwitch();
		}

		if (legacy && _legacyProjectionWasMounted) {
			session.EditorChannel.Replay();
		}

		_legacyProjectionWasMounted = legacy;
		CompleteEditorProjectionMounts();
	}

	private void ReleaseEditorProjection(JsonElement root) {
		if (!MatchesEditorProjectionPage(root)) {
			return;
		}

		UnbindEditorProjection();
	}

	private void OnEditorPageDisconnected(string pageId) {
		if (string.Equals(pageId, _editorProjectionPageId, StringComparison.Ordinal)) {
			UnbindEditorProjection();
		}
	}

	private void UnbindEditorProjection() {
		var session = _editorProjectionSession;
		_editorProjectionState = EditorProjectionState.Unbound;
		_editorProjectionPageId = null;
		_editorProjectionReplayAll = false;
		_legacyProjectionWasMounted = false;
		_pendingEditorProjection.Clear();
		session?.SetEditorOutputActive(false);
		CompleteEditorProjectionMounts();
	}

	private Task BeginEditorProjectionMount() {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingEditorProjectionMounts.Add(completion);
		return completion.Task;
	}

	private void CompleteEditorProjectionMounts() {
		foreach (var completion in _pendingEditorProjectionMounts) {
			completion.TrySetResult();
		}
		_pendingEditorProjectionMounts.Clear();
	}

	private bool MatchesEditorProjection(JsonElement root) =>
		(_editorProjectionState is EditorProjectionState.Offered or EditorProjectionState.Mounted)
		&& _editorProjectionSession is { } session
		&& ReferenceEquals(session, _session)
		&& string.Equals(root.GetStringOrNull("sessionId"), session.Id, StringComparison.Ordinal)
		&& MatchesEditorProjectionPage(root)
		&& ProjectionRevision(root) == _editorProjectionRevision;

	private bool MatchesEditorProjectionPage(JsonElement root) =>
		(_editorProjectionState is EditorProjectionState.Offered or EditorProjectionState.Mounted)
		&& string.Equals(root.GetStringOrNull("projectionEpoch"), _editorProjectionEpoch, StringComparison.Ordinal)
		&& string.Equals(root.GetStringOrNull("projectionPageId"), _editorProjectionPageId, StringComparison.Ordinal);

	private static long ProjectionRevision(JsonElement root) =>
		root.TryGetProperty("projectionRevision", out var revisionElement)
		&& revisionElement.TryGetInt64(out long revision)
			? revision
			: -1;
}
