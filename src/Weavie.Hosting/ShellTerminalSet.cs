using Weavie.Core.Configuration;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

internal sealed class ShellTerminalSet : IDisposable {
	private readonly SessionMessageBus _bus;
	private readonly MessageFeatureChannel _catalog;
	private readonly SettingsStore _settings;
	private readonly IPtyLauncher _launcher;
	private readonly string _workspace;
	private readonly Func<string, string> _scrollbackPath;
	private readonly Action<bool, Action> _acceptInput;
	private readonly Action<int, int> _resized;
	private readonly Lock _gate = new();
	private readonly List<ShellTerminal> _items = [];
	private bool _disposed;

	internal ShellTerminalSet(
		SessionMessageBus bus,
		SettingsStore settings,
		IPtyLauncher launcher,
		string workspace,
		IReadOnlyList<string> ids,
		Func<string, string> scrollbackPath,
		Action<bool, Action> acceptInput,
		Action<int, int> resized) {
		ArgumentNullException.ThrowIfNull(bus);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(ids);
		ArgumentNullException.ThrowIfNull(scrollbackPath);
		ArgumentNullException.ThrowIfNull(acceptInput);
		ArgumentNullException.ThrowIfNull(resized);
		_bus = bus;
		_catalog = bus.Feature("terminal.shell");
		_settings = settings;
		_launcher = launcher;
		_workspace = workspace;
		_scrollbackPath = scrollbackPath;
		_acceptInput = acceptInput;
		_resized = resized;
		var unique = new HashSet<string>(StringComparer.Ordinal);
		foreach (string id in ids) {
			if (!ShellTerminalId.IsValid(id) || !unique.Add(id)) {
				throw new ArgumentException("Shell terminal IDs must be path-safe lowercase GUIDs and unique.", nameof(ids));
			}
			_items.Add(Build(id));
		}
		PublishCatalog(_catalog, SnapshotIds());
	}

	internal IReadOnlyList<ShellTerminal> Items => Snapshot();

	internal ShellTerminal? Primary {
		get {
			lock (_gate) return _items.FirstOrDefault();
		}
	}

	internal bool HasForegroundJob {
		get {
			lock (_gate) return _items.Any(item => item.Controller.HasForegroundJob);
		}
	}

	internal event Action<IReadOnlyList<string>>? Changed;

	internal ShellTerminal Create() {
		ShellTerminal terminal;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			terminal = Build(ShellTerminalId.New());
			_items.Add(terminal);
		}
		PublishChanged();
		terminal.Controller.EnsureStarted();
		return terminal;
	}

	internal ShellTerminal? Resolve(string? id) {
		lock (_gate) {
			return id is null
				? _items.FirstOrDefault()
				: _items.FirstOrDefault(item => item.Id == id);
		}
	}

	internal ShellTerminalCloseResult DetachForClose(
		string? id,
		bool force,
		out ShellTerminal? detached) {
		detached = null;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			var found = id is null
				? _items.FirstOrDefault()
				: _items.FirstOrDefault(item => item.Id == id);
			if (found is null) {
				return ShellTerminalCloseResult.NotFound;
			}
			if (!force && found.Controller.HasForegroundJob) {
				return ShellTerminalCloseResult.Busy;
			}
			_items.Remove(found);
			detached = found;
		}
		detached.DetachMessages();
		PublishChanged();
		return ShellTerminalCloseResult.Closed;
	}

	internal void EnsureStarted() {
		foreach (var terminal in Snapshot()) {
			terminal.Controller.EnsureStarted();
		}
	}

	internal void Restart() {
		foreach (var terminal in Snapshot()) {
			terminal.Controller.Restart();
		}
	}

	internal void Resync(MessageTarget target) {
		ArgumentNullException.ThrowIfNull(target);
		var terminals = Snapshot();
		PublishCatalog(target.Feature("terminal.shell"), [.. terminals.Select(terminal => terminal.Id)]);
		foreach (var terminal in terminals) {
			terminal.Controller.ResyncPane(target.Feature(FeatureName(terminal.Id)));
		}
	}

	internal void SeedSize(int columns, int rows) {
		foreach (var terminal in Snapshot()) {
			terminal.Controller.Resize(columns, rows);
		}
	}

	internal static string FeatureName(string id) => $"terminal.shell.{id}";

	private ShellTerminal Build(string id) {
		var messages = _bus.Feature(FeatureName(id));
		string scrollbackPath = _scrollbackPath(id);
		var controller = new TerminalController(
			messages,
			$"shell:{id}",
			_settings,
			_launcher,
			new ShellTerminalProcess(_settings, _workspace),
			RestartPolicy.Never) {
			Workspace = _workspace,
			ScrollbackLogPath = scrollbackPath,
		};
		return new ShellTerminal(
			id,
			controller,
			TerminalMessageWiring.Wire(messages, controller, _acceptInput, _resized),
			scrollbackPath);
	}

	private ShellTerminal[] Snapshot() {
		lock (_gate) return [.. _items];
	}

	private string[] SnapshotIds() => [.. Snapshot().Select(terminal => terminal.Id)];

	private void PublishChanged() {
		string[] ids = SnapshotIds();
		PublishCatalog(_catalog, ids);
		Changed?.Invoke(ids);
	}

	private static void PublishCatalog(IMessageFeatureTarget target, IReadOnlyList<string> ids) =>
		target.Publish("catalog", new { terminalIds = ids });

	public void Dispose() {
		ShellTerminal[] terminals;
		lock (_gate) {
			if (_disposed) return;
			_disposed = true;
			terminals = [.. _items];
			_items.Clear();
		}
		foreach (var terminal in terminals) {
			terminal.Dispose();
		}
	}
}

internal sealed class ShellTerminal : IDisposable {
	private IDisposable? _messages;
	private readonly string _scrollbackPath;

	internal ShellTerminal(
		string id,
		TerminalController controller,
		IDisposable messages,
		string scrollbackPath) {
		Id = id;
		Controller = controller;
		_messages = messages;
		_scrollbackPath = scrollbackPath;
	}

	internal string Id { get; }

	internal TerminalController Controller { get; }

	internal void DetachMessages() => Interlocked.Exchange(ref _messages, null)?.Dispose();

	internal void DisposePermanently() {
		Dispose();
		File.Delete(_scrollbackPath);
	}

	public void Dispose() {
		DetachMessages();
		Controller.Dispose();
	}
}

internal enum ShellTerminalCloseResult {
	Closed,
	NotFound,
	Busy,
}
