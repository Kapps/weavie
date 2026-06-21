namespace Weavie.Core.Commands;

/// <summary>Host-supplied invoker for a <see cref="CommandLocation.Web"/> command: posts the run to the web and awaits its ack.</summary>
public delegate Task<CommandResult> WebCommandInvoker(string id, string? argsJson, CancellationToken ct);

/// <summary>
/// Routes a command invocation to its handler: a registered Core handler for a
/// <see cref="CommandLocation.Core"/> command, or the host-supplied <see cref="WebInvoker"/> for a
/// <see cref="CommandLocation.Web"/> command. Both <c>runCommand</c> (MCP) and the web's
/// <c>invoke-command</c> bridge message land here. <see cref="CommandRegistry"/> is the catalog; this is the
/// behavior.
/// </summary>
public sealed class CommandDispatcher {
	private readonly Dictionary<string, Func<string?, CancellationToken, Task<CommandResult>>> _handlers =
		new(StringComparer.Ordinal);

	private readonly Lock _gate = new();

	/// <summary>Creates a dispatcher over <paramref name="registry"/>.</summary>
	public CommandDispatcher(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		Registry = registry;
	}

	/// <summary>The catalog this dispatcher routes against.</summary>
	public CommandRegistry Registry { get; }

	/// <summary>
	/// The host's invoker for web commands (posts <c>run-command</c> + awaits <c>command-ack</c>). Null until
	/// the host wires it; a web command invoked before it is wired fails loudly rather than silently dropping.
	/// </summary>
	public WebCommandInvoker? WebInvoker { get; set; }

	/// <summary>
	/// Registers the Core handler for a <see cref="CommandLocation.Core"/> command. Throws if the id is
	/// unregistered, runs in the web, or already has a handler. Returns a disposable that unregisters it.
	/// </summary>
	public IDisposable RegisterHandler(string id, Func<string?, CancellationToken, Task<CommandResult>> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		var definition = Registry.Require(id);
		if (definition.RunsIn != CommandLocation.Core) {
			throw new InvalidOperationException(
				$"Command '{id}' runs in {definition.RunsIn}; only Core commands take a Core handler.");
		}

		lock (_gate) {
			if (!_handlers.TryAdd(id, handler)) {
				throw new InvalidOperationException($"Command '{id}' already has a handler.");
			}
		}

		return new Registration(() => {
			lock (_gate) {
				_handlers.Remove(id);
			}
		});
	}

	/// <summary>
	/// Runs the command <paramref name="id"/> with optional raw-JSON <paramref name="argsJson"/>. Throws
	/// <see cref="UnknownCommandException"/> for an unregistered id; otherwise returns the handler's result
	/// (or a failure when no handler/web connection is available). <c>when</c> guards are not evaluated here —
	/// programmatic invocation always runs.
	/// </summary>
	public async Task<CommandResult> InvokeAsync(string id, string? argsJson, CancellationToken ct) {
		var definition = Registry.Require(id);
		if (definition.RunsIn == CommandLocation.Core) {
			Func<string?, CancellationToken, Task<CommandResult>>? handler;
			lock (_gate) {
				_handlers.TryGetValue(id, out handler);
			}

			return handler is null
				? CommandResult.Failure($"Command '{id}' has no handler registered.")
				: await handler(argsJson, ct).ConfigureAwait(false);
		}

		var invoker = WebInvoker;
		return invoker is null
			? CommandResult.Failure($"Command '{id}' runs in the web UI, which isn't connected.")
			: await invoker(id, argsJson, ct).ConfigureAwait(false);
	}

	private sealed class Registration : IDisposable {
		private Action? _unregister;

		public Registration(Action unregister) {
			_unregister = unregister;
		}

		public void Dispose() {
			_unregister?.Invoke();
			_unregister = null;
		}
	}
}
