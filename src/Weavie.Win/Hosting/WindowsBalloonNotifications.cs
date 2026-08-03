using System.ComponentModel;
using System.Runtime.InteropServices;
using Weavie.Core.Sessions;

namespace Weavie.Win.Hosting;

/// <summary>Clickable, silent Shell notifications for Weavie's portable Windows distribution.</summary>
internal sealed class WindowsBalloonNotifications : IDisposable {
	private const int CallbackMessage = 0x8001;
	private const uint NotifyIconVersion4 = 4;
	private const uint NimAdd = 0;
	private const uint NimModify = 1;
	private const uint NimDelete = 2;
	private const uint NimSetVersion = 4;
	private const uint NifMessage = 0x1;
	private const uint NifIcon = 0x2;
	private const uint NifTip = 0x4;
	private const uint NifInfo = 0x10;
	private const uint NiifUser = 0x4;
	private const uint NiifNoSound = 0x10;
	private const uint NiifLargeIcon = 0x20;
	private const uint NinBalloonHide = 0x403;
	private const uint NinBalloonTimeout = 0x404;
	private const uint NinBalloonUserClick = 0x405;

	private readonly Dictionary<string, Entry> _byReplacement = [];
	private readonly Dictionary<uint, Entry> _byIcon = [];
	private readonly NotificationWindow _window;
	private readonly object _gate = new();
	private uint _nextIconId;
	private bool _disposed;

	public WindowsBalloonNotifications() {
		_window = new NotificationWindow(this);
	}

	internal event Action<SystemNotification>? Activated;
	internal event Action<SystemNotification>? Closed;

	internal void Show(SystemNotification notification) {
		ArgumentNullException.ThrowIfNull(notification);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			uint iconId = NextIconId();
			var entry = new Entry(iconId, notification);
			AddIcon(entry);
			if (_byReplacement.Remove(notification.ReplacementId, out var previous)) {
				_byIcon.Remove(previous.IconId);
				DeleteIcon(previous.IconId);
			}
			_byReplacement.Add(notification.ReplacementId, entry);
			_byIcon.Add(iconId, entry);
		}
	}

	internal void Remove(string replacementId) {
		ArgumentException.ThrowIfNullOrEmpty(replacementId);
		lock (_gate) {
			if (_byReplacement.Remove(replacementId, out var entry)) {
				_byIcon.Remove(entry.IconId);
				DeleteIcon(entry.IconId);
			}
		}
	}

	private uint NextIconId() {
		for (int attempt = 0; attempt < ushort.MaxValue; attempt++) {
			_nextIconId = _nextIconId % ushort.MaxValue + 1;
			if (!_byIcon.ContainsKey(_nextIconId)) {
				return _nextIconId;
			}
		}
		throw new InvalidOperationException("Windows has no notification icon identities available.");
	}

	private void AddIcon(Entry entry) {
		IntPtr icon = (AppIcon.Shared
			?? throw new InvalidOperationException("Weavie's embedded Windows icon is missing.")).Handle;
		var data = Data(entry.IconId);
		data._flags = NifMessage | NifIcon | NifTip;
		data._callbackMessage = CallbackMessage;
		data._icon = icon;
		data._tip = "Weavie";
		if (!ShellNotifyIcon(NimAdd, ref data)) {
			throw NativeFailure("add Weavie's notification icon");
		}

		data._timeoutOrVersion = NotifyIconVersion4;
		if (!ShellNotifyIcon(NimSetVersion, ref data)) {
			DeleteIcon(entry.IconId);
			throw NativeFailure("select Windows notification behavior");
		}

		data._flags = NifInfo;
		data._info = Limit(entry.Notification.Body, 255);
		data._infoTitle = Limit(entry.Notification.Title, 63);
		data._infoFlags = NiifUser | NiifNoSound | NiifLargeIcon;
		data._balloonIcon = icon;
		if (!ShellNotifyIcon(NimModify, ref data)) {
			DeleteIcon(entry.IconId);
			throw NativeFailure("show a Windows notification");
		}
	}

	private void OnShellEvent(uint iconId, uint notificationCode) {
		SystemNotification? completed = null;
		bool activated = false;
		lock (_gate) {
			if (!_byIcon.TryGetValue(iconId, out var entry)
				|| notificationCode is not (NinBalloonHide or NinBalloonTimeout or NinBalloonUserClick)) {
				return;
			}
			_byIcon.Remove(iconId);
			_byReplacement.Remove(entry.Notification.ReplacementId);
			DeleteIcon(iconId);
			completed = entry.Notification;
			activated = notificationCode == NinBalloonUserClick;
		}

		if (activated) {
			Activated?.Invoke(completed);
		} else {
			Closed?.Invoke(completed);
		}
	}

	private void OnTaskbarCreated() {
		SystemNotification[] closed;
		lock (_gate) {
			closed = [.. _byIcon.Values.Select(entry => entry.Notification)];
			_byIcon.Clear();
			_byReplacement.Clear();
		}
		foreach (var notification in closed) {
			Closed?.Invoke(notification);
		}
	}

	private NotifyIconData Data(uint iconId) => new() {
		_size = Marshal.SizeOf<NotifyIconData>(),
		_window = _window.Handle,
		_iconId = iconId,
		_tip = string.Empty,
		_info = string.Empty,
		_infoTitle = string.Empty,
	};

	private void DeleteIcon(uint iconId) {
		var data = Data(iconId);
		_ = ShellNotifyIcon(NimDelete, ref data);
	}

	private static string Limit(string value, int maximumLength) =>
		value.Length <= maximumLength ? value : value[..maximumLength];

	private static Win32Exception NativeFailure(string operation) =>
		new(Marshal.GetLastWin32Error(), $"Windows could not {operation}.");

	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			foreach (uint iconId in _byIcon.Keys) {
				DeleteIcon(iconId);
			}
			_byIcon.Clear();
			_byReplacement.Clear();
		}
		_window.Dispose();
	}

	private sealed record Entry(uint IconId, SystemNotification Notification);

	private sealed class NotificationWindow : NativeWindow, IDisposable {
		private readonly WindowsBalloonNotifications _owner;
		private readonly int _taskbarCreated;

		internal NotificationWindow(WindowsBalloonNotifications owner) {
			_owner = owner;
			_taskbarCreated = RegisterWindowMessage("TaskbarCreated");
			if (_taskbarCreated == 0) {
				throw NativeFailure("register for Windows taskbar restarts");
			}
			CreateHandle(new CreateParams { Caption = "Weavie notifications" });
		}

		protected override void WndProc(ref Message message) {
			if (message.Msg == CallbackMessage) {
				ulong value = unchecked((ulong)message.LParam.ToInt64());
				_owner.OnShellEvent((uint)(value >> 16) & 0xffff, (uint)value & 0xffff);
			} else if (message.Msg == _taskbarCreated) {
				_owner.OnTaskbarCreated();
			}
			base.WndProc(ref message);
		}

		public void Dispose() => DestroyHandle();
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData {
		internal int _size;
		internal IntPtr _window;
		internal uint _iconId;
		internal uint _flags;
		internal int _callbackMessage;
		internal IntPtr _icon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		internal string _tip;
		internal uint _state;
		internal uint _stateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		internal string _info;
		internal uint _timeoutOrVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		internal string _infoTitle;
		internal uint _infoFlags;
		internal Guid _item;
		internal IntPtr _balloonIcon;
	}

	[DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

	[DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessage(string message);
}
