using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Session attention: classifies every loaded session's status transitions (turn complete / needs input /
// failed) into owned events the web presents as a sound + OS notification — never selection-gated,
// a background session's ping is the whole point. See docs/specs/session-attention.md.
public sealed partial class HostCore {
	private readonly Dictionary<NotificationReplacement, string> _notificationReplacements = [];
	private readonly Dictionary<string, NotificationRoute> _notificationRoutes = [];
	private readonly object _notificationGate = new();
	private readonly SemaphoreSlim _notificationOperations = new(1, 1);
	private MessageFeatureChannel? _notificationFeature;

	private void WireSystemNotificationMessages() {
		_notificationFeature = _messages.Host.Feature("notifications");
		_notificationFeature.Handle<NotificationEmpty, NotificationPermissionMessage>(
			"permission",
			async (_, ct) => new NotificationPermissionMessage(
				PermissionName(await _platform.Notifications.GetPermissionAsync(ct).ConfigureAwait(false))));
		_notificationFeature.Handle<NotificationEmpty, NotificationPermissionMessage>(
			"requestPermission",
			async (_, ct) => new NotificationPermissionMessage(
				PermissionName(await _platform.Notifications.RequestPermissionAsync(ct).ConfigureAwait(false))));
		_notificationFeature.HandleOwned<NotificationShowMessage, NotificationShownMessage>(
			"show",
			ShowSystemNotificationAsync);
		_platform.Notifications.Activated += OnSystemNotificationActivated;
		_messages.Host.PeerDisconnected += OnNotificationPeerDisconnected;
	}

	/// <summary>Subscribes a session's status transitions and publishes each attention-worthy event on its bus.</summary>
	private void WireAttention(HostSession session) {
		// The machine delivers Changed serially (its delivery gate), so this closure-tracked previous status
		// can't race its own handler.
		var previous = session.Status.Status;
		session.Status.Changed += status => {
			var prior = previous;
			previous = status;
			if (AttentionRules.Classify(prior, status) is { } kind) {
				PostForSession(session, () => PostSessionAttention(session, kind));
			}
		};
	}

	private void PostSessionAttention(HostSession session, AttentionKind kind) {
		session.Bus.Feature("attention").Publish("raised", new {
			label = session.DisplayLabel,
			kind = AttentionRules.WireName(kind),
			body = AttentionRules.NotificationBody(kind),
		});
	}

	private async Task<NotificationShownMessage> ShowSystemNotificationAsync(
		NotificationShowMessage message,
		MessagePeer peer,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrWhiteSpace(message.BackendId);
		ArgumentException.ThrowIfNullOrWhiteSpace(message.Label);
		var kind = AttentionRules.FromWireName(message.Kind);
		await _notificationOperations.WaitAsync(ct).ConfigureAwait(false);
		try {
			var key = new NotificationReplacement(peer, message.BackendId, message.Address.Slot);
			string replacementId;
			string activationId = Guid.NewGuid().ToString("n");
			NotificationRoute? previous = null;
			lock (_notificationGate) {
				if (!_notificationReplacements.TryGetValue(key, out replacementId!)) {
					replacementId = Guid.NewGuid().ToString("n");
					_notificationReplacements.Add(key, replacementId);
				}
				previous = _notificationRoutes.Values.FirstOrDefault(route => route.ReplacementId == replacementId);
				_notificationRoutes.Add(
					activationId,
					new NotificationRoute(
						activationId,
						replacementId,
						peer,
						message.BackendId,
						message.Address));
			}

			try {
				await _platform.Notifications.ShowAsync(
					new SystemNotification(
						replacementId,
						activationId,
						message.Label,
						AttentionRules.NotificationBody(kind)),
					ct).ConfigureAwait(false);
			} catch {
				lock (_notificationGate) {
					_notificationRoutes.Remove(activationId);
				}
				throw;
			}
			lock (_notificationGate) {
				if (previous is not null
					&& _notificationRoutes.TryGetValue(previous.ActivationId, out var current)
					&& ReferenceEquals(current, previous)) {
					_notificationRoutes.Remove(previous.ActivationId);
				}
			}

			return new NotificationShownMessage(true);
		} finally {
			_notificationOperations.Release();
		}
	}

	private void OnSystemNotificationActivated(SystemNotificationActivation activation) =>
		_ui.Post(() => ActivateSystemNotification(activation));

	private void ActivateSystemNotification(SystemNotificationActivation activation) {
		NotificationRoute? route;
		lock (_notificationGate) {
			_notificationRoutes.Remove(activation.Id, out route);
		}
		if (route is null || _notificationFeature is null) {
			return;
		}

		_platform.ActivateWindow(activation.ActivationToken);
		_notificationFeature.Target(route.Peer).Publish("activated", new {
			backendId = route.BackendId,
			address = route.Address,
		});
	}

	private void OnNotificationPeerDisconnected(MessagePeer peer) =>
		_ui.Post(() => _ = RemovePeerNotificationsAfterDisconnectAsync(peer));

	private async Task RemovePeerNotificationsAfterDisconnectAsync(MessagePeer peer) {
		try {
			await RemovePeerNotificationsAsync(peer, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			Log($"[notifications] failed to clear a disconnected page's notifications: {ex.Message}");
		}
	}

	private async Task RemovePeerNotificationsAsync(MessagePeer peer, CancellationToken ct) {
		await _notificationOperations.WaitAsync(ct).ConfigureAwait(false);
		try {
			string[] replacementIds;
			lock (_notificationGate) {
				replacementIds = [.. _notificationReplacements
					.Where(entry => ReferenceEquals(entry.Key.Peer, peer))
					.Select(entry => entry.Value)];
				foreach (var key in _notificationReplacements.Keys
					.Where(key => ReferenceEquals(key.Peer, peer)).ToArray()) {
					_notificationReplacements.Remove(key);
				}
				foreach (string activationId in _notificationRoutes
					.Where(entry => ReferenceEquals(entry.Value.Peer, peer))
					.Select(entry => entry.Key)
					.ToArray()) {
					_notificationRoutes.Remove(activationId);
				}
			}
			await RemoveNativeNotificationsAsync(replacementIds, ct).ConfigureAwait(false);
		} finally {
			_notificationOperations.Release();
		}
	}

	private async Task DisposeSystemNotificationsAsync() {
		_platform.Notifications.Activated -= OnSystemNotificationActivated;
		_messages.Host.PeerDisconnected -= OnNotificationPeerDisconnected;
		await _notificationOperations.WaitAsync().ConfigureAwait(false);
		try {
			string[] replacementIds;
			lock (_notificationGate) {
				replacementIds = [.. _notificationReplacements.Values.Distinct(StringComparer.Ordinal)];
				_notificationReplacements.Clear();
				_notificationRoutes.Clear();
			}
			await RemoveNativeNotificationsAsync(replacementIds, CancellationToken.None).ConfigureAwait(false);
		} finally {
			_notificationOperations.Release();
		}
	}

	private async Task RemoveNativeNotificationsAsync(IEnumerable<string> replacementIds, CancellationToken ct) {
		List<Exception>? failures = null;
		foreach (string replacementId in replacementIds) {
			try {
				await _platform.Notifications.RemoveAsync(replacementId, ct).ConfigureAwait(false);
			} catch (Exception ex) {
				(failures ??= []).Add(ex);
			}
		}
		if (failures is not null) {
			throw new AggregateException("One or more native notifications could not be removed.", failures);
		}
	}

	private static string PermissionName(SystemNotificationPermission permission) => permission switch {
		SystemNotificationPermission.Unavailable => "unavailable",
		SystemNotificationPermission.NotDetermined => "notDetermined",
		SystemNotificationPermission.Granted => "granted",
		SystemNotificationPermission.Denied => "denied",
		_ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "unhandled notification permission"),
	};

	private sealed record NotificationEmpty;

	private sealed record NotificationPermissionMessage(string Permission);

	private sealed record NotificationShowMessage(
		string BackendId,
		SessionAddress Address,
		string Label,
		string Kind);

	private sealed record NotificationShownMessage(bool Shown);

	private sealed record NotificationReplacement(MessagePeer Peer, string BackendId, string Slot);

	private sealed record NotificationRoute(
		string ActivationId,
		string ReplacementId,
		MessagePeer Peer,
		string BackendId,
		SessionAddress Address);
}
