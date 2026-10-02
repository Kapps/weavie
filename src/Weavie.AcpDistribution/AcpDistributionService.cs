using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common.Tar;
using SharpCompress.Common.Zip;
using Weavie.Core;
using Weavie.Core.FileSystem;

namespace Weavie.AcpDistribution;

/// <summary>Installs ACP Registry entries and resolves their exact launch recipes.</summary>
public sealed class AcpDistributionService : IAcpAgentCatalog {
	private readonly Lock _gate = new();
	private readonly HttpClient _http;
	private readonly AcpRegistryClient _registry;
	private readonly AcpInstallationStore _installations;
	private readonly AcpCustomAgentStore _custom;
	private readonly INpmInstaller _npm;
	private readonly string _packages;
	private Catalog? Cached { get; set; }

	/// <summary>Creates the app-global official ACP catalog.</summary>
	public static AcpDistributionService CreateDefault() {
		var http = new HttpClient();
		return new AcpDistributionService(
			http,
			new AcpRegistryClient(http),
			new LocalFileSystem(),
			WeaviePaths.AcpInstallationsFile,
			WeaviePaths.AcpCustomAgentsFile,
			WeaviePaths.AcpPackages,
			new NpmInstaller());
	}

	internal AcpDistributionService(
		HttpClient http,
		AcpRegistryClient registry,
		IFileSystem fileSystem,
		string installationsPath,
		string customPath,
		string packagesPath,
		INpmInstaller npm) {
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		ArgumentNullException.ThrowIfNull(fileSystem);
		_installations = new AcpInstallationStore(fileSystem, installationsPath);
		_custom = new AcpCustomAgentStore(fileSystem, customPath);
		_npm = npm ?? throw new ArgumentNullException(nameof(npm));
		_packages = Path.GetFullPath(packagesPath);
	}

	/// <inheritdoc/>
	public event Action? Changed;

	/// <inheritdoc/>
	public IReadOnlyList<AcpLaunchSpec> LaunchSpecs => Current.LaunchSpecs;

	/// <inheritdoc/>
	public IReadOnlyList<AcpBrokenAgent> BrokenAgents => Current.Broken;

	/// <inheritdoc/>
	public IReadOnlySet<string> ProviderIds => Current.Ids;

	private Catalog Current {
		get {
			lock (_gate) return Cached ??= Merge(_installations.Load(), _custom.Load());
		}
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<AcpRegistryAgent>> ListRegistryAsync(CancellationToken ct) {
		var registry = await _registry.FetchAsync(ct).ConfigureAwait(false);
		AcpInstallations installed;
		lock (_gate) installed = _installations.Load();
		var byId = installed.Agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
		var broken = installed.Broken.ToDictionary(entry => entry.Agent.Id, entry => entry.Agent, StringComparer.Ordinal);
		string target = AcpPlatformTarget.Current();
		var listed = registry.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
		return [.. registry.Select(entry => {
			string id = AcpRegistryClient.Require(entry.Id, "agent id");
			byId.TryGetValue(id, out var local);
			broken.TryGetValue(id, out var unlaunchable);
			return new AcpRegistryAgent {
				Id = id,
				Name = AcpRegistryClient.Require(entry.Name, $"agent '{id}' name"),
				Version = AcpRegistryClient.Require(entry.Version, $"agent '{id}' version"),
				Description = entry.Description ?? string.Empty,
				Distributions = DistributionKinds(entry.Distribution!, target),
				InstalledDistribution = local?.Distribution ?? unlaunchable?.Distribution,
				InstalledVersion = local?.Version,
				Broken = unlaunchable?.Reason,
			};
		}), .. broken.Values.Where(agent => !listed.Contains(agent.Id)).Select(agent => new AcpRegistryAgent {
			Id = agent.Id,
			Name = agent.Name,
			Version = string.Empty,
			Description = "No longer offered by the ACP Registry.",
			Distributions = [],
			InstalledDistribution = agent.Distribution,
			Broken = agent.Reason,
		})];
	}

	/// <inheritdoc/>
	public async Task InstallAsync(
		string id,
		string distribution,
		Func<AcpLaunchSpec, CancellationToken, Task> verify,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(distribution);
		ArgumentNullException.ThrowIfNull(verify);
		var registry = await _registry.FetchAsync(ct).ConfigureAwait(false);
		var entry = registry.SingleOrDefault(candidate => candidate.Id == id)
			?? throw new InvalidOperationException($"The ACP Registry has no agent named '{id}'.");
		var launch = distribution switch {
			"npx" => await InstallNpmAsync(entry, ct).ConfigureAwait(false),
			"uvx" => UvxLaunch(entry),
			"binary" => await InstallBinaryAsync(entry, ct).ConfigureAwait(false),
			_ => throw new InvalidOperationException($"Agent '{id}' has no '{distribution}' distribution."),
		};
		await verify(launch, ct).ConfigureAwait(false);
		lock (_gate) {
			var current = _installations.Load().Without(id);
			var installations = current with { Agents = [.. current.Agents, launch] };
			var merged = Merge(installations, _custom.Load());
			_installations.Save(installations);
			Cached = merged;
		}
		Changed?.Invoke();
	}

	/// <inheritdoc/>
	public void Remove(string id) {
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		bool removed;
		lock (_gate) {
			var current = _installations.Load();
			removed = current.Ids.Contains(id);
			if (removed) {
				var remaining = current.Without(id);
				var merged = Merge(remaining, _custom.Load());
				_installations.Save(remaining);
				Cached = merged;
			}
		}
		if (removed) Changed?.Invoke();
	}

	/// <inheritdoc/>
	public void Reload(Action<IReadOnlySet<string>> validate) {
		ArgumentNullException.ThrowIfNull(validate);
		lock (_gate) {
			var merged = Merge(_installations.Load(), _custom.Load());
			validate(merged.Ids);
			Cached = merged;
		}
		Changed?.Invoke();
	}

	private async Task<AcpLaunchSpec> InstallBinaryAsync(AcpRegistryEntry entry, CancellationToken ct) {
		string id = AcpRegistryClient.Require(entry.Id, "agent id");
		string target = AcpPlatformTarget.Current();
		if (entry.Distribution?.Binary?.TryGetValue(target, out var nullable) != true || nullable is null) {
			throw new InvalidOperationException($"Agent '{id}' has no binary for {target}.");
		}
		var binary = nullable;
		var archive = HttpUri(AcpRegistryClient.Require(binary.Archive, $"agent '{id}' binary archive"));
		string command = SafeRelative(AcpRegistryClient.Require(binary.Command, $"agent '{id}' binary command"));
		string hash = RequireHash(binary.Sha256, id);
		Values(binary.Arguments, id);
		EnvironmentValues(binary.Environment, id);
		string executable = await InstallPackageAsync(entry, target, async staging => {
			byte[] payload = await _http.GetByteArrayAsync(archive, ct).ConfigureAwait(false);
			VerifyHash(payload, hash, id);
			Extract(payload, archive.AbsolutePath, staging);
			string extracted = Within(staging, command);
			if (!File.Exists(extracted)) {
				throw new InvalidDataException($"Agent '{id}' archive does not contain '{command}'.");
			}
			if (!OperatingSystem.IsWindows()) {
				File.SetUnixFileMode(extracted, File.GetUnixFileMode(extracted) | UnixFileMode.UserExecute);
			}
			return command;
		}).ConfigureAwait(false);
		return Launch(entry, "binary", executable, binary.Arguments, binary.Environment);
	}

	private async Task<AcpLaunchSpec> InstallNpmAsync(AcpRegistryEntry entry, CancellationToken ct) {
		string id = AcpRegistryClient.Require(entry.Id, "agent id");
		var package = entry.Distribution!.Npx ?? throw new InvalidOperationException($"Agent '{id}' has no 'npx' distribution.");
		string spec = AcpRegistryClient.Require(package.Package, $"agent '{id}' npx package");
		string executable = await InstallPackageAsync(entry, "npx", async staging => {
			await _npm.InstallAsync(spec, staging, ct).ConfigureAwait(false);
			return Path.GetRelativePath(staging, AcpNpmPackage.Executable(staging));
		}).ConfigureAwait(false);
		var (command, arguments) = AcpNpmPackage.Launch(executable);
		return Launch(entry, "npx", command, [.. arguments, .. Values(package.Arguments, id)], package.Environment);
	}

	// Populates a fresh staging directory, swaps it into the package's install directory, and returns the absolute
	// path of the file it named.
	private async Task<string> InstallPackageAsync(AcpRegistryEntry entry, string kind, Func<string, Task<string>> populate) {
		string id = AcpRegistryClient.RequireSegment(entry.Id, "agent id", allowPlus: false);
		string version = AcpRegistryClient.RequireSegment(entry.Version, $"agent '{id}' version", allowPlus: true);
		string destination = Within(_packages, Path.Combine(id, version, kind));
		string parent = Path.GetDirectoryName(destination)!;
		Directory.CreateDirectory(parent);
		string staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.staging");
		Directory.CreateDirectory(staging);
		try {
			string file = await populate(staging).ConfigureAwait(false);
			ReplaceDirectory(staging, destination);
			return Within(destination, file);
		} finally {
			if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
		}
	}

	private static AcpLaunchSpec UvxLaunch(AcpRegistryEntry entry) {
		string id = AcpRegistryClient.Require(entry.Id, "agent id");
		var package = entry.Distribution!.Uvx ?? throw new InvalidOperationException($"Agent '{id}' has no 'uvx' distribution.");
		string spec = AcpRegistryClient.Require(package.Package, $"agent '{id}' uvx package");
		return Launch(entry, "uvx", "uvx", [spec, .. Values(package.Arguments, id)], package.Environment);
	}

	private static AcpLaunchSpec Launch(
		AcpRegistryEntry entry,
		string distribution,
		string command,
		IReadOnlyList<string?>? arguments,
		IReadOnlyDictionary<string, string?>? environment) {
		string id = AcpRegistryClient.Require(entry.Id, "agent id");
		return new AcpLaunchSpec {
			Id = id,
			Name = AcpRegistryClient.Require(entry.Name, $"agent '{id}' name"),
			Version = AcpRegistryClient.Require(entry.Version, $"agent '{id}' version"),
			Command = command,
			Arguments = Values(arguments, id),
			Environment = EnvironmentValues(environment, id),
			Distribution = distribution,
		};
	}

	private static IReadOnlyList<string> DistributionKinds(AcpRegistryDistribution distribution, string target) {
		var kinds = new List<string>(3);
		if (distribution.Binary?.TryGetValue(target, out var binary) == true && Usable(binary)) kinds.Add("binary");
		if (distribution.Npx is not null) kinds.Add("npx");
		if (distribution.Uvx is not null) kinds.Add("uvx");
		return kinds;
	}

	private static Catalog Merge(AcpInstallations installed, IReadOnlyList<AcpLaunchSpec> custom) {
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (string id in installed.Ids.Concat(custom.Select(agent => agent.Id))) {
			if (!ids.Add(id)) throw new InvalidDataException($"ACP agent '{id}' is configured twice.");
		}
		return new Catalog([.. installed.Agents, .. custom], [.. installed.Broken.Select(entry => entry.Agent)], ids);
	}

	private sealed record Catalog(
		IReadOnlyList<AcpLaunchSpec> LaunchSpecs,
		IReadOnlyList<AcpBrokenAgent> Broken,
		IReadOnlySet<string> Ids);

	private static IReadOnlyList<string> Values(IReadOnlyList<string?>? values, string id) {
		if (values is null) return [];
		if (values.Any(value => value is null)) throw new InvalidDataException($"Agent '{id}' has null arguments.");
		return [.. values!];
	}

	private static IReadOnlyDictionary<string, string> EnvironmentValues(
		IReadOnlyDictionary<string, string?>? values,
		string id) {
		if (values is null) return new Dictionary<string, string>(StringComparer.Ordinal);
		if (values.Any(entry => entry.Value is null)) {
			throw new InvalidDataException($"Agent '{id}' has a null environment value.");
		}
		return values.ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);
	}

	private static Uri HttpUri(string value) {
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
			|| uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) {
			throw new InvalidDataException($"ACP binary archive '{value}' is not an HTTP(S) URL.");
		}
		return uri;
	}

	private static string SafeRelative(string value) {
		string normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
		while (normalized.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) normalized = normalized[2..];
		if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathFullyQualified(normalized)) {
			throw new InvalidDataException($"ACP binary command '{value}' is not a relative path.");
		}
		return normalized;
	}

	private static string Within(string root, string relative) {
		string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
		if (candidate != fullRoot && !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
			throw new InvalidDataException($"ACP archive path '{relative}' escapes its package directory.");
		}
		return candidate;
	}

	private static string RequireHash(string? expected, string id) => ValidHash(expected)
		? expected!
		: throw new InvalidDataException($"Agent '{id}' has an invalid SHA-256 digest.");

	private static bool ValidHash(string? value) =>
		value is { Length: 64 } && value.All(Uri.IsHexDigit);

	private static bool Usable(AcpBinaryDistribution? binary) {
		if (binary is null || !ValidHash(binary.Sha256)
			|| binary.Arguments?.Any(value => value is null) == true
			|| binary.Environment?.Any(entry => entry.Value is null) == true) {
			return false;
		}
		try {
			HttpUri(AcpRegistryClient.Require(binary.Archive, "binary archive"));
			SafeRelative(AcpRegistryClient.Require(binary.Command, "binary command"));
			return true;
		} catch (JsonException) {
			return false;
		} catch (InvalidDataException) {
			return false;
		}
	}

	private static void VerifyHash(byte[] payload, string expected, string id) {
		string actual = Convert.ToHexStringLower(SHA256.HashData(payload));
		if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
			throw new InvalidDataException($"Agent '{id}' binary failed SHA-256 verification.");
		}
	}

	private static void ReplaceDirectory(string staging, string destination) {
		if (!Directory.Exists(destination)) {
			Directory.Move(staging, destination);
			return;
		}
		string backup = destination + $".{Guid.NewGuid():N}.backup";
		Directory.Move(destination, backup);
		try {
			Directory.Move(staging, destination);
			Directory.Delete(backup, recursive: true);
		} catch {
			if (!Directory.Exists(destination) && Directory.Exists(backup)) {
				Directory.Move(backup, destination);
			}
			throw;
		}
	}

	private static void Extract(byte[] payload, string archivePath, string destination) {
		using var input = new MemoryStream(payload, writable: false);
		if (!IsArchive(archivePath)) {
			string leaf = Path.GetFileName(Uri.UnescapeDataString(archivePath));
			if (string.IsNullOrWhiteSpace(leaf)) throw new InvalidDataException("The ACP binary URL has no file name.");
			File.WriteAllBytes(Within(destination, SafeRelative(leaf)), payload);
			return;
		}
		if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
			|| archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) {
			using var decompressed = new GZipStream(input, CompressionMode.Decompress);
			using var tarReader = new TarReader(decompressed);
			while (tarReader.GetNextEntry() is { } entry) {
				if (entry.EntryType is TarEntryType.Directory) continue;
				if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile) {
					throw new InvalidDataException($"ACP archive entry '{entry.Name}' is not a regular file.");
				}
				WriteArchiveFile(
					entry.DataStream ?? throw new InvalidDataException($"ACP archive entry '{entry.Name}' has no content."),
					entry.Name,
					destination,
					entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
			}
			return;
		}
		using var archive = ArchiveFactory.OpenArchive(input);
		foreach (var entry in archive.Entries.Where(candidate => !candidate.IsDirectory)) {
			int unixMode = UnixMode(entry);
			if (entry.LinkTarget is not null || UnixFileType(unixMode) is not 0 and not 0x8000) {
				throw new InvalidDataException($"ACP archive entry '{entry.Key}' is not a regular file.");
			}
			using var source = entry.OpenEntryStream();
			WriteArchiveFile(
				source,
				entry.Key ?? throw new InvalidDataException("ACP archive entry has no path."),
				destination,
				(UnixFileMode)(unixMode & 0x49));
		}
	}

	private static void WriteArchiveFile(Stream source, string path, string destination, UnixFileMode execute) {
		string output = Within(destination, SafeRelative(path));
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		using (var target = File.Create(output)) source.CopyTo(target);
		if (!OperatingSystem.IsWindows() && execute != 0) {
			File.SetUnixFileMode(output, File.GetUnixFileMode(output) | execute);
		}
	}

	private static int UnixMode(SharpCompress.Archives.IArchiveEntry entry) => entry switch {
		SharpCompress.Common.Tar.TarEntry tar => checked((int)tar.Mode),
		ZipEntry zip => (int)((uint)(zip.Attrib ?? 0) >> 16),
		_ => 0,
	};

	private static int UnixFileType(int mode) => mode & 0xF000;

	private static bool IsArchive(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase);
}
