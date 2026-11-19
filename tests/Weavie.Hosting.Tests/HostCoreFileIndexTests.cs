using System.Text;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A page gets a session's file list as one download and every later change as its own small message, each a
/// single step from the revision before it.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreFileIndexTests {
	private static async Task<JsonElement[]> DownloadAsync(TestHost host, HostSession session, long? knownRevision) {
		using var output = new MemoryStream();
		Assert.True(await host.Core.WriteFileIndexAsync(session.Address, knownRevision, () => output, CancellationToken.None));
		return [.. Encoding.UTF8.GetString(output.ToArray())
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => JsonSerializer.Deserialize<JsonElement>(line))];
	}

	private static string[] Paths(IEnumerable<JsonElement> batches) =>
		[.. batches.SelectMany(batch => batch.GetProperty("paths").EnumerateArray()).Select(path => path.GetString()!)];

	private static string[] Strings(JsonElement change, string name) =>
		[.. change.GetProperty(name).EnumerateArray().Select(path => path.GetString()!)];

	[Fact]
	public async Task TheListDownloadsAsRelativePathsAndAnswersUnchangedForTheRevisionInHand() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		File.WriteAllText(Path.Combine(session.WorkspaceRoot, "nested-probe.txt"), "");
		Directory.CreateDirectory(Path.Combine(session.WorkspaceRoot, "src"));
		File.WriteAllText(Path.Combine(session.WorkspaceRoot, "src", "probe.cs"), "");
		string probe = Path.Combine(session.WorkspaceRoot, "src", "probe.cs");
		await Wait.UntilAsync(() => session.Inventory.LastSnapshot?.Files.Contains(probe) == true);

		var batches = await DownloadAsync(host, session, null);

		string[] paths = Paths(batches);
		Assert.Contains("nested-probe.txt", paths);
		Assert.Contains("src/probe.cs", paths);
		Assert.All(batches, batch => Assert.Equal(paths.Length, batch.GetProperty("count").GetInt32()));
		Assert.True(batches[^1].GetProperty("complete").GetBoolean());
		Assert.Single(batches, batch => batch.GetProperty("complete").GetBoolean());
		long revision = batches[^1].GetProperty("revision").GetInt64();
		var unchanged = Assert.Single(await DownloadAsync(host, session, revision));
		Assert.True(unchanged.GetProperty("unchanged").GetBoolean());
		Assert.Empty(Paths([unchanged]));
		Assert.False(Assert.Single(await DownloadAsync(host, session, revision - 1), batch => batch.GetProperty("complete").GetBoolean())
			.GetProperty("unchanged").GetBoolean());
	}

	[Fact]
	public async Task ACreatedAndThenDeletedFilePublishesTwoStepsAndNeverTheList() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		long revision = (await DownloadAsync(host, session, null))[^1].GetProperty("revision").GetInt64();
		host.Bridge.Clear();
		string file = Path.Combine(session.WorkspaceRoot, "docs", "step-probe.md");

		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, "");
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "indexChanged").Any(change => Strings(change, "added").Length > 0));
		File.Delete(file);
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "indexChanged").Any(change => Strings(change, "removed").Length > 0));

		var changes = host.Bridge.PostedEvents("files", "indexChanged");
		Assert.Equal(
			Enumerable.Range(1, changes.Count).Select(step => revision + step),
			changes.Select(change => change.GetProperty("revision").GetInt64()));
		Assert.Equal(["docs/step-probe.md"], changes.SelectMany(change => Strings(change, "added")));
		Assert.Equal(["docs/step-probe.md"], changes.SelectMany(change => Strings(change, "removed")));
		Assert.Empty(host.Bridge.PostedEvents("files", "indexReplaced"));
		long current = changes[^1].GetProperty("revision").GetInt64();
		Assert.True(Assert.Single(await DownloadAsync(host, session, current)).GetProperty("unchanged").GetBoolean());
	}

	[Fact]
	public async Task AChangeTooLargeToStepTellsThePageToDownloadAgain() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		await DownloadAsync(host, session, null);
		using var staging = new TempDirectory("weavie-index-burst");
		for (int index = 0; index <= HostCore.MaxFileIndexChangePaths; index++) {
			File.WriteAllText(staging.Combine($"f{index}.txt"), "");
		}
		host.Bridge.Clear();

		// One rename lists the whole folder in a single refresh, so it is one change.
		Directory.Move(staging.Path, Path.Combine(session.WorkspaceRoot, "burst"));

		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "indexReplaced").Count > 0);
		long revision = host.Bridge.PostedEvents("files", "indexReplaced")[^1].GetProperty("revision").GetInt64();
		Assert.DoesNotContain(host.Bridge.PostedEvents("files", "indexChanged"), change => Strings(change, "added").Length > 0);
		var batches = await DownloadAsync(host, session, null);
		Assert.True(batches[^1].GetProperty("revision").GetInt64() >= revision);
		Assert.Equal(HostCore.MaxFileIndexChangePaths + 1, Paths(batches).Count(path => path.StartsWith("burst/", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task APageSyncCarriesTheWorkspaceAndNeverTheList() {
		await using var host = await TestHost.StartAsync();

		var workspace = host.Bridge.PostedEvents("files", "workspace")[^1];

		Assert.Equal(Path.GetFullPath(host.WorkspaceSession.WorkspaceRoot), workspace.GetProperty("root").GetString());
		Assert.Equal(Path.DirectorySeparatorChar.ToString(), workspace.GetProperty("separator").GetString());
		Assert.Empty(host.Bridge.PostedEvents("files", "index"));
	}

	[Fact]
	public async Task AnAddressNoLoadedSessionHasFindsNothing() {
		await using var host = await TestHost.StartAsync();
		var stale = new Messaging.SessionAddress(host.WorkspaceSession.Address.Slot, "not-this-one");

		Assert.False(await host.Core.WriteFileIndexAsync(
			stale, null, () => throw new InvalidOperationException("Nothing is written for an address nothing has."), CancellationToken.None));
	}
}
