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
	private readonly string _root = Path.Combine(Path.GetTempPath(), "weavie-acp-distribution", Guid.NewGuid().ToString("N"));

	public AcpDistributionServiceTests() {
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task PackageDistributionsArePersistedAsLiteralCommands() {
		var fileSystem = new InMemoryFileSystem();
		var handler = new RegistryHandler(PackageRegistry("1.2.3"));
		var service = Service(fileSystem, handler);
		var listed = Assert.Single(await service.ListRegistryAsync(CancellationToken.None));
		Assert.Equal(["npx", "uvx"], listed.Distributions);

		int changes = 0;
		service.Changed += () => changes++;
		await service.InstallAsync("sample", "npx", CancellationToken.None);

		var launch = Assert.Single(service.LaunchSpecs);
		Assert.Equal("npx", launch.Command);
		Assert.Equal(["--yes", "sample-acp@1.2.3", "--stdio"], launch.Arguments);
		Assert.Equal("1", launch.Environment["SAMPLE_ACP"]);
		Assert.Equal("npx", launch.Distribution);
		Assert.Equal(1, changes);

		var reloaded = Service(fileSystem, handler);
		var persisted = Assert.Single(reloaded.LaunchSpecs);
		Assert.Equal(launch.Id, persisted.Id);
		Assert.Equal(launch.Command, persisted.Command);
		Assert.Equal(launch.Arguments, persisted.Arguments);
		Assert.Equal(launch.Environment, persisted.Environment);
		await reloaded.InstallAsync("sample", "uvx", CancellationToken.None);
		var uvx = Assert.Single(reloaded.LaunchSpecs);
		Assert.Equal("uvx", uvx.Command);
		Assert.Equal(["sample-acp==1.2.3", "--stdio"], uvx.Arguments);
	}

	[Fact]
	public void WindowsNpxUsesTheStandardShimWithoutAUserControlledShellExpression() {
		var (Command, Arguments) = AcpDistributionService.PackageProcess(
			"npx",
			["--yes", "@scope/sample@1.2.3", "--stdio"],
			windows: true);

		Assert.Equal("npx", Command);
		Assert.Equal(["--yes", "@scope/sample@1.2.3", "--stdio"], Arguments);
		Assert.Throws<InvalidDataException>(() => AcpDistributionService.PackageProcess(
			"npx",
			["sample&calc"],
			windows: true));
	}

	[Fact]
	public void PosixInstallationsPersistLiteralNpxArgumentsWithWhitespace() {
		if (OperatingSystem.IsWindows()) return;
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpInstallationStore(fileSystem, Path.Combine(_root, "installed.json"));
		var launch = new AcpLaunchSpec {
			Id = "sample",
			Name = "Sample",
			Command = "npx",
			Arguments = ["--yes", "sample-acp@1.2.3", "--profile name"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Version = "1.2.3",
			Distribution = "npx",
		};

		store.Save([launch]);

		Assert.Equal(launch.Arguments, Assert.Single(store.Load()).Arguments);
	}

	[Fact]
	public void CustomProfilesUseExactPathCommandsAndEnvironment() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(Path.Combine(_root, "custom.json"),
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
		string custom = Path.Combine(_root, "custom.json");
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

		await service.InstallAsync("sample", "binary", CancellationToken.None);

		var launch = Assert.Single(service.LaunchSpecs);
		Assert.True(Path.IsPathFullyQualified(launch.Command));
		Assert.Equal("agent", File.ReadAllText(launch.Command));
		Assert.StartsWith(Path.Combine(_root, "packages", "sample", "1.2.3"), launch.Command);
	}

	[Fact]
	public async Task BinaryHashMismatchDoesNotInstallAnything() {
		var handler = new RegistryHandler(BinaryRegistry("./sample", new string('0', 64))) {
			Archive = Encoding.UTF8.GetBytes("payload"),
		};
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => service.InstallAsync("sample", "binary", CancellationToken.None));

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

		await service.InstallAsync("sample", "binary", CancellationToken.None);

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
			() => service.InstallAsync("sample", "binary", CancellationToken.None));

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
			() => service.InstallAsync("sample", "binary", CancellationToken.None));

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
			() => service.InstallAsync("sample", "binary", CancellationToken.None));

		Assert.False(File.Exists(Path.Combine(_root, "packages", "sample", "1.2.3", "escaped")));
	}

	[Theory]
	[InlineData("../sample", "1.2.3")]
	[InlineData("sample", "1.2.3/../../../outside")]
	public async Task RegistryIdentityCannotEscapeThePackageRoot(string id, string version) {
		var handler = new RegistryHandler(PackageRegistry(version, id));
		var service = Service(new InMemoryFileSystem(), handler);

		await Assert.ThrowsAsync<JsonException>(() => service.ListRegistryAsync(CancellationToken.None));

		Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
	}

	[Fact]
	public async Task RemovingAnInstallationLeavesCustomProfilesUntouched() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(Path.Combine(_root, "custom.json"),
			"""{"version":1,"agents":[{"id":"mine","name":"Mine","command":"mine","args":[],"env":{}}]}""");
		var service = Service(fileSystem, new RegistryHandler(PackageRegistry("1.2.3")));
		await service.InstallAsync("sample", "npx", CancellationToken.None);

		service.Remove("sample");

		Assert.Equal("mine", Assert.Single(service.LaunchSpecs).Id);
	}

	private AcpDistributionService Service(InMemoryFileSystem fileSystem, RegistryHandler handler) {
		var http = new HttpClient(handler);
		return new AcpDistributionService(
			http,
			new AcpRegistryClient(http, new Uri("https://registry.test/index.json")),
			fileSystem,
			Path.Combine(_root, "installations.json"),
			Path.Combine(_root, "custom.json"),
			Path.Combine(_root, "packages"));
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
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
