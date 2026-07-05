using System.Text.Json;
using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

/// <summary>
/// The built-in <see cref="WorkspacePreset"/> catalog — the curated, hardcoded knowledge that lets Weavie
/// configure a workspace's setup command and test profile without a model call. TS/C#/Go today; Rust/Python
/// next. Mirrors <c>LanguageServerCatalog</c>. See <c>docs/concepts/workspace-autoconfig.md</c>.
/// </summary>
public static class WorkspacePresetCatalog {
	/// <summary>TypeScript / JavaScript — detected by <c>package.json</c>; package manager and test runner read from the manifest.</summary>
	public static WorkspacePreset TypeScript { get; } = new() {
		Id = "typescript",
		DisplayName = "TypeScript / JavaScript",
		Markers = ["package.json"],
		Detect = DetectTypeScript,
	};

	/// <summary>C# / .NET — detected by a solution or project file; test framework read from <c>.csproj</c> package references.</summary>
	public static WorkspacePreset CSharp { get; } = new() {
		Id = "csharp",
		DisplayName = "C#",
		Markers = ["*.slnx", "*.sln", "*.csproj"],
		Detect = DetectCSharp,
	};

	/// <summary>Go — detected by <c>go.mod</c> / <c>go.work</c>; test convention is fixed (<c>TestXxx</c> functions).</summary>
	public static WorkspacePreset Go { get; } = new() {
		Id = "go",
		DisplayName = "Go",
		Markers = ["go.mod", "go.work"],
		Detect = DetectGo,
	};

	/// <summary>
	/// All built-in presets, in catalog order — the order their contributions are unioned. Root-solution
	/// languages first (a repo's .NET restore before a sub-package's install reads most naturally); test-rule
	/// globs are disjoint by extension, so order never affects which rule a file matches.
	/// </summary>
	public static IReadOnlyList<WorkspacePreset> All { get; } = [CSharp, TypeScript, Go];

	private static PresetResult DetectTypeScript(DetectionContext ctx) {
		string pm = PackageManager(ctx);
		string? runner = null;
		string manifest = Path.Combine(ctx.MarkerDirectory, "package.json");
		if (ctx.FileSystem.FileExists(manifest)) {
			try {
				using var doc = JsonDocument.Parse(ctx.FileSystem.ReadAllText(manifest));
				var root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Object) {
					if (root.TryGetProperty("packageManager", out var pinned) && pinned.ValueKind == JsonValueKind.String) {
						string name = pinned.GetString()!.Split('@', 2)[0].Trim();
						if (name is "pnpm" or "yarn" or "bun" or "npm") {
							pm = name;
						}
					}

					runner = DetectRunner(root);
				}
			} catch (Exception ex) when (ex is JsonException or IOException) {
				// A malformed or unreadable package.json can't tell us the runner; leave tests to the setup card
				// while still restoring dependencies. Never guess a runner from a manifest we couldn't read.
			}
		}

		string setup = pm + " install";
		if (runner is null) {
			return new PresetResult { SetupCommand = setup, TestRules = [] };
		}

		string bin = pm == "npm" ? "npx" : pm; // pnpm/yarn/bun run local binaries directly; npm uses npx
		var (runFile, runOne) = runner switch {
			"vitest" => (bin + " vitest run ${file}", bin + " vitest run ${file} -t ${name}"),
			"jest" => (bin + " jest ${file}", bin + " jest ${file} -t ${name}"),
			_ => (bin + " mocha ${file}", bin + " mocha ${file} -g ${name}"),
		};

		return new PresetResult {
			SetupCommand = setup,
			TestRules = [new TestRule {
				Glob = "**/*.{test,spec}.{ts,tsx,js,jsx,mts,cts}",
				Symbol = "^(?:describe|it|test)\\((?:'|\")(.+?)(?:'|\")",
				RunOne = runOne,
				RunFile = runFile,
				NameSeparator = " > ",
			}],
		};
	}

	private static PresetResult DetectCSharp(DetectionContext ctx) => new() {
		SetupCommand = "dotnet restore",
		TestRules = [new TestRule {
			Glob = "**/*.cs",
			Symbol = "^(\\w+)",
			// Contains-match on the method name (runOne) and, by convention, the file's class (runFile via
			// ${fileName}). dotnet's --filter reaches class + method scope; symbol-exact class scoping is a follow-up.
			RunOne = "dotnet test --filter FullyQualifiedName~${name}",
			RunFile = "dotnet test --filter FullyQualifiedName~${fileName}",
			Header = CSharpHeader(ctx),
		}],
	};

	private static PresetResult DetectGo(DetectionContext ctx) => new() {
		SetupCommand = "go mod download",
		TestRules = [new TestRule {
			Glob = "**/*_test.go",
			Symbol = "^(Test\\w+)",
			RunOne = "go test ${fileDir} -run '^${name}$'",
			RunFile = "go test ${fileDir}",
		}],
	};

	// Package manager from lockfiles in the manifest directory (most-specific wins); npm is the baseline that
	// ships with Node. A package.json packageManager field, when present, overrides this in DetectTypeScript.
	private static string PackageManager(DetectionContext ctx) {
		if (ctx.MarkerFiles.Contains("pnpm-lock.yaml", StringComparer.OrdinalIgnoreCase)) {
			return "pnpm";
		}

		if (ctx.MarkerFiles.Contains("yarn.lock", StringComparer.OrdinalIgnoreCase)) {
			return "yarn";
		}

		if (ctx.MarkerFiles.Any(f => f is "bun.lockb" or "bun.lock")) {
			return "bun";
		}

		return "npm";
	}

	private static string? DetectRunner(JsonElement packageJson) {
		var deps = new HashSet<string>(StringComparer.Ordinal);
		foreach (string section in (string[])["dependencies", "devDependencies"]) {
			if (packageJson.TryGetProperty(section, out var element) && element.ValueKind == JsonValueKind.Object) {
				foreach (var property in element.EnumerateObject()) {
					deps.Add(property.Name);
				}
			}
		}

		if (deps.Contains("vitest")) {
			return "vitest";
		}

		if (deps.Contains("jest")) {
			return "jest";
		}

		return deps.Contains("mocha") ? "mocha" : null;
	}

	// The attribute-slice regex that selects test methods, from the test framework a .csproj references
	// (xUnit's [Fact]/[Theory], NUnit/MSTest's [Test]/[TestMethod]); xUnit is the default when none is found.
	// Scans every .csproj in the walk, not just the marker directory: the C# marker is usually a solution file at
	// the repo root, while the test project (and its framework reference) lives in a subdirectory.
	private static string CSharpHeader(DetectionContext ctx) {
		foreach (string path in ctx.AllFiles) {
			if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			string text;
			try {
				text = ctx.FileSystem.ReadAllText(path);
			} catch (IOException) {
				continue;
			}

			if (text.Contains("xunit", StringComparison.OrdinalIgnoreCase)) {
				return "\\[(Fact|Theory)\\b";
			}

			if (text.Contains("nunit", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("MSTest", StringComparison.OrdinalIgnoreCase)) {
				return "\\[(Test|TestMethod)\\b";
			}
		}

		return "\\[(Fact|Theory)\\b";
	}
}
