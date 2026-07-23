using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Lsp;
using Weavie.Core.Processes;

namespace Weavie.Hosting;

// The install-a-missing-language-server flow: each session's failed lsp-starts feed the workspace's
// ServerInstallOffers (surfacing the install suggestion cards), and weavie.lsp.installServer fulfills an
// offer by installing into ~/.weavie/tools — deterministic, zero model tokens, never the user's global
// toolset. See docs/concepts/suggestions.md.
public sealed partial class HostCore {
	private readonly ServerInstallOffers _installOffers = new(ServerResolver.FindOnPath, ServerResolver.Resolve);
	private readonly LanguageServerInstaller _serverInstaller = new(
		ServerResolver.FindOnPath, ServerResolver.Resolve, ToolProcess.RunAsync, Log);
	private int _installingServers; // single-flight: one install run at a time, across sessions

	// A failed lsp-start: record the miss (gated on an honorable recipe) and surface its card.
	private void OnLspUnresolved(LanguageServerDescriptor descriptor) {
		if (_installOffers.RecordUnresolved(descriptor)) {
			_suggestions?.Evaluate();
		}
	}

	private async Task<CommandResult> InstallLanguageServersAsync(string? argsJson, CancellationToken ct) {
		if (!TryParseServerArg(argsJson, out string? serverId, out string? error)) {
			return CommandResult.Failure(error!);
		}

		IReadOnlyList<ServerInstallOffer> offers;
		if (serverId is null) {
			offers = _installOffers.Active;
			if (offers.Count == 0) {
				return CommandResult.Failure(
					"No language server is currently offered for install — every supported one either resolves already or its toolchain is missing.");
			}
		} else if (_installOffers.For(serverId) is { } offer) {
			offers = [offer];
		} else {
			return CommandResult.Failure(
				$"No install offer for '{serverId}' — it already resolves, its toolchain is missing, or no file of that language has needed it yet.");
		}

		if (Interlocked.CompareExchange(ref _installingServers, 1, 0) != 0) {
			return CommandResult.Failure("A language server install is already running.");
		}

		try {
			var messages = new List<string>();
			bool allOk = true;
			foreach (var offer in offers) {
				// A busy toast persists for the whole (possibly minutes-long) install. On success it just clears —
				// the command-feedback toast of the returned summary is the single success surface (a keyed info here
				// would double it). A failure replaces it keyed with a persistent error carrying the tool's words.
				string key = $"lsp-install-{offer.Descriptor.Id}";
				_ui.Post(() => Notify("busy", $"Installing {offer.Candidate.Command} into Weavie's tools folder… ({offer.Recipe.Toolchain})", key));
				var result = await _serverInstaller.InstallAsync(offer, WorkspaceRoot, ct).ConfigureAwait(false);
				_ui.Post(() => {
					if (result.Ok) {
						ClearNotify(key);
					} else {
						Notify("error", result.Message, key);
					}
				});
				messages.Add(result.Message);
				allOk &= result.Ok;
			}

			if (_installOffers.Recompute()) {
				_ui.Post(() => {
					_suggestions?.Evaluate();
					// Re-advertise the active session's LSP catalog: the page rebinds its clients and re-issues
					// lsp-start for open models, so the just-installed server starts serving without a restart.
					if (_session is { } active) {
						PushLspConfigToWeb(active);
					}
				});
			}

			string summary = string.Join(" ", messages);
			return allOk ? CommandResult.Success(summary) : CommandResult.Failure(summary);
		} finally {
			Interlocked.Exchange(ref _installingServers, 0);
		}
	}

	// Parses {server?}. Absent/empty args is valid (install every offer); malformed JSON is a loud failure,
	// never a silent fall-through that could install something other than asked.
	private static bool TryParseServerArg(string? argsJson, out string? server, out string? error) {
		server = null;
		error = null;
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return true;
		}

		try {
			using var doc = JsonDocument.Parse(argsJson);
			server = doc.RootElement.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String
				? s.GetString()
				: null;
			return true;
		} catch (JsonException ex) {
			error = $"Could not install: invalid command arguments ({ex.Message}).";
			return false;
		}
	}
}
