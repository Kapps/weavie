namespace Weavie.Core.Lsp;

/// <summary>
/// How Weavie can install a launch candidate itself: the toolchain that performs the install and the package
/// it pulls. Installs land under <see cref="WeaviePaths.Tools"/> (resolved as a backstop after <c>PATH</c>),
/// never the user's global toolset; the per-toolchain mechanics live in <see cref="ToolchainInstall"/>.
/// </summary>
/// <param name="Toolchain">The package-manager command that installs (<c>npm</c>/<c>dotnet</c>/<c>go</c>); also the offer's PATH gate.</param>
/// <param name="Package">The package the toolchain installs (e.g. <c>@typescript/native-preview</c>).</param>
public sealed record ServerInstallRecipe(string Toolchain, string Package);

/// <summary>
/// One installable miss: a descriptor that failed to resolve, plus the candidate + recipe Weavie offers to
/// install for it.
/// </summary>
/// <param name="Descriptor">The language whose start failed.</param>
/// <param name="Candidate">The candidate the offer installs (the descriptor's first installable one).</param>
/// <param name="Recipe">The candidate's install recipe.</param>
public sealed record ServerInstallOffer(
	LanguageServerDescriptor Descriptor, ServerLaunchCandidate Candidate, ServerInstallRecipe Recipe);
