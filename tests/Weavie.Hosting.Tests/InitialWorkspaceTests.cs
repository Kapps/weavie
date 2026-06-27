using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The launch workspace resolution shared by every GUI host: last-opened, else the explicit <c>workspace</c>
/// setting, else <c>null</c> (the welcome screen). Pins that an unset workspace never silently resolves to the
/// home directory.
/// </summary>
public sealed class InitialWorkspaceTests {
	[Fact]
	public void NoHistoryAndNoSetting_IsNull() {
		using var h = new Harness();
		Assert.Null(InitialWorkspace.Resolve(h.Settings, h.Recents));
	}

	[Fact]
	public void LastOpenedThatExists_Wins() {
		using var h = new Harness();
		string dir = h.MakeDir();
		h.Recents.Add(dir);
		Assert.Equal(dir, InitialWorkspace.Resolve(h.Settings, h.Recents));
	}

	[Fact]
	public void MissingLastOpened_FallsToExplicitSetting() {
		using var h = new Harness();
		string gone = h.MakeDir();
		h.Recents.Add(gone); // most-recent entry…
		Directory.Delete(gone, recursive: true); // …whose folder is now missing

		string configured = h.MakeDir();
		h.SetWorkspace(configured);
		Assert.Equal(configured, InitialWorkspace.Resolve(h.Settings, h.Recents));
	}

	[Fact]
	public void NoSetting_DoesNotFallBackToHome() {
		using var h = new Harness();
		// The whole point: an unset workspace is the empty state, not the user's home directory.
		Assert.Null(InitialWorkspace.Resolve(h.Settings, h.Recents));
	}

	private sealed class Harness : IDisposable {
		private readonly string _settingsPath;
		private readonly List<string> _dirs = [];

		public Harness() {
			_settingsPath = Path.Combine(Path.GetTempPath(), "weavie-iw-" + Guid.NewGuid().ToString("n") + ".toml");
			Settings = CoreSettings.CreateStore(_settingsPath, enableWatcher: false);
			Recents = new RecentWorkspaces(
				new LocalFileSystem(),
				Path.Combine(Path.GetTempPath(), "weavie-iw-recents-" + Guid.NewGuid().ToString("n") + ".json"));
		}

		public SettingsStore Settings { get; }
		public RecentWorkspaces Recents { get; }

		public string MakeDir() {
			string dir = Directory.CreateDirectory(
				Path.Combine(Path.GetTempPath(), "weavie-iw-ws-" + Guid.NewGuid().ToString("n"))).FullName;
			_dirs.Add(dir);
			return dir;
		}

		public void SetWorkspace(string dir) =>
			Settings.Set("workspace", JsonDocument.Parse("\"" + JsonEncodedText.Encode(dir) + "\"").RootElement.Clone());

		public void Dispose() {
			Settings.Dispose();
			foreach (string dir in _dirs) {
				try {
					Directory.Delete(dir, recursive: true);
				} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					// best-effort temp cleanup
				}
			}

			try {
				File.Delete(_settingsPath);
			} catch (IOException) {
				// best-effort
			}
		}
	}
}
