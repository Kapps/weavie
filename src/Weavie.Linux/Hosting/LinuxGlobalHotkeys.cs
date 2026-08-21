using Weavie.Core.Commands;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

internal sealed class LinuxGlobalHotkeys : IGlobalHotkeyRegistrar {
	private readonly ILinuxGlobalHotkeyBackend _backend;
	private readonly Action<string> _applyActivationToken;
	private readonly int _uiThreadId;
	private bool _disposed;

	internal LinuxGlobalHotkeys(Action<string> applyActivationToken) {
		ArgumentNullException.ThrowIfNull(applyActivationToken);
		_applyActivationToken = applyActivationToken;
		_uiThreadId = Environment.CurrentManagedThreadId;
		IntPtr display = Gdk.gdk_display_get_default();
		if (display == IntPtr.Zero) {
			throw new InvalidOperationException("GTK has no default display for global-hotkey registration.");
		}

		_backend = Gdk.GetDisplayBackend(display) switch {
			Gdk.DisplayBackend.Wayland => new WaylandGlobalHotkeys(new XdgGlobalShortcutsPortal()),
			Gdk.DisplayBackend.X11 => new X11GlobalHotkeys(),
			_ => new UnsupportedLinuxGlobalHotkeys(),
		};
		_backend.Pressed += OnPressed;
		_backend.Log += OnLog;
	}

	public event Action<GlobalHotkey>? Pressed;

	public event Action<string>? Log;

	public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) {
		ArgumentNullException.ThrowIfNull(hotkeys);
		if (_disposed) {
			return;
		}
		var snapshot = hotkeys.ToArray();
		RunOnMain(() => _backend.Apply(snapshot));
	}

	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		RunOnMain(() => {
			_backend.Pressed -= OnPressed;
			_backend.Log -= OnLog;
			_backend.Dispose();
		});
	}

	private void OnPressed(GlobalHotkey hotkey, string? activationToken) =>
		RunOnMain(() => {
			if (!string.IsNullOrEmpty(activationToken)) {
				_applyActivationToken(activationToken);
			}
			Pressed?.Invoke(hotkey);
		});

	private void OnLog(string message) => Log?.Invoke(message);

	private void RunOnMain(Action action) {
		if (Environment.CurrentManagedThreadId == _uiThreadId) {
			action();
		} else {
			GtkMain.Invoke(action);
		}
	}

	private sealed class UnsupportedLinuxGlobalHotkeys : ILinuxGlobalHotkeyBackend {
		public event Action<GlobalHotkey, string?>? Pressed;

		public event Action<string>? Log;

		public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) {
			if (hotkeys.Count > 0) {
				Log?.Invoke("[hotkey] this GDK display backend does not support Linux global shortcuts.");
			}
		}

		public void Dispose() => _ = Pressed;
	}
}
