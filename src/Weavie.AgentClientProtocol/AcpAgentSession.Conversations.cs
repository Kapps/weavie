using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession : IStructuredAgentConversations {
	private bool _clearing;

	/// <inheritdoc/>
	public Task<IAgentConversation> OpenFreshAsync(AgentConversationBinding binding, CancellationToken ct) =>
		OpenOwnedAsync(binding, primary => NewChild(primary, fork: false, binding), ct);

	/// <inheritdoc/>
	public Task<IAgentConversation> OpenForkAsync(AgentConversationBinding binding, CancellationToken ct) =>
		OpenOwnedAsync(binding, primary => NewChild(primary, fork: true, binding), ct);

	/// <inheritdoc/>
	public async Task<IAgentConversation?> RestoreAsync(AgentConversationBinding binding, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(binding);
		AcpConversationState? kept;
		lock (_turnTransitionGate) {
			kept = binding.Owner is { } key ? _sideConversations.Values.FirstOrDefault(state => state.Owner == key && Continuable(state)) : null;
			if (kept is null) {
				Forget(owner => owner == binding.Owner);
				return null;
			}
		}
		return await OpenOwnedAsync(binding,
			_ => new(AcpConversationHandoff.Fresh(kept), AcpConversationOpening.Continue, AcpConversationRole.Independent), ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public void Forget(Func<AgentConversationKey, bool> owners) {
		ArgumentNullException.ThrowIfNull(owners);
		lock (_turnTransitionGate) {
			var live = _runtime.Conversations.Select(conversation => conversation.ConversationId).ToHashSet(StringComparer.Ordinal);
			foreach (var state in _sideConversations.Values.Where(state => state.Owner is { } owner && owners(owner) && !live.Contains(state.ConversationId)).ToArray()) {
				ForgetKept(state);
			}
		}
	}

	private void ForgetKept(AcpConversationState state) {
		_sideConversations.Remove(state.ConversationId);
		_sessions.Forget(_definition.Id, _context.Workspace, state.ConversationId);
		_primary.Publish(SideMessage(Side(state), "side-conversation-closed", "closed"));
	}

	// A new conversation replaces any its owner kept before.
	private AcpConversationSpec NewChild(AcpConversation primary, bool fork, AgentConversationBinding binding) {
		if (binding.Owner is { } key) Forget(owner => owner == key);
		var spec = ChildSpec(primary, fork, binding.Title, AcpConversationRole.Independent);
		return spec with { Seed = spec.Seed with { Continuation = spec.Seed.Continuation with { Owner = binding.Owner } } };
	}

	private async Task<IAgentConversation> OpenOwnedAsync(AgentConversationBinding binding, Func<AcpConversation, AcpConversationSpec> spec, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(binding);
		AcpConversationHandle? handle = null;
		try {
			ct.ThrowIfCancellationRequested();
			AcpConversation primary;
			lock (_turnTransitionGate) {
				EnsureConversationAdmission();
				primary = _primary;
			}
			await primary.WaitUntilReadyAsync(ct).ConfigureAwait(false);
			var overrides = binding.ControlHold.Values();
			lock (_turnTransitionGate) {
				EnsureConversationAdmission();
				ct.ThrowIfCancellationRequested();
				if (!primary.Live || !primary.Ready) throw new InvalidOperationException("The conversation was replaced while opening owned work.");
				if (!_runtime.Features.Close) throw new InvalidOperationException($"{_definition.Name} cannot close independently owned conversations.");
				var opening = spec(primary);
				if (opening.Opening is ForkFromOpening) EnsureSideConversationSupport(primary);
				var controls = primary.Snapshot.Axes.ToDictionary(axis => axis.Id, axis => axis.Value, StringComparer.Ordinal);
				foreach (var (axis, value) in overrides) controls[axis] = value;
				handle = StartOwned(opening, binding with { ControlHold = new CapturedControls(controls) });
			}
			await handle.Conversation.WaitUntilReadyAsync(ct).ConfigureAwait(false);
			return handle;
		} catch {
			if (handle is null) await binding.Tools.DisposeAsync().ConfigureAwait(false);
			else {
				var closing = handle.DisposeAsync();
				if (!ct.IsCancellationRequested) await closing.ConfigureAwait(false);
			}
			throw;
		}
	}

	private static AcpConversationSpec ChildSpec(AcpConversation primary, bool fork, string prompt, AcpConversationRole role) => new(
		AcpConversationHandoff.Fresh(NewContinuation(Guid.NewGuid().ToString("N"), primary.TurnNumber, prompt, fork && primary.GuidanceSent)),
		fork && primary.TurnNumber > 0 ? new ForkFromOpening(primary, MessageId: null) : AcpConversationOpening.Continue, role);

	private AcpConversationHandle StartOwned(AcpConversationSpec spec, AgentConversationBinding binding) {
		lock (_turnTransitionGate) {
			EnsureConversationAdmission();
			var side = Side(spec.Seed.Continuation);
			AcpConversationPort port = spec.Role == AcpConversationRole.Side ? new SidePort(this, side) : new IndependentPort(this, side);
			var conversation = _runtime.Create(spec, port, binding);
			var handle = new AcpConversationHandle(_runtime, conversation, Output.View(_definition.Id, side.ConversationId));
			if (spec.Role == AcpConversationRole.Side) _sides.Add(side.ConversationId, handle);
			try {
				if (spec.Seed.Continuation.SessionId is null) conversation.Publish(SideMarker(side, "forking") with {
					Summary = conversation.Independent ? binding.Title : null,
				});
				_runtime.Open(conversation, _runtime.Process!);
			} catch (Exception error) {
				conversation.Terminate(error);
			}
			return handle;
		}
	}

	private sealed class CapturedControls(IReadOnlyDictionary<string, string> controls) : IAgentControlHold {
		public IReadOnlyDictionary<string, string> Values() => controls;
	}

	private void EnsureConversationAdmission() {
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_primary.Rewinding) throw new InvalidOperationException("Wait for the rewind to finish.");
		if (_clearing) throw new InvalidOperationException("Wait for the conversation to clear.");
		if (_storageFailed) throw new InvalidOperationException("The conversation archive has failed. Restart the session before opening new work.");
	}

	// Independently owned work is kept for its owner to restore, as an aside is for a reply, until the owner closes it.
	private sealed class IndependentPort(AcpAgentSession owner, SideConversation side)
		: NestedPort(owner, owner._runtime, side.ConversationId, side.AnchorTurnNumber) {
		private bool _kept = true;

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			if (!_kept || state.Owner is null) {
				Owner._sessions.Append(Owner._definition.Id, Owner._context.Workspace, messages);
				return;
			}
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);
			Owner._sideConversations[state.ConversationId] = state;
		}

		protected override void OnForget(AcpConversationState state) {
			_kept = false;
			Owner.ForgetKept(state);
		}

		protected override bool OnFail(Exception error) =>
			error is IOException or AcpProtocolException or AcpSessionStoreException && Runtime.Fail(error);

		protected override void OnSettled(AcpConversationState state, bool terminal) {
			if (terminal) OnEmit(state, new AgentPaneMessage { Type = "side-conversation-failed", ProviderId = Runtime.Definition.Id, Status = "failed" });
		}

	}
}
