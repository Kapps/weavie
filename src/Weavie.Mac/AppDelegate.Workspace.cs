using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Foundation;
using Weavie.Core.Lsp;

namespace Weavie.Mac;

// Workspace + chrome wiring for the app: the __WEAVIE_SHELL__ / __WEAVIE_LSP__ config builders, the
// keybinding-chord lookup that feeds the native menu, and File > Open Folder / Open Recent (a workspace
// switch via app relaunch). Split from AppDelegate.cs so each file holds one concern.
public sealed partial class AppDelegate {
	/// <summary>
	/// Builds the <c>window.__WEAVIE_SHELL__</c> config: the macOS title-bar mode (the omnibar strip), the
	/// workspace label, and the recents for File ▸ Open Recent. Hand-built (JsonSerializer.Serialize is
	/// trim-unsafe — IL2026 — on the macOS target).
	/// </summary>
	private string BuildShellConfigJson(string workspace) {
		var recents = _recents?.Items ?? [];
		var sb = new StringBuilder("{\"platform\":\"mac\",\"titleBar\":\"mac\",\"workspaceLabel\":");
		sb.Append(JsonString(WorkspaceLabel(workspace))).Append(",\"recents\":[");
		for (int i = 0; i < recents.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append(JsonString(recents[i]));
		}

		sb.Append("]}");
		return sb.ToString();
	}

	/// <summary>
	/// Builds the <c>window.__WEAVIE_LSP__</c> discovery payload (loopback WS url + per-session token +
	/// workspace + the language-server catalog). Hand-built for the same trim-safety reason as the rest of
	/// the macOS JSON; <c>DefaultSettingsJson</c> is already valid JSON, so it's embedded verbatim.
	/// </summary>
	private static string BuildLspConfigJson(int port, string token, string workspace) {
		var sb = new StringBuilder("{\"url\":");
		sb.Append(JsonString($"ws://127.0.0.1:{port}"))
			.Append(",\"token\":").Append(JsonString(token))
			.Append(",\"workspace\":").Append(JsonString(workspace))
			.Append(",\"servers\":[");
		bool first = true;
		foreach (var descriptor in LanguageServerCatalog.All) {
			if (!first) {
				sb.Append(',');
			}

			first = false;
			sb.Append("{\"id\":").Append(JsonString(descriptor.Id)).Append(",\"languageIds\":[");
			for (int i = 0; i < descriptor.LanguageIds.Count; i++) {
				if (i > 0) {
					sb.Append(',');
				}

				sb.Append(JsonString(descriptor.LanguageIds[i]));
			}

			sb.Append("],\"settings\":")
				.Append(string.IsNullOrEmpty(descriptor.DefaultSettingsJson) ? "null" : descriptor.DefaultSettingsJson)
				.Append('}');
		}

		sb.Append("]}");
		return sb.ToString();
	}

	/// <summary>The effective chord for a command id (the first non-global resolved binding), or null if unbound.</summary>
	private string? ResolveChord(string commandId) =>
		_keybindings?.Resolved.FirstOrDefault(binding => binding.Command == commandId && !binding.Global)?.Key;

	/// <summary>The folder's leaf name for the window title / shell label (e.g. <c>weavie</c> for <c>/src/weavie</c>).</summary>
	private static string WorkspaceLabel(string root) {
		string leaf = Path.GetFileName(root.TrimEnd('/'));
		return string.IsNullOrEmpty(leaf) ? root : leaf;
	}

	/// <summary>
	/// Shows the native "open folder as workspace" picker (File ▸ Open Folder). The chosen folder becomes
	/// the workspace on the next launch — see <see cref="SwitchWorkspace"/>.
	/// </summary>
	private void OpenFolderInteractive() {
		var panel = NSOpenPanel.OpenPanel;
		panel.Title = "Open Folder";
		panel.CanChooseFiles = false;
		panel.CanChooseDirectories = true;
		panel.AllowsMultipleSelection = false;
		panel.CanCreateDirectories = true;
		if (panel.RunModal() == 1 && panel.Url is { Path: { } chosen }) {
			SwitchWorkspace(chosen);
		}
	}

	/// <summary>
	/// Switches the workspace to <paramref name="path"/>: records it in recents, persists it as the
	/// <c>workspace</c> setting, and relaunches the app (a fresh instance opens the new folder, then this one
	/// exits). v1 hosts a single workspace per process, so a switch is a clean relaunch rather than an
	/// in-place teardown of the terminals / MCP / LSP.
	/// </summary>
	private void SwitchWorkspace(string path) {
		if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) {
			Notify("error", $"Folder not found: {path}");
			return;
		}

		if (Path.TrimEndingDirectorySeparator(path) == Path.TrimEndingDirectorySeparator(_workspace ?? string.Empty)) {
			return; // already this workspace
		}

		_recents?.Add(path);
		_settings?.Set("workspace", JsonDocument.Parse(JsonString(path)).RootElement.Clone());
		RelaunchApp();
	}

	/// <summary>Launches a fresh instance of the app bundle (which reads the updated workspace) and quits this one.</summary>
	private static void RelaunchApp() {
		string bundlePath = NSBundle.MainBundle.BundlePath;
		try {
			var startInfo = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
			startInfo.ArgumentList.Add("-n"); // open a new instance even though one is running
			startInfo.ArgumentList.Add(bundlePath);
			Process.Start(startInfo);
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Console.Error.WriteLine($"[weavie] relaunch failed: {ex.Message}");
		}

		NSApplication.SharedApplication.Terminate(null);
	}
}
