using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Sessions;

/// <summary>The operating system's authorization state for native app notifications.</summary>
public enum SystemNotificationPermission {
	/// <summary>The current host has no native notification surface.</summary>
	Unavailable,

	/// <summary>The user has not decided; an explicit user action may request authorization.</summary>
	NotDetermined,

	/// <summary>Native notifications are authorized.</summary>
	Granted,

	/// <summary>Native notifications are disabled for the app or user.</summary>
	Denied,
}

/// <summary>One silent native notification with separate replacement and exact-activation identities.</summary>
public sealed record SystemNotification(
	string ReplacementId,
	string ActivationId,
	string Title,
	string Body);

/// <summary>A user activation of a delivered native notification.</summary>
public sealed record SystemNotificationActivation(string Id, string? ActivationToken);

/// <summary>
/// One workspace window's channel into the app-global native notification surface supplied by a platform shell.
/// </summary>
public interface ISystemNotificationChannel {
	/// <summary>Raised when the user activates a delivered notification.</summary>
	event Action<SystemNotificationActivation>? Activated;

	/// <summary>Reads the operating system's current authorization state.</summary>
	Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct);

	/// <summary>Requests authorization from an explicit user action.</summary>
	Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct);

	/// <summary>Shows or replaces a silent native notification.</summary>
	Task ShowAsync(SystemNotification notification, CancellationToken ct);

	/// <summary>Removes a previously shown notification by its replacement identity.</summary>
	Task RemoveAsync(string replacementId, CancellationToken ct);
}

/// <summary>The required notification channel for a host with no native notification surface.</summary>
public sealed class NoopSystemNotificationChannel : ISystemNotificationChannel {
	private NoopSystemNotificationChannel() {
	}

	/// <summary>The shared no-op channel.</summary>
	public static NoopSystemNotificationChannel Instance { get; } = new();

	/// <inheritdoc/>
	public event Action<SystemNotificationActivation>? Activated {
		add { }
		remove { }
	}

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return Task.FromResult(SystemNotificationPermission.Unavailable);
	}

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return Task.FromResult(SystemNotificationPermission.Unavailable);
	}

	/// <inheritdoc/>
	public Task ShowAsync(SystemNotification notification, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(notification);
		ct.ThrowIfCancellationRequested();
		throw new NotSupportedException("This host has no native notification surface.");
	}

	/// <inheritdoc/>
	public Task RemoveAsync(string replacementId, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(replacementId);
		ct.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}
}

/// <summary>
/// Tracks native notification replacement identities and routes each current activation to its owning channel.
/// </summary>
/// <typeparam name="TOwner">The per-window channel type that receives activations.</typeparam>
public sealed class SystemNotificationRoutes<TOwner> where TOwner : class {
	private readonly Dictionary<TOwner, Dictionary<string, Slot>> _byOwner =
		new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<string, Route> _byActivation = new(StringComparer.Ordinal);
	private readonly object _gate = new();

	/// <summary>Begins a delivery while retaining the current route until the caller commits it.</summary>
	public SystemNotificationRouteRegistration Replace(TOwner owner, SystemNotification notification) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(notification);
		lock (_gate) {
			if (!_byOwner.TryGetValue(owner, out var replacements)) {
				replacements = new Dictionary<string, Slot>(StringComparer.Ordinal);
				_byOwner.Add(owner, replacements);
			}

			if (!replacements.TryGetValue(notification.ReplacementId, out var slot)) {
				slot = new Slot(owner, notification.ReplacementId);
				replacements.Add(notification.ReplacementId, slot);
			}
			var route = new Route(slot, notification.ActivationId);
			slot.Pending.Add(route);
			try {
				_byActivation.Add(notification.ActivationId, route);
			} catch {
				slot.Pending.Remove(route);
				RemoveEmptySlot(slot);
				throw;
			}

			return new SystemNotificationRouteRegistration(commit => Complete(route, commit));
		}
	}

	/// <summary>Removes and returns the owner of one current activation.</summary>
	public bool TryTake(string activationId, [NotNullWhen(true)] out TOwner? owner) {
		ArgumentException.ThrowIfNullOrEmpty(activationId);
		lock (_gate) {
			if (!_byActivation.Remove(activationId, out var route)) {
				owner = null;
				return false;
			}
			if (ReferenceEquals(route.Slot.Current, route)) {
				route.Slot.Current = null;
			} else {
				route.Slot.Pending.Remove(route);
			}
			RemoveEmptySlot(route.Slot);
			owner = route.Slot.Owner;
			return true;
		}
	}

	/// <summary>Forgets one owner's current replacement route.</summary>
	public void Forget(TOwner owner, string replacementId) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentException.ThrowIfNullOrEmpty(replacementId);
		lock (_gate) {
			if (_byOwner.TryGetValue(owner, out var replacements)
				&& replacements.Remove(replacementId, out var slot)) {
				ForgetSlot(slot);
				if (replacements.Count == 0) {
					_byOwner.Remove(owner);
				}
			}
		}
	}

	/// <summary>Forgets every route owned by one channel and returns their replacement identities.</summary>
	public string[] ForgetOwner(TOwner owner) {
		ArgumentNullException.ThrowIfNull(owner);
		lock (_gate) {
			if (!_byOwner.Remove(owner, out var replacements)) {
				return [];
			}
			foreach (var slot in replacements.Values) {
				ForgetSlot(slot);
			}
			return [.. replacements.Keys];
		}
	}

	/// <summary>Forgets every route.</summary>
	public void Clear() {
		lock (_gate) {
			_byOwner.Clear();
			_byActivation.Clear();
		}
	}

	private void Complete(Route route, bool commit) {
		lock (_gate) {
			if (!_byActivation.TryGetValue(route.ActivationId, out var active)
				|| !ReferenceEquals(active, route)
				|| !route.Slot.Pending.Remove(route)) {
				return;
			}

			if (!commit) {
				_byActivation.Remove(route.ActivationId);
				RemoveEmptySlot(route.Slot);
				return;
			}

			if (route.Slot.Current is { } previous) {
				_byActivation.Remove(previous.ActivationId);
			}
			route.Slot.Current = route;
		}
	}

	private void RemoveEmptySlot(Slot slot) {
		if (slot.Current is null && slot.Pending.Count == 0
			&& _byOwner.TryGetValue(slot.Owner, out var replacements)
			&& replacements.TryGetValue(slot.ReplacementId, out var current)
			&& ReferenceEquals(current, slot)) {
			replacements.Remove(slot.ReplacementId);
			if (replacements.Count == 0) {
				_byOwner.Remove(slot.Owner);
			}
		}
	}

	private void ForgetSlot(Slot slot) {
		if (slot.Current is { } current) {
			_byActivation.Remove(current.ActivationId);
			slot.Current = null;
		}
		foreach (var pending in slot.Pending) {
			_byActivation.Remove(pending.ActivationId);
		}
		slot.Pending.Clear();
	}

	private sealed class Slot(TOwner owner, string replacementId) {
		internal TOwner Owner { get; } = owner;
		internal string ReplacementId { get; } = replacementId;
		internal Route? Current { get; set; }
		internal HashSet<Route> Pending { get; } = [];
	}

	private sealed record Route(Slot Slot, string ActivationId);
}

/// <summary>A reversible native-route replacement while platform delivery is being committed.</summary>
public sealed class SystemNotificationRouteRegistration {
	private Action<bool>? _complete;

	internal SystemNotificationRouteRegistration(Action<bool> complete) {
		_complete = complete;
	}

	/// <summary>Commits the installed route.</summary>
	public void Commit() => Interlocked.Exchange(ref _complete, null)?.Invoke(true);

	/// <summary>Restores the previous route if this registration is still current.</summary>
	public void Rollback() => Interlocked.Exchange(ref _complete, null)?.Invoke(false);
}
