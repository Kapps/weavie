using Weavie.Core;

namespace Weavie.Hosting;

/// <summary>The launch args and environment additions that make an interactive shell report its cwd via OSC 7.</summary>
public sealed record ShellLaunchOverride {
	/// <summary>Arguments the shell is launched with (replacing the caller's default interactive args).</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Environment additions/overrides for the launch.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }
}

/// <summary>
/// Injects OSC 7 cwd reporting into a bash/zsh interactive shell so Weavie always knows the shell's directory
/// (used to resolve a clicked relative path — e.g. a filename printed by <c>ls</c> — against the live cwd). The
/// integration sources the user's own startup files unchanged and only <em>appends</em> a per-prompt emitter, so
/// aliases, prompt, and PATH are untouched. Materializes small rc files once under
/// <c>~/.weavie/internals/shell-integration</c> and returns the launch that points the shell at them.
/// </summary>
public sealed class ShellIntegration {
	private readonly string _scriptRoot;
	private readonly string _userZdotdir;

	/// <summary>Creates an integration that writes its rc files under <paramref name="scriptRoot"/> and preserves <paramref name="userZdotdir"/> as the user's real zsh config dir.</summary>
	public ShellIntegration(string scriptRoot, string userZdotdir) {
		ArgumentException.ThrowIfNullOrEmpty(scriptRoot);
		ArgumentException.ThrowIfNullOrEmpty(userZdotdir);
		_scriptRoot = scriptRoot;
		_userZdotdir = userZdotdir;
	}

	/// <summary>The default integration: rc files under the weavie internals dir, preserving the ambient <c>ZDOTDIR</c> (or <c>$HOME</c>).</summary>
	public static ShellIntegration Default() {
		string? zdotdir = Environment.GetEnvironmentVariable("ZDOTDIR");
		string userZdotdir = string.IsNullOrEmpty(zdotdir)
			? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
			: zdotdir;
		return new ShellIntegration(WeaviePaths.Internal("shell-integration"), userZdotdir);
	}

	/// <summary>
	/// The launch for an interactive <paramref name="shellName"/> that reports OSC 7 cwd, or <see langword="null"/>
	/// for a shell the integration doesn't cover (falling back to the caller's default args). Writes the rc files
	/// as a side effect.
	/// </summary>
	public ShellLaunchOverride? Resolve(string shellName) {
		switch (shellName) {
			case "bash":
				string bashRc = Path.Combine(_scriptRoot, "weavie.bash");
				Write(bashRc, BashRc);
				return new ShellLaunchOverride {
					Arguments = ["--rcfile", bashRc, "-i"],
					Environment = new Dictionary<string, string>(StringComparer.Ordinal),
				};
			case "zsh":
				string zshDir = Path.Combine(_scriptRoot, "zsh");
				Directory.CreateDirectory(zshDir);
				Write(Path.Combine(zshDir, ".zshenv"), ZshEnv);
				Write(Path.Combine(zshDir, ".zprofile"), ZshProfile);
				Write(Path.Combine(zshDir, ".zshrc"), ZshRc);
				return new ShellLaunchOverride {
					Arguments = ["-l", "-i"],
					Environment = new Dictionary<string, string>(StringComparer.Ordinal) {
						["ZDOTDIR"] = zshDir,
						["WEAVIE_ZDOTDIR_USER"] = _userZdotdir,
					},
				};
			default:
				return null;
		}
	}

	private static void Write(string path, string content) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		// Overwrite every launch: the content is static, so this is cheap and self-heals a truncated file.
		File.WriteAllText(path, content);
	}

	// bash reads a single --rcfile as interactive-non-login, so reproduce the login startup (profile chain) the
	// plain `bash -l -i` had, then append an OSC 7 emitter to PROMPT_COMMAND without clobbering the user's.
	private const string BashRc = """
		if [ -f /etc/profile ]; then . /etc/profile; fi
		for __weavie_p in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
		  if [ -f "$__weavie_p" ]; then . "$__weavie_p"; break; fi
		done
		unset __weavie_p
		__weavie_osc7() { printf '\033]7;file://%s%s\007' "${HOSTNAME:-}" "$PWD"; }
		case ";${PROMPT_COMMAND:-};" in
		  *__weavie_osc7*) ;;
		  *) PROMPT_COMMAND="__weavie_osc7${PROMPT_COMMAND:+;$PROMPT_COMMAND}" ;;
		esac

		""";

	// zsh reads its startup files from $ZDOTDIR, so we point it at ours and each stub sources the user's matching
	// file by explicit path. We keep ZDOTDIR on our dir through .zshenv/.zprofile (re-asserting in case the user's
	// file changed it) so our .zshrc is reached, then restore it there so the user's later .zlogin loads normally.
	private const string ZshEnv = """
		__weavie_zdotdir="$ZDOTDIR"
		[ -f "$WEAVIE_ZDOTDIR_USER/.zshenv" ] && source "$WEAVIE_ZDOTDIR_USER/.zshenv"
		ZDOTDIR="$__weavie_zdotdir"; unset __weavie_zdotdir

		""";

	private const string ZshProfile = """
		__weavie_zdotdir="$ZDOTDIR"
		[ -f "$WEAVIE_ZDOTDIR_USER/.zprofile" ] && source "$WEAVIE_ZDOTDIR_USER/.zprofile"
		ZDOTDIR="$__weavie_zdotdir"; unset __weavie_zdotdir

		""";

	private const string ZshRc = """
		[ -f "$WEAVIE_ZDOTDIR_USER/.zshrc" ] && source "$WEAVIE_ZDOTDIR_USER/.zshrc"
		ZDOTDIR="$WEAVIE_ZDOTDIR_USER"
		__weavie_osc7() { printf '\033]7;file://%s%s\007' "${HOST:-}" "$PWD"; }
		autoload -Uz add-zsh-hook 2>/dev/null && add-zsh-hook precmd __weavie_osc7

		""";
}
