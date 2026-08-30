using Weavie.Core.Configuration;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>An ordered, session-owned set of independently supervised shell terminal tabs.</summary>
public sealed class ShellTerminalSet : IDisposable {
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
		IReadOnlyList<ShellTerminalDescriptor> descriptors,
		Func<string, string> scrollbackPath,
		Action<bool, Action> acceptInput,
		Action<int, int> resized) {
		ArgumentNullException.ThrowIfNull(bus);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(descriptors);
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
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var descriptor in descriptors) {
			if (descriptor is null || !ShellTerminalDescriptor.IsValidId(descriptor.Id) || !ids.Add(descriptor.Id)) {
				throw new ArgumentException("Shell terminal IDs must be path-safe lowercase GUIDs and unique.", nameof(descriptors));
			}
			_items.Add(Build(bus, descriptor));
		}
		PublishCatalog();
	}

	/// <summary>The ordered terminal tabs.</summary>
	public IReadOnlyList<ShellTerminal> Items {
		get {
			lock (_gate) return [.. _items];
		}
	}

	/// <summary>The deterministic terminal used by non-view automation, or null when the shell pane is empty.</summary>
	public ShellTerminal? Primary {
		get {
			lock (_gate) return _items.FirstOrDefault();
		}
	}

	/// <summary>Whether any terminal tab owns a foreground job.</summary>
	public bool HasForegroundJob {
		get {
			lock (_gate) return _items.Any(item => item.Controller.HasForegroundJob);
		}
	}

	internal event Action<IReadOnlyList<ShellTerminalDescriptor>>? Changed;

	/// <summary>Creates, starts, and announces one new shell terminal tab.</summary>
	public ShellTerminal Create() {
		ShellTerminal terminal;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			terminal = Build(_bus, ShellTerminalDescriptor.New());
			_items.Add(terminal);
		}
		PublishChanged();
		terminal.Controller.EnsureStarted();
		return terminal;
	}

	/// <summary>Looks up an exact terminal id.</summary>
	public ShellTerminal? Find(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) return _items.FirstOrDefault(item => item.Id == id);
	}

	/// <summary>Removes one terminal unless it owns a foreground job and <paramref name="force"/> is false.</summary>
	public ShellTerminalCloseResult DetachForClose(string id, bool force, out ShellTerminal? detached) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		detached = null;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			var found = _items.FirstOrDefault(item => item.Id == id);
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

	/// <summary>Starts every restored terminal without requiring a visible page.</summary>
	public void EnsureStarted() {
		foreach (var terminal in Items) {
			terminal.Controller.EnsureStarted();
		}
	}

	/// <summary>Restarts every terminal so a changed shell setting takes effect.</summary>
	public void Restart() {
		foreach (var terminal in Items) {
			terminal.Controller.Restart();
		}
	}

	internal void Resync(MessageTarget target) {
		ArgumentNullException.ThrowIfNull(target);
		ReplayCatalog(target.Feature("terminal.shell"));
		foreach (var terminal in Items) {
			terminal.Controller.ResyncPane(target.Feature(FeatureName(terminal.Id)));
		}
	}

	internal void SeedSize(int columns, int rows) {
		foreach (var terminal in Items) {
			terminal.Controller.Resize(columns, rows);
		}
	}

	internal static string FeatureName(string id) => $"terminal.shell.{id}";

	private ShellTerminal Build(SessionMessageBus bus, ShellTerminalDescriptor descriptor) {
		var messages = bus.Feature(FeatureName(descriptor.Id));
		string scrollbackPath = _scrollbackPath(descriptor.Id);
		var controller = new TerminalController(
			messages,
			$"shell:{descriptor.Id}",
			_settings,
			_launcher,
			new ShellTerminalProcess(_settings, _workspace),
			RestartPolicy.Never) {
			Workspace = _workspace,
			ScrollbackLogPath = scrollbackPath,
		};
		return new ShellTerminal(
			descriptor,
			controller,
			TerminalMessageWiring.Wire(messages, controller, _acceptInput, _resized),
			scrollbackPath);
	}

	private void PublishChanged() {
		PublishCatalog();
		Changed?.Invoke([.. Items.Select(item => item.Descriptor)]);
	}

	private void PublishCatalog() => ReplayCatalog(_catalog);

	private void ReplayCatalog(IMessageFeatureTarget target) =>
		target.Publish("catalog", new {
			terminals = Items.Select(item => new { id = item.Id }),
		});

	/// <inheritdoc/>
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

/// <summary>One shell tab's stable identity, supervised PTY, and exact message handlers.</summary>
public sealed class ShellTerminal : IDisposable {
	private IDisposable? _messages;
	private readonly string _scrollbackPath;

	internal ShellTerminal(
		ShellTerminalDescriptor descriptor,
		TerminalController controller,
		IDisposable messages,
		string scrollbackPath) {
		Descriptor = descriptor;
		Controller = controller;
		_messages = messages;
		_scrollbackPath = scrollbackPath;
	}

	/// <summary>The stable terminal descriptor.</summary>
	public ShellTerminalDescriptor Descriptor { get; }

	/// <summary>The stable terminal id.</summary>
	public string Id => Descriptor.Id;

	/// <summary>The terminal process controller.</summary>
	public TerminalController Controller { get; }

	internal void DetachMessages() => Interlocked.Exchange(ref _messages, null)?.Dispose();

	internal void DisposePermanently() {
		Dispose();
		File.Delete(_scrollbackPath);
	}

	/// <inheritdoc/>
	public void Dispose() {
		DetachMessages();
		Controller.Dispose();
	}
}

/// <summary>The outcome of closing an exact shell terminal.</summary>
public enum ShellTerminalCloseResult {
	/// <summary>The terminal was removed.</summary>
	Closed,

	/// <summary>No terminal has that id.</summary>
	NotFound,

	/// <summary>The terminal owns a foreground job and requires confirmation.</summary>
	Busy,
}
