namespace Weavie.Core.Commands;

/// <summary>
/// The cross-platform half of global hotkeys: it owns <em>which</em> hotkeys exist and what they do, while a
/// per-OS <see cref="IGlobalHotkeyRegistrar"/> owns the native registration. It reads the
/// <see cref="ResolvedKeybinding.Global"/> bindings from the <see cref="KeybindingStore"/>, parses each chord,
/// and hands the set to the registrar — re-applying when the user edits <c>~/.weavie/keybindings.json</c>. On
/// a press it invokes the bound command through the <see cref="CommandDispatcher"/>, like any other trigger.
///
/// Lives at app scope (one per process): a global hotkey isn't tied to any one window. Disposing it detaches
/// the store subscription and disposes the registrar. See <c>docs/specs/commands.md</c>.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable {
	private readonly KeybindingStore _keybindings;
	private readonly CommandDispatcher _dispatcher;
	private readonly IGlobalHotkeyRegistrar _registrar;
	private readonly Action _onKeybindingsChanged;
	private bool _disposed;

	/// <summary>
	/// Wires the service, applies the current global bindings immediately, and re-applies on each
	/// keybindings-file change. Takes ownership of <paramref name="registrar"/> (disposes it).
	/// </summary>
	public GlobalHotkeyService(KeybindingStore keybindings, CommandDispatcher dispatcher, IGlobalHotkeyRegistrar registrar) {
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(registrar);
		_keybindings = keybindings;
		_dispatcher = dispatcher;
		_registrar = registrar;

		_registrar.Pressed += OnPressed;
		Apply();
		_onKeybindingsChanged = Apply;
		_keybindings.KeybindingsChanged += _onKeybindingsChanged;
	}

	/// <summary>Diagnostic log line: dispatch failures (a global command with no handler, etc.).</summary>
	public event Action<string>? Log;

	/// <inheritdoc/>
	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		_keybindings.KeybindingsChanged -= _onKeybindingsChanged;
		_registrar.Pressed -= OnPressed;
		_registrar.Dispose();
	}

	// Recompute the global hotkey set from the resolved bindings and hand it to the registrar. A global
	// binding with no key (modifiers-only) can't be a hotkey, so drop it loudly rather than register garbage.
	private void Apply() {
		var hotkeys = new List<GlobalHotkey>();
		foreach (var binding in _keybindings.Resolved) {
			if (!binding.Global) {
				continue;
			}

			var parsed = ChordParser.Parse(binding.Key);
			if (!parsed.HasKey) {
				Log?.Invoke($"[hotkey] ignoring global binding '{binding.Key}' for '{binding.Command}': no key in chord.");
				continue;
			}

			hotkeys.Add(new GlobalHotkey {
				Command = binding.Command,
				ArgsJson = binding.ArgsJson,
				Chord = binding.Key,
				Modifiers = parsed.Modifiers,
				Key = parsed.Key,
			});
		}

		_registrar.Apply(hotkeys);
	}

	private void OnPressed(GlobalHotkey hotkey) => _ = InvokeAsync(hotkey);

	private async Task InvokeAsync(GlobalHotkey hotkey) {
		try {
			var result = await _dispatcher.InvokeAsync(hotkey.Command, hotkey.ArgsJson, CancellationToken.None).ConfigureAwait(false);
			if (!result.Ok) {
				Log?.Invoke($"[hotkey] {hotkey.Chord} → {hotkey.Command} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Log?.Invoke($"[hotkey] {hotkey.Chord} → {hotkey.Command} error: {ex.Message}");
		}
	}
}
