using System.Collections.Concurrent;
using System.Threading.Channels;
using Tmds.DBus.Protocol;
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

	[Theory]
	[InlineData("org.freedesktop.DBus.Error.UnknownInterface", true)]
	[InlineData("org.freedesktop.DBus.Error.UnknownMethod", true)]
	[InlineData("org.freedesktop.portal.Error.NotAllowed", true)]
	[InlineData("org.freedesktop.portal.Error.Failed", false)]
	public void PortalIdentityRegistration_OnlyIgnoresUnavailableOrSandboxedRegistry(
		string errorName,
		bool expected) => Assert.Equal(expected, XdgGlobalShortcutsPortal.CanUseDetectedIdentity(errorName));

	[Fact]
	public async Task PreRegistryPortal_EntersSystemdDesktopAppScope() {
		var appScope = new FakeAppScope();
		var identity = new PortalHostIdentity(appScope);
		var logs = new List<string>();

		await identity.RegisterAsync(
			() => Task.FromException(
				new DBusErrorReplyException("org.freedesktop.DBus.Error.UnknownMethod", "No Registry")),
			logs.Add,
			CancellationToken.None);

		Assert.Equal(1, appScope.EnsureCount);
		Assert.Contains(logs, line => line.Contains("systemd desktop-app identity", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SandboxedPortal_KeepsDetectedIdentityWithoutMovingScope() {
		var appScope = new FakeAppScope();
		var identity = new PortalHostIdentity(appScope);

		await identity.RegisterAsync(
			() => Task.FromException(
				new DBusErrorReplyException("org.freedesktop.portal.Error.NotAllowed", "Sandboxed")),
			_ => { },
			CancellationToken.None);

		Assert.Equal(0, appScope.EnsureCount);
	}

	[Fact]
	public async Task PreRegistryPortal_SurfacesMissingDesktopAppScope() {
		var scopeError = new InvalidOperationException("No user systemd manager");
		var appScope = new FakeAppScope { Failure = scopeError };
		var identity = new PortalHostIdentity(appScope);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => identity.RegisterAsync(
			() => Task.FromException(
				new DBusErrorReplyException("org.freedesktop.DBus.Error.UnknownInterface", "No Registry")),
			_ => { },
			CancellationToken.None));

		Assert.Same(scopeError, error.InnerException);
	}

	[Theory]
	[InlineData("app-io.github.kapps.weavie-1234.scope", true)]
	[InlineData("app-weavie-io.github.kapps.weavie-a0b1.scope", true)]
	[InlineData("app-io.github.kapps.weavie.service", true)]
	[InlineData("app-gnome-io.github.kapps.weavie@1234.service", true)]
	[InlineData("app-io.github.kapps.other-1234.scope", false)]
	[InlineData("org.gnome.Terminal.service", false)]
	public void DesktopAppScope_RecognizesOnlyWeavieApplicationUnits(string unit, bool expected) =>
		Assert.Equal(expected, LinuxDesktopAppScope.IsAppUnit(unit));

	[Fact]
	public async Task DesktopAppScope_SubscribesBeforeStartingAndWaitingForTheSystemdJob() {
		var calls = new List<string>();
		Action<string, string>? jobRemoved = null;

		await LinuxDesktopAppScope.AwaitScopeCreationAsync(
			"app-weavie.scope",
			() => {
				calls.Add("subscribe");
				return Task.CompletedTask;
			},
			(onRemoved, _) => {
				calls.Add("watch");
				jobRemoved = onRemoved;
				return ValueTask.FromResult<IDisposable>(new CancellationTokenSource());
			},
			() => {
				calls.Add("start");
				Assert.NotNull(jobRemoved);
				jobRemoved("app-weavie.scope", "done");
				return Task.CompletedTask;
			},
			CancellationToken.None);

		Assert.Equal(["subscribe", "watch", "start"], calls);
	}

	[Theory]
	[InlineData(4, 4, "owner-a", "owner-a", true)]
	[InlineData(4, 5, "owner-a", "owner-a", false)]
	[InlineData(4, 4, "owner-a", "owner-b", false)]
	[InlineData(4, 4, "owner-a", null, false)]
	public void PortalSetup_PublishesOnlyTheExactObservedOwner(
		long setupGeneration,
		long currentGeneration,
		string expectedOwner,
		string? currentOwner,
		bool expected) => Assert.Equal(
		expected,
		XdgGlobalShortcutsPortal.SetupIsCurrent(
			setupGeneration,
			currentGeneration,
			expectedOwner,
			currentOwner));

	[Fact]
	public async Task WaylandBackend_RebindsOnePortalSessionAndRoutesItsActivation() {
		var portal = new FakePortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = new List<(GlobalHotkey Hotkey, string? Token)>();
		backend.Pressed += (hotkey, token) => pressed.Add((hotkey, token));
		var first = Hotkey("`", HotkeyModifiers.Ctrl);

		backend.Apply([first]);
		var (firstSession, firstShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Activate(firstSession, firstShortcuts[0].Id, "activation-one");

		Assert.Equal([(first, "activation-one")], pressed);

		var second = Hotkey("p", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift);
		backend.Apply([second]);
		var (secondSession, secondShortcuts) = await portal.Binds.Reader.ReadAsync();

		Assert.Contains(firstSession, portal.Closed);
		Assert.Equal("CTRL+SHIFT+p", Assert.Single(secondShortcuts).Trigger);
		portal.Activate(firstSession, firstShortcuts[0].Id, "stale-token");
		Assert.Single(pressed);
		portal.Activate(secondSession, secondShortcuts[0].Id, "activation-two");
		Assert.Equal((second, "activation-two"), pressed[1]);
	}

	[Fact]
	public async Task WaylandBackend_DoesNotRebindAnUnchangedGlobalSet() {
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
	public async Task WaylandBackend_OnlyActivatesPortalAcceptedShortcutsAndReportsOmissions() {
		var portal = new FakePortal { AcceptedShortcutCount = 1 };
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = new List<GlobalHotkey>();
		var logs = new List<string>();
		backend.Pressed += (hotkey, _) => pressed.Add(hotkey);
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

		Assert.Single(pressed, accepted);
		Assert.Contains(logs, line => line.Contains("did not bind 'ctrl+p'", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WaylandBackend_ReportsWhenThePortalAcceptsNoShortcuts() {
		var portal = new FakePortal { AcceptedShortcutCount = 0 };
		using var backend = new WaylandGlobalHotkeys(portal);
		var logs = new List<string>();
		backend.Log += logs.Add;

		backend.Apply([Hotkey("`", HotkeyModifiers.Ctrl)]);
		_ = await portal.Binds.Reader.ReadAsync();

		Assert.Contains(logs, line => line.Contains("did not bind 'ctrl+`'", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WaylandBackend_RebindsDesiredShortcutsAfterPortalRestart() {
		var portal = new FakePortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var pressed = new List<GlobalHotkey>();
		backend.Pressed += (hotkey, _) => pressed.Add(hotkey);
		var hotkey = Hotkey("`", HotkeyModifiers.Ctrl);

		backend.Apply([hotkey]);
		var (firstSession, firstShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Restart();
		var (secondSession, secondShortcuts) = await portal.Binds.Reader.ReadAsync();
		portal.Activate(firstSession, firstShortcuts[0].Id, "stale-token");
		portal.Activate(secondSession, secondShortcuts[0].Id, "fresh-token");

		Assert.Single(pressed, hotkey);
		Assert.DoesNotContain(firstSession, portal.Closed);
	}

	[Fact]
	public async Task WaylandBackend_KeepsSupersededCancellationAliveUntilItsResetCompletes() {
		var portal = new LateCancellationPortal();
		using var backend = new WaylandGlobalHotkeys(portal);
		var logs = new ConcurrentQueue<string>();
		backend.Log += logs.Enqueue;

		backend.Apply([Hotkey("`", HotkeyModifiers.Ctrl)]);
		await portal.FirstBindEntered.Task;
		backend.Apply([Hotkey("p", HotkeyModifiers.Ctrl)]);
		portal.ReleaseFirstBind.TrySetResult();
		await portal.SecondBindCompleted.Task;

		Assert.DoesNotContain(logs, line => line.Contains("disposed", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WaylandBackend_DisposeWaitsForAConcurrentPortalRebind() {
		var portal = new BlockingRebindPortal();
		var backend = new WaylandGlobalHotkeys(portal);
		var logs = new ConcurrentQueue<string>();
		backend.Log += logs.Enqueue;
		backend.Apply([Hotkey("`", HotkeyModifiers.Ctrl)]);

		var restart = Task.Run(portal.Restart);
		Assert.True(portal.RebindEntered.Wait(TimeSpan.FromSeconds(5)));
		var disposeStarted = new ManualResetEventSlim();
		var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var disposeThread = new Thread(() => {
			disposeStarted.Set();
			try {
				backend.Dispose();
				disposeCompleted.TrySetResult();
			} catch (Exception ex) {
				disposeCompleted.TrySetException(ex);
			}
		});
		disposeThread.Start();
		Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
		var observationWindow = Task.Delay(TimeSpan.FromMilliseconds(200));
		var firstCompletion = await Task.WhenAny(disposeCompleted.Task, observationWindow);
		portal.ReleaseRebind.Set();

		await restart;
		await disposeCompleted.Task;
		disposeThread.Join();
		Assert.Same(observationWindow, firstCompletion);
		Assert.DoesNotContain(logs, line => line.Contains("disposed", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void WaylandBackend_DisposesPortalWhenClosingTheSessionFails() {
		var portal = new FailingClosePortal();
		var backend = new WaylandGlobalHotkeys(portal);
		backend.Apply([Hotkey("`", HotkeyModifiers.Ctrl)]);

		Assert.Throws<InvalidOperationException>(backend.Dispose);

		Assert.Equal(1, portal.DisposeCount);
	}

	private static GlobalHotkey Hotkey(string key, HotkeyModifiers modifiers) => new() {
		Command = CoreCommands.ToggleWindow,
		Chord = $"ctrl+{key}",
		Key = key,
		Modifiers = modifiers,
	};

	private sealed class FakePortal : IGlobalShortcutsPortal {
		private int _nextSession;

		public Channel<(string Session, IReadOnlyList<PortalShortcut> Shortcuts)> Binds { get; } =
			Channel.CreateUnbounded<(string, IReadOnlyList<PortalShortcut>)>();

		public List<string> Closed { get; } = [];
		public int BindCount { get; private set; }
		public int AcceptedShortcutCount { get; init; } = int.MaxValue;

		public event Action<PortalActivation>? Activated;
		public event Action? Invalidated;
		public event Action<string>? Log;

		public Task<PortalBinding> BindAsync(IReadOnlyList<PortalShortcut> shortcuts, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			BindCount++;
			string session = $"/session/{++_nextSession}";
			Assert.True(Binds.Writer.TryWrite((session, shortcuts)));
			IReadOnlySet<string> accepted = shortcuts
				.Take(AcceptedShortcutCount)
				.Select(shortcut => shortcut.Id)
				.ToHashSet(StringComparer.Ordinal);
			return Task.FromResult(new PortalBinding(session, accepted));
		}

		public Task CloseSessionAsync(string sessionHandle) {
			Closed.Add(sessionHandle);
			return Task.CompletedTask;
		}

		public void Activate(string session, string id, string token) =>
			Activated?.Invoke(new PortalActivation(session, id, token));

		public void Restart() => Invalidated?.Invoke();

		public void Dispose() => _ = Log;
	}

	private sealed class FakeAppScope : ILinuxDesktopAppScope {
		internal int EnsureCount { get; private set; }
		internal Exception? Failure { get; init; }

		public Task EnsureAsync(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			EnsureCount++;
			return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
		}
	}

	private sealed class LateCancellationPortal : IGlobalShortcutsPortal {
		private int _bindCount;

		internal TaskCompletionSource FirstBindEntered { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource ReleaseFirstBind { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource SecondBindCompleted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public event Action<PortalActivation>? Activated;
		public event Action? Invalidated;
		public event Action<string>? Log;

		public async Task<PortalBinding> BindAsync(
			IReadOnlyList<PortalShortcut> shortcuts,
			CancellationToken ct) {
			int bind = Interlocked.Increment(ref _bindCount);
			if (bind == 1) {
				FirstBindEntered.TrySetResult();
				await ReleaseFirstBind.Task.ConfigureAwait(false);
				using var registration = ct.Register(static () => { });
				ct.ThrowIfCancellationRequested();
			}

			string session = $"/session/{bind}";
			if (bind == 2) {
				SecondBindCompleted.TrySetResult();
			}
			return new PortalBinding(
				session,
				shortcuts.Select(shortcut => shortcut.Id).ToHashSet(StringComparer.Ordinal));
		}

		public Task CloseSessionAsync(string sessionHandle) => Task.CompletedTask;

		public void Dispose() {
			_ = Activated;
			_ = Invalidated;
			_ = Log;
		}
	}

	private sealed class BlockingRebindPortal : IGlobalShortcutsPortal {
		private int _bindCount;

		internal ManualResetEventSlim RebindEntered { get; } = new();
		internal ManualResetEventSlim ReleaseRebind { get; } = new();

		public event Action<PortalActivation>? Activated;
		public event Action? Invalidated;
		public event Action<string>? Log;

		public Task<PortalBinding> BindAsync(
			IReadOnlyList<PortalShortcut> shortcuts,
			CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			int bind = Interlocked.Increment(ref _bindCount);
			if (bind == 2) {
				RebindEntered.Set();
				ReleaseRebind.Wait();
			}
			return Task.FromResult(new PortalBinding(
				$"/session/{bind}",
				shortcuts.Select(shortcut => shortcut.Id).ToHashSet(StringComparer.Ordinal)));
		}

		public Task CloseSessionAsync(string sessionHandle) => Task.CompletedTask;

		internal void Restart() => Invalidated?.Invoke();

		public void Dispose() {
			_ = Activated;
			_ = Log;
		}
	}

	private sealed class FailingClosePortal : IGlobalShortcutsPortal {
		internal int DisposeCount { get; private set; }

		public event Action<PortalActivation>? Activated;
		public event Action? Invalidated;
		public event Action<string>? Log;

		public Task<PortalBinding> BindAsync(
			IReadOnlyList<PortalShortcut> shortcuts,
			CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(new PortalBinding(
				"/session/1",
				shortcuts.Select(shortcut => shortcut.Id).ToHashSet(StringComparer.Ordinal)));
		}

		public Task CloseSessionAsync(string sessionHandle) =>
			Task.FromException(new InvalidOperationException("Session close failed"));

		public void Dispose() {
			DisposeCount++;
			_ = Activated;
			_ = Invalidated;
			_ = Log;
		}
	}
}
