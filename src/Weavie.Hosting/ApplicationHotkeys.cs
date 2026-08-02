using Weavie.Core.Commands;

namespace Weavie.Hosting;

/// <summary>
/// Owns the process-wide global-hotkey command path. Platform composition roots provide only their native
/// registrar and frontmost-window action; command dispatch and keybinding reapplication live here once.
/// </summary>
public sealed class ApplicationHotkeys : IDisposable {
	private readonly IGlobalHotkeyRegistrar _registrar;
	private readonly GlobalHotkeyService _service;
	private readonly Action<string> _log;
	private bool _disposed;

	/// <summary>Registers the effective global bindings and routes window-toggle presses to the platform action.</summary>
	public ApplicationHotkeys(
		CommandRegistry commands,
		KeybindingStore keybindings,
		IGlobalHotkeyRegistrar registrar,
		Action toggleWindow,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(commands);
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(registrar);
		ArgumentNullException.ThrowIfNull(toggleWindow);
		ArgumentNullException.ThrowIfNull(log);
		_registrar = registrar;
		_log = log;
		_registrar.Log += _log;

		var dispatcher = new CommandDispatcher(commands);
		dispatcher.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			toggleWindow();
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		_service = new GlobalHotkeyService(keybindings, dispatcher, registrar);
		_service.Log += _log;
	}

	/// <inheritdoc/>
	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		_service.Log -= _log;
		_service.Dispose();
		_registrar.Log -= _log;
	}
}
