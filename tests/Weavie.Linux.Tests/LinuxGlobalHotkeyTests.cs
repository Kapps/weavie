using System.Threading.Channels;
using Weavie.Core.Commands;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class LinuxGlobalHotkeyTests {
	[Theory]
	[InlineData("`", HotkeyModifiers.Ctrl, "CTRL+grave")]
	[InlineData("p", HotkeyModifiers.Mod | HotkeyModifiers.Shift, "CTRL+SHIFT+p")]
	[InlineData("enter", HotkeyModifiers.Alt, "ALT+Return")]
	[InlineData("f12", HotkeyModifiers.Meta, "LOGO+F12")]
	public void PortalMapping_UsesXkbShortcutNames(string key, HotkeyModifiers modifiers, string expected) {
		Assert.True(LinuxHotkeyMapping.TryPortalTrigger(Hotkey(key, modifiers), out string trigger));
		Assert.Equal(expected, trigger);
	}

	[Fact]
	public void X11Mapping_ResolvesPlatformModToControl() {
		uint modifiers = LinuxHotkeyMapping.X11Modifiers(HotkeyModifiers.Mod | HotkeyModifiers.Alt);
		Assert.Equal(X11.ControlMask | X11.Mod1Mask, modifiers);
	}

	[Fact]
	public async Task WaylandBackend_RebindsOneSessionAndRoutesOnlyItsActivations() {
		var portal = new FakePortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = Channel.CreateUnbounded<(GlobalHotkey Hotkey, string? Token)>();
		backend.Pressed += (hotkey, token) => Assert.True(pressed.Writer.TryWrite((hotkey, token)));
		var first = Hotkey("`", HotkeyModifiers.Ctrl);

		backend.Apply([first]);
		var (firstSession, firstShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Activate(firstSession, firstShortcuts[0].Id, "activation-one");

		Assert.Equal((first, "activation-one"), await pressed.Reader.ReadAsync());

		var second = Hotkey("p", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift);
		backend.Apply([second]);
		var (secondSession, secondShortcuts) = await portal.Binds.Reader.ReadAsync();

		Assert.Contains(firstSession, portal.Closed);
		Assert.Equal("CTRL+SHIFT+p", Assert.Single(secondShortcuts).Trigger);
		portal.Activate(firstSession, firstShortcuts[0].Id, "stale-token");
		portal.Activate(secondSession, secondShortcuts[0].Id, "activation-two");
		Assert.Equal((second, "activation-two"), await pressed.Reader.ReadAsync());
		Assert.False(pressed.Reader.TryRead(out _));
	}

	[Fact]
	public async Task WaylandBackend_DoesNotRebindAnUnchangedSet() {
		var portal = new FakePortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var hotkey = Hotkey("`", HotkeyModifiers.Ctrl);

		backend.Apply([hotkey]);
		_ = await portal.Binds.Reader.ReadAsync();
		backend.Apply([hotkey]);

		Assert.Equal(1, portal.BindCount);
		Assert.Empty(portal.Closed);
	}

	[Fact]
	public async Task WaylandBackend_UsesThePortalAcceptedSubset() {
		var portal = new FakePortal { AcceptedShortcutCount = 1 };
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = Channel.CreateUnbounded<GlobalHotkey>();
		var logs = new List<string>();
		backend.Pressed += (hotkey, _) => Assert.True(pressed.Writer.TryWrite(hotkey));
		backend.Log += logs.Add;
		var accepted = Hotkey("`", HotkeyModifiers.Ctrl);
		var omitted = Hotkey("p", HotkeyModifiers.Ctrl) with {
			Command = "test.omitted",
			Chord = "ctrl+p",
		};

		backend.Apply([accepted, omitted]);
		var (session, shortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Activate(session, shortcuts[0].Id, "accepted-token");
		portal.Activate(session, shortcuts[1].Id, "ignored-token");

		Assert.Equal(accepted, await pressed.Reader.ReadAsync());
		Assert.False(pressed.Reader.TryRead(out _));
		Assert.Contains(logs, line => line.Contains("did not bind 'ctrl+p'", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WaylandBackend_RebindsAfterThePortalInvalidatesItsSession() {
		var portal = new FakePortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = Channel.CreateUnbounded<GlobalHotkey>();
		backend.Pressed += (hotkey, _) => Assert.True(pressed.Writer.TryWrite(hotkey));
		var hotkey = Hotkey("`", HotkeyModifiers.Ctrl);

		backend.Apply([hotkey]);
		var (firstSession, firstShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Invalidate();
		var (secondSession, secondShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Activate(firstSession, firstShortcuts[0].Id, "stale-token");
		portal.Activate(secondSession, secondShortcuts[0].Id, "fresh-token");

		Assert.Equal(hotkey, await pressed.Reader.ReadAsync());
		Assert.False(pressed.Reader.TryRead(out _));
	}

	[Fact(Timeout = 5000)]
	public async Task WaylandBackend_DisposeInterruptsAPendingPortalBinding() {
		var portal = new FakePortal { BlockBindingUntilDisposed = true };
		var backend = new WaylandGlobalHotkeys(portal);

		backend.Apply([Hotkey("`", HotkeyModifiers.Ctrl)]);
		_ = await portal.Binds.Reader.ReadAsync();
		backend.Dispose();

		Assert.True(portal.Disposed);
	}

	private static GlobalHotkey Hotkey(string key, HotkeyModifiers modifiers) => new() {
		Command = CoreCommands.ToggleWindow,
		Chord = $"ctrl+{key}",
		Key = key,
		Modifiers = modifiers,
	};

	private sealed class FakePortal : IGlobalShortcutsPortal {
		private int _nextSession;

		internal Channel<(string Session, IReadOnlyList<PortalShortcut> Shortcuts)> Binds { get; } =
			Channel.CreateUnbounded<(string, IReadOnlyList<PortalShortcut>)>();
		internal List<string> Closed { get; } = [];
		internal int BindCount { get; private set; }
		internal int AcceptedShortcutCount { get; init; } = int.MaxValue;
		internal bool BlockBindingUntilDisposed { get; init; }
		internal bool Disposed { get; private set; }
		private TaskCompletionSource<PortalBinding>? _pendingBinding;

		public event Action<PortalActivation>? Activated;
		public event Action? Invalidated;
		public event Action<string>? Log;

		public Task<PortalBinding> BindAsync(IReadOnlyList<PortalShortcut> shortcuts) {
			BindCount++;
			string session = $"/session/{++_nextSession}";
			Assert.True(Binds.Writer.TryWrite((session, shortcuts)));
			IReadOnlySet<string> accepted = shortcuts
				.Take(AcceptedShortcutCount)
				.Select(shortcut => shortcut.Id)
				.ToHashSet(StringComparer.Ordinal);
			var binding = new PortalBinding(session, accepted);
			if (!BlockBindingUntilDisposed) {
				return Task.FromResult(binding);
			}
			_pendingBinding = new TaskCompletionSource<PortalBinding>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			return _pendingBinding.Task;
		}

		public Task CloseSessionAsync(string sessionHandle) {
			Closed.Add(sessionHandle);
			return Task.CompletedTask;
		}

		internal void Activate(string session, string id, string token) =>
			Activated?.Invoke(new PortalActivation(session, id, token));

		internal void Invalidate() => Invalidated?.Invoke();

		public void Dispose() {
			Disposed = true;
			_pendingBinding?.TrySetException(new ObjectDisposedException(nameof(FakePortal)));
			_ = Log;
		}
	}
}
