using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpDistributionServiceTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-acp-distribution");

	[Fact]
	public async Task PackageDistributionsArePersistedAsExactLaunches() {
		var fileSystem = new InMemoryFileSystem();
		var handler = new RegistryHandler(PackageRegistry("1.2.3"));
		var service = Service(fileSystem, handler);
		var listed = Assert.Single(await service.ListRegistryAsync(CancellationToken.None));
		Assert.Equal(["npx", "uvx"], listed.Distributions);

		int changes = 0;
		service.Changed += () => changes++;
		await service.InstallAsync("sample", "npx", Accept, CancellationToken.None);

		var launch = Assert.Single(service.LaunchSpecs);
		Assert.Equal("node", launch.Command);
		Assert.Equal([InstalledScript(), "--stdio"], launch.Arguments);
		Assert.Equal("1", launch.Environment["SAMPLE_ACP"]);
		Assert.Equal("npx", launch.Distribution);
		Assert.Equal(1, changes);

		var reloaded = Service(fileSystem, handler);
		var persisted = Assert.Single(reloaded.LaunchSpecs);
		Assert.Equal(launch.Id, persisted.Id);
		Assert.Equal(launch.Command, persisted.Command);
		Assert.Equal(launch.Arguments, persisted.Arguments);
		Assert.Equal(launch.Environment, persisted.Environment);
		await reloaded.InstallAsync("sample", "uvx", Accept, CancellationToken.None);
		var uvx = Assert.Single(reloaded.LaunchSpecs);
		Assert.Equal("uvx", uvx.Command);
		Assert.Equal(["sample-acp==1.2.3", "--stdio"], uvx.Arguments);
	}

	[Fact]
	public async Task NpmInstallsRunTheUsersNpmOutsideEveryProject() {
		string project = _root.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "package.json"),
			"""{"devEngines":{"packageManager":{"name":"pnpm","version":"11.21.0","onFail":"error"}}}""");
		File.WriteAllText(Path.Combine(project, ".npmrc"), "registry=http://127.0.0.1:9/\n");
		string install = Directory.CreateDirectory(Path.Combine(project, "install")).FullName;

		WritePackageTarball(Path.Combine(install, "sample-acp.tgz"));

		await new NpmInstaller().InstallAsync("file:sample-acp.tgz", install, CancellationToken.None);

		Assert.Equal(
			Path.Combine(install, "node_modules", "@scope", "sample-acp", "bin", "acp.js"),
			AcpNpmPackage.Executable(install));
	}

	[Theory]
	[InlineData("""{"bin":"cli.js"}""", "cli.js")]
	[InlineData("""{"bin":{"other":"other.js"}}""", "other.js")]
	[InlineData("""{"bin":{"other":"other.js","sample-acp":"cli.js"}}""", "cli.js")]
	[InlineData("""{"bin":{"one":"cli.js","two":"cli.js"}}""", "cli.js")]
	public void NpmExecutableMatchesTheOneNpxWouldRun(string manifest, string script) {
		string install = WritePackage(_root.CreateDirectory("install"), manifest, script);

		Assert.Equal(Path.Combine(install, "node_modules", "sample-acp", script), AcpNpmPackage.Executable(install));
	}

	[Theory]
	[InlineData("""{"bin":{"one":"one.js","two":"cli.js"}}""")]
	[InlineData("""{"bin":"../outside.js"}""")]
	[InlineData("""{}""")]
	public void NpmPackagesWithoutOneContainedExecutableAreRejected(string manifest) {
		string install = WritePackage(_root.CreateDirectory("install"), manifest, "cli.js");

		Assert.Throws<InvalidDataException>(() => AcpNpmPackage.Executable(install));
	}

	[Theory]
	[InlineData("#!/usr/bin/env node", "node", new string[0])]
	[InlineData("#!/usr/bin/env -S node --no-warnings", "node", new[] { "--no-warnings" })]
	[InlineData("#!/bin/sh", "/bin/sh", new string[0])]
	public void NpmScriptsRunThroughTheirInterpreter(string shebang, string command, string[] options) {
		string script = _root.WriteFile("cli", shebang + "\nconsole.log(1)\n");

		var (launched, arguments) = AcpNpmPackage.Launch(script);

		Assert.Equal(command, launched);
		Assert.Equal([.. options, script], arguments);
	}

	[Fact]
	public void NativeNpmExecutablesRunDirectly() {
		string binary = _root.WriteFile("droid", "\u007fELF");

		Assert.Equal((binary, (IReadOnlyList<string>)[]), AcpNpmPackage.Launch(binary));
	}

	[Theory]
	[InlineData("@scope/sample@1.2.3")]
	[InlineData("sample-acp@1.2.3-beta.1+build.2")]
	public void NpmSpecsFromTheRegistryAreAccepted(string package) => AcpNpmPackage.ValidateSpec(package);

	[Theory]
	[InlineData("sample&calc")]
	[InlineData("--global")]
	[InlineData("sample acp")]
	[InlineData("sample@^1.2.3")]
	public void NpmSpecsThatCouldEscapeTheCommandLineAreRejected(string package) =>
		Assert.Throws<InvalidDataException>(() => AcpNpmPackage.ValidateSpec(package));

	[Fact]
	public void NpmInstallationsRequireTheirAbsoluteInstalledExecutable() {
		var store = new AcpInstallationStore(new InMemoryFileSystem(), _root.Combine("installed.json"));
		var launch = new AcpLaunchSpec {
			Id = "sample",
			Name = "Sample",
			Command = "node",
			Arguments = [_root.Combine("cli.js"), "--profile name"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Version = "1.2.3",
			Distribution = "npx",
		};

		store.Save([launch]);

		Assert.Equal(launch.Arguments, Assert.Single(store.Load()).Arguments);
		store.Save([launch with { Command = _root.Combine("droid"), Arguments = ["acp"] }]);
		Assert.Throws<JsonException>(() => store.Save([launch with { Command = "npx", Arguments = ["--yes", "sample-acp@1.2.3"] }]));
	}

	[Fact]
	public void CustomProfilesUseExactPathCommandsAndEnvironment() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(_root.Combine("custom.json"),
			"""
			{"version":1,"agents":[{"id":"mine","name":"Mine","command":"my-acp","args":["serve"],"env":{"MODE":"acp"}}]}
			""");
		var service = Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3")));

		var launch = Assert.Single(service.LaunchSpecs);
		Assert.Equal("my-acp", launch.Command);
		Assert.Equal(["serve"], launch.Arguments);
		Assert.Equal("acp", launch.Environment["MODE"]);
		Assert.Equal("custom", launch.Distribution);
	}

	[Fact]
	public void CustomProfileReloadIsTransactional() {
		var fileSystem = new InMemoryFileSystem();
		string custom = _root.Combine("custom.json");
		fileSystem.WriteAllText(custom,
			"""{"version":1,"agents":[{"id":"mine","name":"Mine","command":"mine","args":[],"env":{}}]}""");
		var service = Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3")));
		Assert.Equal("mine", Assert.Single(service.LaunchSpecs).Id);
		fileSystem.WriteAllText(custom, """{"version":1,"agents":[]}""");

		Assert.Equal("mine", Assert.Single(service.LaunchSpecs).Id);
		Assert.Throws<InvalidOperationException>(() =>
			service.Reload(_ => throw new InvalidOperationException("referenced")));
		Assert.Equal("mine", Assert.Single(service.LaunchSpecs).Id);

		service.Reload(static _ => { });
		Assert.Empty(service.LaunchSpecs);
	}

	[Fact]
	public async Task BinaryDistributionIsVerifiedAndExtractedInsideItsPackage() {
		byte[] archive = Zip(("bin/sample" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty), "agent"));
		string command = "./bin/sample" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
		var handler = new RegistryHandler(BinaryRegistry(command, Convert.ToHexStringLower(SHA256.HashData(archive)))) {
			Archive = archive,
		};
		var service = Service(new InMemoryFileSystem(), handler);

		await service.InstallAsync("sample", "binary", Accept, CancellationToken.None);

		var launch = Assert.Single(service.LaunchSpecs);
		Assert.True(Path.IsPathFullyQualified(launch.Command));
		Assert.Equal("agent", File.ReadAllText(launch.Command));
		Assert.StartsWith(_root.Combine("packages", "sample", "1.2.3"), launch.Command);
	}

	[Fact]
	public async Task BinaryHashMismatchDoesNotInstallAnything() {
		var handler = new RegistryHandler(BinaryRegistry("./sample", new string('0', 64))) {
			Archive = Encoding.UTF8.GetBytes("payload"),
		};
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => service.InstallAsync("sample", "binary", Accept, CancellationToken.None));

		Assert.Empty(service.LaunchSpecs);
	}

	[Fact]
	public async Task BinaryArchivePreservesEveryExecutableFile() {
		if (OperatingSystem.IsWindows()) return;
		byte[] archive = TarGzip(
			("bin/sample", "agent", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
			("jbr/bin/java", "runtime", UnixFileMode.UserRead | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute));
		var handler = new RegistryHandler(BinaryRegistry(
			"./bin/sample",
			Convert.ToHexStringLower(SHA256.HashData(archive)),
			"https://registry.test/sample.tar.gz")) { Archive = archive };
		var service = Service(new InMemoryFileSystem(), handler);

		await service.InstallAsync("sample", "binary", Accept, CancellationToken.None);

		string install = Directory.GetParent(Path.GetDirectoryName(Assert.Single(service.LaunchSpecs).Command)!)!.FullName;
		string runtime = Path.Combine(install, "jbr", "bin", "java");
		var mode = File.GetUnixFileMode(runtime);
		Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
		Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
	}

	[Fact]
	public async Task BinaryArchiveRejectsLinks() {
		byte[] archive = TarGzipLink("bin/sample", "../outside");
		var handler = new RegistryHandler(BinaryRegistry(
			"./bin/sample",
			Convert.ToHexStringLower(SHA256.HashData(archive)),
			"https://registry.test/sample.tar.gz")) { Archive = archive };
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => service.InstallAsync("sample", "binary", Accept, CancellationToken.None));

		Assert.Empty(service.LaunchSpecs);
	}

	[Fact]
	public async Task BinaryWithoutAHashIsNotAdvertisedOrDownloaded() {
		var handler = new RegistryHandler(BinaryRegistry("./sample", null)) {
			Archive = Encoding.UTF8.GetBytes("payload"),
		};
		var service = Service(new InMemoryFileSystem(), handler);

		Assert.Empty(Assert.Single(await service.ListRegistryAsync(CancellationToken.None)).Distributions);
		await Assert.ThrowsAsync<InvalidDataException>(
			() => service.InstallAsync("sample", "binary", Accept, CancellationToken.None));

		Assert.Empty(service.LaunchSpecs);
		Assert.Equal(0, handler.ArchiveRequests);
	}

	[Fact]
	public async Task ArchiveTraversalIsRejected() {
		byte[] archive = Zip(("../escaped", "bad"), ("sample", "agent"));
		var handler = new RegistryHandler(BinaryRegistry(
			"./sample",
			Convert.ToHexStringLower(SHA256.HashData(archive)))) { Archive = archive };
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => service.InstallAsync("sample", "binary", Accept, CancellationToken.None));

		Assert.False(File.Exists(_root.Combine("packages", "sample", "1.2.3", "escaped")));
	}

	[Theory]
	[InlineData("../sample", "1.2.3")]
	[InlineData("sample", "1.2.3/../../../outside")]
	public async Task RegistryIdentityCannotEscapeThePackageRoot(string id, string version) {
		var handler = new RegistryHandler(PackageRegistry(version, id));
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<JsonException>(() => service.ListRegistryAsync(CancellationToken.None));

		Assert.False(Directory.Exists(_root.Combine("outside")));
	}

	[Fact]
	public async Task RemovingAnInstallationLeavesCustomProfilesUntouched() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(_root.Combine("custom.json"),
			"""{"version":1,"agents":[{"id":"mine","name":"Mine","command":"mine","args":[],"env":{}}]}""");
		var service = Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3")));
		await service.InstallAsync("sample", "npx", Accept, CancellationToken.None);

		service.Remove("sample");

		Assert.Equal("mine", Assert.Single(service.LaunchSpecs).Id);
	}

	[Fact]
	public async Task AnInstallTheCheckRejectsIsNeverSaved() {
		var fileSystem = new InMemoryFileSystem();
		var service = Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3")));
		int changes = 0;
		service.Changed += () => changes++;
		AcpLaunchSpec? checkedLaunch = null;

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(
			"sample",
			"npx",
			(launch, _) => {
				checkedLaunch = launch;
				throw new InvalidOperationException("the agent never answered");
			},
			CancellationToken.None));

		Assert.Equal("the agent never answered", error.Message);
		Assert.Equal([InstalledScript(), "--stdio"], checkedLaunch?.Arguments);
		Assert.Empty(service.LaunchSpecs);
		Assert.Equal(0, changes);
		Assert.Empty(Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3"))).LaunchSpecs);
	}

	private static Task Accept(AcpLaunchSpec launch, CancellationToken ct) => Task.CompletedTask;

	private AcpDistributionService Service(InMemoryFileSystem fileSystem, RegistryHandler handler) {
		var http = new HttpClient(handler);
		return new AcpDistributionService(
			http,
			new AcpRegistryClient(http, new Uri("https://registry.test/index.json")),
			fileSystem,
			_root.Combine("installations.json"),
			_root.Combine("custom.json"),
			_root.Combine("packages"),
			new FakeNpm());
	}

	private string InstalledScript() =>
		_root.Combine("packages", "sample", "1.2.3", "npx", "node_modules", "sample-acp", "cli.js");

	private static string WritePackage(string install, string manifest, string script) {
		File.WriteAllText(Path.Combine(install, "package.json"), """{"dependencies":{"sample-acp":"1.2.3"}}""");
		string package = Directory.CreateDirectory(Path.Combine(install, "node_modules", "sample-acp")).FullName;
		File.WriteAllText(Path.Combine(package, "package.json"), manifest);
		File.WriteAllText(Path.Combine(package, script), "#!/usr/bin/env node\n");
		return install;
	}

	// A registry-free npm package: a local tarball whose one scoped bin npm must find.
	private static void WritePackageTarball(string path) {
		using var file = File.Create(path);
		using var compressed = new GZipStream(file, CompressionLevel.Optimal);
		using var archive = new TarWriter(compressed);
		foreach (var (name, content) in new[] {
			("package/package.json", """{"name":"@scope/sample-acp","version":"1.2.3","bin":{"sample-acp":"bin/acp.js"}}"""),
			("package/bin/acp.js", "#!/usr/bin/env node\n"),
		}) {
			archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
				DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
				Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
			});
		}
	}

	private sealed class FakeNpm : INpmInstaller {
		public Task InstallAsync(string package, string directory, CancellationToken ct) {
			Assert.Equal("sample-acp@1.2.3", package);
			WritePackage(directory, """{"bin":"cli.js"}""", "cli.js");
			return Task.CompletedTask;
		}
	}

	private static string PackageRegistry(string version) => PackageRegistry(version, "sample");

	private static string PackageRegistry(string version, string id) => JsonSerializer.Serialize(new {
		version = "1.0.0",
		agents = new[] {
			new {
				id,
				name = "Sample",
				version,
				description = "Sample agent",
				distribution = new {
					npx = new {
						package = $"sample-acp@{version}",
						args = new[] { "--stdio" },
						env = new Dictionary<string, string> { ["SAMPLE_ACP"] = "1" },
					},
					uvx = new { package = $"sample-acp=={version}", args = new[] { "--stdio" } },
				},
			},
		},
	});

	private static string BinaryRegistry(string command, string? hash) => JsonSerializer.Serialize(new {
		version = "1.0.0",
		agents = new[] {
			new {
				id = "sample",
				name = "Sample",
				version = "1.2.3",
				description = "Sample agent",
				distribution = new {
					binary = new Dictionary<string, object> {
						[AcpPlatformTarget.Current()] = new {
							archive = "https://registry.test/sample.zip",
							cmd = command,
							sha256 = hash,
						},
					},
				},
			},
		},
	});

	private static string BinaryRegistry(string command, string? hash, string archive) => JsonSerializer.Serialize(new {
		version = "1.0.0",
		agents = new[] {
			new {
				id = "sample",
				name = "Sample",
				version = "1.2.3",
				description = "Sample agent",
				distribution = new {
					binary = new Dictionary<string, object> {
						[AcpPlatformTarget.Current()] = new {
							archive,
							cmd = command,
							sha256 = hash,
						},
					},
				},
			},
		},
	});

	private static byte[] Zip(params (string Path, string Content)[] entries) {
		using var stream = new MemoryStream();
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach (var (path, content) in entries) {
				using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
				writer.Write(content);
			}
		}
		return stream.ToArray();
	}

	private static byte[] TarGzip(params (string Path, string Content, UnixFileMode Mode)[] entries) {
		using var stream = new MemoryStream();
		using (var compressed = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
		using (var archive = new TarWriter(compressed, leaveOpen: true)) {
			foreach (var (path, content, mode) in entries) {
				archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) {
					DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
					Mode = mode,
				});
			}
		}
		return stream.ToArray();
	}

	private static byte[] TarGzipLink(string path, string target) {
		using var stream = new MemoryStream();
		using (var compressed = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
		using (var archive = new TarWriter(compressed, leaveOpen: true)) {
			archive.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, path) { LinkName = target });
		}
		return stream.ToArray();
	}

	public void Dispose() {
		_root.Dispose();
		GC.SuppressFinalize(this);
	}

	private sealed class RegistryHandler(string registry) : HttpMessageHandler {
		public byte[] Archive { get; init; } = [];
		public int ArchiveRequests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			bool archive = request.RequestUri?.AbsolutePath != "/index.json";
			if (archive) ArchiveRequests++;
			HttpContent content = archive
				? new ByteArrayContent(Archive)
				: new StringContent(registry, Encoding.UTF8, "application/json");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}
}
