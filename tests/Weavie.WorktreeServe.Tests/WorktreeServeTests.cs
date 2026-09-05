using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Remote;
using Xunit;

namespace Weavie.WorktreeServe.Tests;

public sealed class WorktreeServeTests {
	[Fact]
	public void Options_default_to_the_free_tailnet_preview_port() {
		var (options, error) = WorktreeServeOptions.Resolve([]);

		Assert.Null(error);
		Assert.NotNull(options);
		Assert.Equal(10000, options.HttpsPort);
		Assert.Null(options.Workspace);
		Assert.Null(options.StateRoot);
	}

	[Theory]
	[InlineData("--https-port", "0")]
	[InlineData("--https-port", "not-a-port")]
	[InlineData("--https-port", "443")]
	[InlineData("--https-port", "8443")]
	[InlineData("--unknown", "value")]
	public void Invalid_options_fail_loudly(string name, string value) {
		var (options, error) = WorktreeServeOptions.Resolve([name, value]);

		Assert.Null(options);
		Assert.NotNull(error);
	}

	[Fact]
	public void Existing_runner_routes_leave_only_the_preview_port_available() {
		var status = TailscaleServeStatus.Parse(
			"""
			{
			  "TCP": { "443": { "HTTPS": true }, "8443": { "HTTPS": true } },
			  "Web": {
			    "box.tail.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:8800" } } },
			    "box.tail.ts.net:8443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:8701" } } }
			  }
			}
			""");

		Assert.True(status.PortIsOccupied(443));
		Assert.True(status.PortIsOccupied(8443));
		Assert.False(status.PortIsOccupied(10000));
	}

	[Fact]
	public void Exact_route_identity_checks_protocol_host_port_path_and_target() {
		var status = TailscaleServeStatus.Parse(
			"""
			{
			  "TCP": { "10000": { "HTTPS": true } },
			  "Web": {
			    "box.tail.ts.net:10000": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:32123" } } }
			  }
			}
			""");

		Assert.True(status.IsExactHttpsProxy("box.tail.ts.net", 10000, "http://127.0.0.1:32123"));
		Assert.False(status.IsExactHttpsProxy("box.tail.ts.net", 10000, "http://127.0.0.1:32124"));
		Assert.False(status.IsExactHttpsProxy("other.tail.ts.net", 10000, "http://127.0.0.1:32123"));
	}

	[Fact]
	public void Foreground_routes_participate_in_occupancy_and_identity() {
		var status = TailscaleServeStatus.Parse(
			"""
			{
			  "TCP": {}, "Web": {},
			  "Foreground": {
			    "session": {
			      "TCP": { "10000": { "HTTPS": true } },
			      "Web": {
			        "box.tail.ts.net:10000": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:32123" } } }
			      }
			    }
			  }
			}
			""");

		Assert.True(status.PortIsOccupied(10000));
		Assert.True(status.IsExactHttpsProxy("box.tail.ts.net", 10000, "http://127.0.0.1:32123"));
	}

	[Fact]
	public void Any_web_handler_occupies_its_port() {
		var status = TailscaleServeStatus.Parse(
			"""{ "TCP": {}, "Web": { "box.tail.ts.net:10000": { "Handlers": { "/x": { "Text": "hi" } } } } }""");

		Assert.True(status.PortIsOccupied(10000));
	}

	[Fact]
	public async Task Headless_output_produces_a_fragment_only_one_click_url() {
		var readiness = new HeadlessReadiness();
		readiness.Accept("[weavie-headless] open  http://127.0.0.1:31234/index.html  in a browser");
		readiness.Accept("[weavie-headless] token a token&with#delimiters");

		var endpoint = await readiness.Ready;
		string url = endpoint.BrowserUrl("box.tail.ts.net", 10000);

		Assert.Equal("http://127.0.0.1:31234", endpoint.Target);
		Assert.StartsWith("https://box.tail.ts.net:10000/index.html#token=", url, StringComparison.Ordinal);
		Assert.DoesNotContain("?", url, StringComparison.Ordinal);
		Assert.Contains("a%20token%26with%23delimiters", url, StringComparison.Ordinal);
	}

	[Fact]
	public void Foreground_serve_arguments_have_no_persistent_background_flag() {
		Assert.Equal(
			["serve", "--bg=false", "--https=10000", "http://127.0.0.1:31234"],
			TailscaleServeSession.Arguments(10000, "http://127.0.0.1:31234"));
	}

	[Fact]
	public void Magic_dns_discovery_trims_the_status_suffix() {
		var cli = new FakeTailscaleCli(new TailscaleResult(0, "{\"Self\":{\"DNSName\":\"box.tail.ts.net.\"}}", ""));

		Assert.Equal("box.tail.ts.net", TailscaleServeSession.DiscoverMagicDns(cli));
	}

	[Fact]
	public void Port_lease_serializes_preview_launchers() {
		using var first = PortLease.Acquire(39876);

		Assert.Throws<InvalidOperationException>(() => PortLease.Acquire(39876));
	}

	[Fact]
	public void Production_state_store_is_never_accepted_for_a_preview() {
		string production = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie");

		Assert.Throws<InvalidOperationException>(() => WorktreeServeApp.RejectProductionState(production));
		Assert.Throws<InvalidOperationException>(
			() => WorktreeServeApp.RejectProductionState(Path.Combine(production, "workspaces", "existing"), production));
		Assert.Throws<InvalidOperationException>(
			() => WorktreeServeApp.RejectProductionState(Path.GetDirectoryName(production)!, production));
	}

	[Fact]
	public void New_empty_state_root_is_claimed_and_can_be_reopened() {
		using var temp = new TempDirectory("worktree-serve-claim");
		string root = temp.Combine("state"); // ClaimStateRoot must create it itself

		WorktreeServeApp.ClaimStateRoot(root);
		WorktreeServeApp.ClaimStateRoot(root);

		Assert.Single(Directory.EnumerateFiles(root));
	}

	[Fact]
	public void Existing_unowned_state_root_is_rejected_without_changing_it() {
		using var root = new TempDirectory("worktree-serve-unowned-state");
		string existing = root.WriteFile("settings.toml", "production");

		Assert.Throws<InvalidOperationException>(() => WorktreeServeApp.ClaimStateRoot(root.Path));

		Assert.Equal("production", File.ReadAllText(existing));
		Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));
	}

	[Fact]
	public void State_root_cannot_overlap_source_or_workspace_paths() {
		using var root = new TempDirectory("worktree-serve-overlap");
		string source = root.CreateDirectory("source");

		Assert.Throws<InvalidOperationException>(
			() => WorktreeServeApp.RejectStateOverlap(Path.Combine(source, "preview"), [source]));
		Assert.Throws<InvalidOperationException>(
			() => WorktreeServeApp.RejectStateOverlap(root.Path, [source]));
	}

	[Fact]
	public void Case_insensitive_filesystems_reject_production_state_aliases() {
		using var root = new TempDirectory("worktree-serve-case-test");
		string lower = root.CreateDirectory(".weavie");
		string upper = root.Combine(".WEAVIE", "workspaces");

		Assert.Equal(
			Directory.Exists(root.Combine(".WEAVIE")),
			PhysicalPath.IsSameOrDescendant(upper, lower));
	}

	[Fact]
	public void Case_distinct_directories_remain_distinct_when_the_volume_supports_them() {
		using var root = new TempDirectory("worktree-serve-case-sensitive-test");
		string lower = root.CreateDirectory("state");
		string upper = root.CreateDirectory("STATE");
		if (Directory.EnumerateDirectories(root.Path).Count() == 1) {
			return;
		}

		Assert.False(PhysicalPath.Equal(lower, upper));
		Assert.False(PhysicalPath.IsSameOrDescendant(lower, upper));
	}

	[Fact]
	public void Symlink_into_production_state_store_is_never_accepted_for_a_preview() {
		using var root = new TempDirectory("worktree-serve-state-test");
		string production = root.CreateDirectory("production");
		string workspace = root.CreateDirectory("production", "workspaces", "existing");
		string alias = root.Combine("preview");
		Directory.CreateSymbolicLink(alias, workspace);

		Assert.Throws<InvalidOperationException>(() => WorktreeServeApp.RejectProductionState(alias, production));
	}

	[Fact]
	public void Default_state_is_stable_per_source_checkout_and_separate_from_production() {
		string first = WorktreeServeApp.DefaultStateRoot("/source/one");
		string again = WorktreeServeApp.DefaultStateRoot("/source/one");
		string other = WorktreeServeApp.DefaultStateRoot("/source/two");

		Assert.Equal(first, again);
		Assert.NotEqual(first, other);
		Assert.Contains(".weavie-previews", first, StringComparison.Ordinal);
		Assert.Contains("worktree-serve", first, StringComparison.Ordinal);
	}

	[Fact]
	public void Primary_workspace_is_the_first_non_bare_git_worktree() {
		var primary = new GitWorktree { Path = "/repository", Head = "abc" };

		Assert.Same(primary, WorktreeServeApp.PrimaryWorktree([
			new GitWorktree { Path = "/bare", IsBare = true },
			primary,
			new GitWorktree { Path = "/linked", Head = "def" },
		]));
	}

	[Fact]
	public void Missing_primary_workspace_fails_loudly() => Assert.Throws<InvalidOperationException>(
		() => WorktreeServeApp.PrimaryWorktree([new GitWorktree { Path = "/bare", IsBare = true }]));

	[Theory]
	[InlineData("node-v22.23.2-linux-x64.tar.gz", "node-v22.23.2-linux-x64")]
	[InlineData("node-v22.23.2-win-x64.zip", "node-v22.23.2-win-x64")]
	public void Node_archive_root_preserves_the_dotted_version(string archive, string expected) => Assert.Equal(expected, NodeToolchain.ArchiveRootName(archive));

	private sealed class FakeTailscaleCli(TailscaleResult result) : ITailscaleCli {
		public string Executable => "tailscale";

		public IReadOnlyDictionary<string, string> ProcessEnvironment { get; } = new Dictionary<string, string>();

		public TailscaleResult Run(IReadOnlyList<string> args) => result;
	}
}
