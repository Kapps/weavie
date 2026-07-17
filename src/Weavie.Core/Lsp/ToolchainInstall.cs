namespace Weavie.Core.Lsp;

/// <summary>
/// The per-toolchain mechanics behind a <see cref="ServerInstallRecipe"/>: the arguments that target the
/// install at Weavie's own tools folder, the environment it needs, and the bin directory the server lands in.
/// Curated knowledge no external source holds, keyed by the recipe's toolchain.
/// </summary>
public static class ToolchainInstall {
	/// <summary>A toolchain's install target: <c>~/.weavie/tools/&lt;toolchain&gt;</c>.</summary>
	public static string InstallDir(string toolchain) => Path.Combine(WeaviePaths.Tools, toolchain);

	/// <summary>
	/// The directory the installed executables land in. npm links bins under <c>&lt;prefix&gt;/bin</c> on POSIX but
	/// the prefix itself on Windows; dotnet's <c>--tool-path</c> and go's <c>GOBIN</c> are the bin dir directly.
	/// </summary>
	public static string BinDir(string toolchain) =>
		toolchain == "npm" && !OperatingSystem.IsWindows()
			? Path.Combine(InstallDir(toolchain), "bin")
			: InstallDir(toolchain);

	/// <summary>The toolchain's argument list installing <paramref name="recipe"/>'s package into <see cref="InstallDir"/>.</summary>
	public static IReadOnlyList<string> Arguments(ServerInstallRecipe recipe) {
		ArgumentNullException.ThrowIfNull(recipe);
		return recipe.Toolchain switch {
			"npm" => ["install", "--global", "--prefix", InstallDir("npm"), recipe.Package],
			"dotnet" => ["tool", "install", "--tool-path", InstallDir("dotnet"), recipe.Package],
			"go" => ["install", recipe.Package],
			_ => throw new NotSupportedException($"No install mechanics for toolchain '{recipe.Toolchain}'."),
		};
	}

	/// <summary>Extra environment the install needs: go targets its bin dir via <c>GOBIN</c>; npm/dotnet via flags.</summary>
	public static IReadOnlyDictionary<string, string> Environment(ServerInstallRecipe recipe) {
		ArgumentNullException.ThrowIfNull(recipe);
		var environment = new Dictionary<string, string>();
		if (recipe.Toolchain == "go") {
			environment["GOBIN"] = InstallDir("go");
		}

		return environment;
	}
}
