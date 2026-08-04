namespace Weavie.Core.Commands;

/// <summary>Host-supplied invoker for a <see cref="CommandLocation.Web"/> command through its attached session view.</summary>
public delegate Task<CommandResult> WebCommandInvoker(string id, string? argsJson, CancellationToken ct);

/// <summary>Host-supplied relay from a model backend to the local presentation host.</summary>
public delegate Task<CommandResult> ClientCommandInvoker(string id, string? argsJson, CancellationToken ct);

/// <summary>
/// Routes a command invocation to its handler: a registered Core handler, or the host-supplied
/// <see cref="WebInvoker"/> for a <see cref="CommandLocation.Web"/> command. Model calls enter through
/// <see cref="PrepareModelInvocationAsync"/> so client-owned Core commands reach the presentation host;
/// web <c>commands.invoke</c> requests use the direct path. <see cref="CommandRegistry"/> is the catalog.
/// </summary>
public sealed class CommandDispatcher {
	private readonly Dictionary<string, Func<string?, CommandInvocationContext, CancellationToken, Task<CommandResult>>> _handlers =
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
	/// The host's invoker for web commands. Null until the host wires it; a web command invoked before then
	/// fails loudly rather than silently dropping.
	/// </summary>
	public WebCommandInvoker? WebInvoker { get; set; }

	/// <summary>
	/// The host's relay for model-invoked, client-owned Core commands. Null until a presentation client is
	/// attached; invocation then fails loudly instead of mutating an invisible backend setting.
	/// </summary>
	public ClientCommandInvoker? ClientInvoker { get; set; }

	/// <summary>
	/// Registers the Core handler for a <see cref="CommandLocation.Core"/> command (throws if the id is
	/// unregistered, web-run, or already handled). Returns a disposable that unregisters it.
	/// </summary>
	public IDisposable RegisterHandler(string id, Func<string?, CancellationToken, Task<CommandResult>> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		return RegisterContextualHandler(id, (args, _, ct) => handler(args, ct));
	}

	/// <summary>
	/// Registers a Core handler that may defer endpoint-destroying work until its result has been delivered.
	/// </summary>
	public IDisposable RegisterContextualHandler(
		string id,
		Func<string?, CommandInvocationContext, CancellationToken, Task<CommandResult>> handler) {
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
	/// Runs command <paramref name="id"/> with optional raw-JSON <paramref name="argsJson"/>; throws
	/// <see cref="UnknownCommandException"/> for an unregistered id. <c>when</c> guards are not evaluated here —
	/// programmatic invocation always runs.
	/// </summary>
	public async Task<CommandResult> InvokeAsync(string id, string? argsJson, CancellationToken ct) {
		var execution = await PrepareAsync(id, argsJson, ct).ConfigureAwait(false);
		await execution.CompleteAsync(ct).ConfigureAwait(false);
		return execution.Result;
	}

	/// <summary>
	/// Runs the handler and returns its result plus a completion boundary. Transports deliver
	/// <see cref="CommandExecution.Result"/> before calling <see cref="CommandExecution.CompleteAsync"/>.
	/// </summary>
	public async Task<CommandExecution> PrepareAsync(string id, string? argsJson, CancellationToken ct) {
		var definition = Registry.Require(id);
		var context = new CommandInvocationContext();
		CommandResult result;
		if (definition.RunsIn == CommandLocation.Core) {
			Func<string?, CommandInvocationContext, CancellationToken, Task<CommandResult>>? handler;
			lock (_gate) {
				_handlers.TryGetValue(id, out handler);
			}

			result = handler is null
				? CommandResult.Failure($"Command '{id}' has no handler registered.")
				: await handler(argsJson, context, ct).ConfigureAwait(false);
		} else {
			var invoker = WebInvoker;
			result = invoker is null
				? CommandResult.Failure($"Command '{id}' runs in the web UI, which isn't connected.")
				: await invoker(id, argsJson, ct).ConfigureAwait(false);
		}

		return new CommandExecution(result, context.Seal());
	}

	/// <summary>
	/// Prepares a model-facing invocation. Client-owned Core commands relay through the presentation client;
	/// backend-owned and web commands follow the normal dispatcher path.
	/// </summary>
	public async Task<CommandExecution> PrepareModelInvocationAsync(
		string id,
		string? argsJson,
		CancellationToken ct) {
		var definition = Registry.Require(id);
		if (definition is not { RunsIn: CommandLocation.Core, Owner: CommandOwner.Client }) {
			return await PrepareAsync(id, argsJson, ct).ConfigureAwait(false);
		}

		var invoker = ClientInvoker;
		var result = invoker is null
			? CommandResult.Failure(
				$"Command '{id}' belongs to the local presentation client, which isn't connected.")
			: await invoker(id, argsJson, ct).ConfigureAwait(false);
		return CommandExecution.Completed(result);
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
