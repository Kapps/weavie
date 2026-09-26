using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core.Configuration;
using Weavie.Core.Theming;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// The app-wide host features every page gets, with or without a workspace: themes, settings writes, the agent
/// defaults, and ACP installs, plus the live pushes when those change. A workspace's <see cref="HostCore"/> and the
/// welcome screen each attach one to their own message bus; dispose to detach from the app-global stores.
/// </summary>
internal sealed class GlobalHostFeatures : IDisposable {
	private readonly HostMessageBus _host;
	private readonly HostServices _services;
	private readonly Action<string> _log;
	private readonly List<IDisposable> _handlers = [];
	private readonly CancellationTokenSource _lifetime = new();

	public GlobalHostFeatures(HostMessageBus host, HostServices services, Action<string> log) {
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(log);
		_host = host;
		_services = services;
		_log = log;
		WireThemes();
		WireSettings();
		WireAgents();
		_handlers.Add(_host.Feature("diagnostics").Handle<WebLogMessage>("log", (message, _) => {
			_log($"[web:{message.Level}] {message.Message}");
			return Task.CompletedTask;
		}));
		_services.Settings.SettingChanged += OnSettingChanged;
		_services.ThemeOverrides.Changed += OnThemeOverridesChanged;
		_services.Keybindings.KeybindingsChanged += PushCommandCatalog;
		_services.AgentProviders.Changed += PushAgentDefaults;
	}

	/// <summary>The page globals every Weavie page reads: agent defaults, resolved theme, commands, and keybindings.</summary>
	public string BootstrapScript() =>
		$"window.__WEAVIE_AGENT__ = {AgentDefaultsJson()};"
		+ $"window.__WEAVIE_THEME__ = {ThemeJson()};"
		+ $"window.__WEAVIE_COMMANDS__ = {_services.Keybindings.BuildCommandsJson()};"
		+ $"window.__WEAVIE_KEYBINDINGS__ = {_services.Keybindings.BuildKeybindingsJson()};";

	public string AgentDefaultsJson() => AgentSettings.BuildJson(
		_services.Settings,
		[.. _services.AgentProviders.Providers.Select(provider => provider.Info)]);

	/// <summary>Makes <paramref name="providerId"/> the default for new sessions when it names an installed provider.</summary>
	public void RememberDefaultProvider(string? providerId) {
		string? provider = providerId?.Trim();
		if (!string.IsNullOrEmpty(provider) && _services.AgentProviders.FindInfo(provider) is not null) {
			_services.Settings.Set(AgentSettings.DefaultProvider, JsonSerializer.SerializeToElement(provider));
		}
	}

	private string ThemeJson() => Weavie.Core.Theming.ThemeJson.Build(_services.Settings, _services.ThemeOverrides, _log);

	private void WireThemes() {
		var themes = _host.Feature("themes");
		_handlers.Add(themes.Handle<EmptyRequest, IReadOnlyList<ThemeChoice>>(
			"list", (_, _) => Task.FromResult(ThemeCatalog.List())));
		_handlers.Add(themes.Handle<ThemeIdRequest, JsonElement>("preview", (message, _) =>
			Task.FromResult(Weavie.Core.Theming.ThemeJson.PreviewSlot(message.Id, _services.ThemeOverrides))));
		_handlers.Add(themes.Handle<ThemeIdRequest, CommandWireResult>("select", (message, _) =>
			Task.FromResult(CommandWireResult.From(ThemeCommands.SelectTheme(
				JsonSerializer.Serialize(new { id = message.Id }),
				_services.Settings)))));
		_handlers.Add(themes.HandleConcurrent<ThemeSearchRequest, JsonElement>("search", async (message, ct) => {
			using var http = new HttpClient();
			return await new OpenVsxThemeInstaller(http, OpenVsxThemeInstaller.DefaultRegistry)
				.SearchAsync(message.Query, message.Offset, message.SortBy, ct).ConfigureAwait(false);
		}));
		_handlers.Add(themes.HandleConcurrent<ThemeExtensionRequest, CommandWireResult>("install", async (message, ct) =>
			CommandWireResult.From(await ThemeCommands.InstallFromOpenVsxAsync(
				JsonSerializer.Serialize(new { @namespace = message.Namespace, name = message.Name, version = message.Version }),
				_services.Settings,
				ct).ConfigureAwait(false))));
		_handlers.Add(themes.HandleConcurrent<ThemeExtensionRequest, IReadOnlyList<ThemePreview>>(
			"previewExtension",
			async (message, ct) => {
				using var http = new HttpClient();
				return await new OpenVsxThemeInstaller(http, OpenVsxThemeInstaller.DefaultRegistry)
					.PreviewAsync(message.Namespace, message.Name, message.Version, _services.ThemeOverrides, ct)
					.ConfigureAwait(false);
			}));
	}

	private void WireSettings() {
		var settings = _host.Feature("settings");
		_handlers.Add(settings.Handle<SettingRead, JsonElement>(
			"get", (message, _) => Task.FromResult(ParseJson(_services.Settings.BuildGetJson(message.Key)))));
		_handlers.Add(settings.Handle<SettingWrite>("set", (message, _) => {
			var result = _services.Settings.Set(message.Key, message.Value);
			return result.ShadowedByEnv is { } variable
				? throw new InvalidOperationException(
					$"{message.Key} is overridden by the {variable} environment variable, so this change has no effect until it's unset.")
				: Task.CompletedTask;
		}));
	}

	private void WireAgents() {
		var agentDefaults = _host.Feature("agentDefaults");
		_handlers.Add(agentDefaults.Handle<EmptyRequest, JsonElement>(
			"get", (_, _) => Task.FromResult(ParseJson(AgentDefaultsJson()))));
		_handlers.Add(agentDefaults.Handle<AgentProviderRequest, JsonElement>("setProvider", (message, _) => {
			RememberDefaultProvider(message.ProviderId);
			return Task.FromResult(ParseJson(AgentDefaultsJson()));
		}));

		var acpRegistry = _host.Feature("acpRegistry");
		_handlers.Add(acpRegistry.Handle<EmptyRequest, IReadOnlyList<AcpRegistryAgent>>(
			"list", (_, ct) => _services.AcpAgents.ListRegistryAsync(ct)));
		// A first npx/uvx start downloads the agent, which can outlast a request, so installs answer at once and
		// report through "installed" when the check finishes.
		_handlers.Add(acpRegistry.Handle<AcpInstallMessage>("install", (message, ct) => {
			_ = InstallAsync(message);
			return Task.CompletedTask;
		}));
	}

	private async Task InstallAsync(AcpInstallMessage message) {
		string? error = null;
		try {
			await _services.AcpAgents.InstallAsync(
				message.Id,
				message.Distribution,
				(launch, ct) => Weavie.AgentClientProtocol.AcpAgentCheck.VerifyAsync(
					Agents.AgentProviderComposition.Definition(launch),
					ct),
				_lifetime.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
			return;
		} catch (Exception ex) {
			error = ex.Message;
			_log($"[acp] installing {message.Id} ({message.Distribution}) failed: {ex}");
		}

		if (!_lifetime.IsCancellationRequested) {
			_host.Feature("acpRegistry").Publish("installed", new InstallResult(message.Id, error));
		}
	}

	private void OnSettingChanged(SettingChange change) {
		if (AgentSettings.Keys.Contains(change.Key)) {
			PushAgentDefaults();
		}

		if (ThemeSettings.Keys.Contains(change.Key)) {
			PushTheme();
		}
	}

	private void OnThemeOverridesChanged(string themeId) {
		if (ThemeSettings.IsSelectedThemeId(_services.Settings, themeId)) {
			PushTheme();
		}
	}

	private void PushTheme() => _host.Feature("settings").PublishJson("theme", ThemeJson());

	/// <summary>Re-pushes the agent defaults, e.g. once a PATH import may have changed which agents resolve.</summary>
	public void PushAgentDefaults() => _host.Feature("settings").PublishJson("agent-defaults", AgentDefaultsJson());

	private void PushCommandCatalog() => _host.Feature("commands").PublishJson(
		"catalog",
		$"{{\"commands\":{_services.Keybindings.BuildCommandsJson()},"
		+ $"\"keybindings\":{_services.Keybindings.BuildKeybindingsJson()}}}");

	private static JsonElement ParseJson(string json) {
		using var document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	/// <inheritdoc/>
	public void Dispose() {
		_lifetime.Cancel();
		_services.Settings.SettingChanged -= OnSettingChanged;
		_services.ThemeOverrides.Changed -= OnThemeOverridesChanged;
		_services.Keybindings.KeybindingsChanged -= PushCommandCatalog;
		_services.AgentProviders.Changed -= PushAgentDefaults;
		foreach (var handler in _handlers) {
			handler.Dispose();
		}
	}

	private sealed record EmptyRequest;
	private sealed record InstallResult(string Id, string? Error);
	private sealed record WebLogMessage(string Level, string Message);
	private sealed record SettingRead(string Key);
	private sealed record SettingWrite(string Key, JsonElement Value);
	private sealed record AgentProviderRequest(string ProviderId);
	private sealed record ThemeIdRequest(string Id);
	private sealed record ThemeSearchRequest(string Query, int Offset, string SortBy);
	private sealed record ThemeExtensionRequest(string Namespace, string Name, string Version);
}
