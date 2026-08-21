using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>
/// Editor-behavior settings — Monaco <c>IEditorOptions</c> as first-class, typed Weavie settings; the editor
/// analogue of <see cref="FontSettings"/>. All are <see cref="ApplyMode.Live"/>: a change re-pushes the
/// resolved options to the web, which applies them with <c>editor.updateOptions</c>.
/// <para>
/// <see cref="SuggestExpandDocs"/>, <see cref="CommentProse"/>, <see cref="PaneShortcutHints"/>, and
/// <see cref="VideoAutoplay"/> have no <c>updateOptions</c> field; the web maps them to small custom
/// behaviors. Everything else is a straight passthrough.
/// </para>
/// </summary>
public static class EditorSettings {
	/// <summary>Inline type/parameter-name hints (the greyed <c>: Type</c> / <c>name:</c> annotations).</summary>
	public const string InlayHints = "editor.inlayHints";

	/// <summary>The minimap (code overview) on the editor's right edge.</summary>
	public const string Minimap = "editor.minimap";

	/// <summary>Colorize matching bracket pairs by nesting depth.</summary>
	public const string BracketPairColorization = "editor.bracketPairColorization";

	/// <summary>Animate editor scrolling.</summary>
	public const string SmoothScrolling = "editor.smoothScrolling";

	/// <summary>Animate caret movement.</summary>
	public const string CursorSmoothCaretAnimation = "editor.cursorSmoothCaretAnimation";

	/// <summary>Render whitespace characters (spaces/tabs).</summary>
	public const string RenderWhitespace = "editor.renderWhitespace";

	/// <summary>Allow scrolling past the last line.</summary>
	public const string ScrollBeyondLastLine = "editor.scrollBeyondLastLine";

	/// <summary>Multiplier on the distance one mouse-wheel notch scrolls.</summary>
	public const string MouseWheelScrollSensitivity = "editor.mouseWheelScrollSensitivity";

	/// <summary>Multiplier on the scroll distance while Alt is held.</summary>
	public const string FastScrollSensitivity = "editor.fastScrollSensitivity";

	/// <summary>Middle-click then move the mouse to scroll continuously, in the editor and the agent transcript.</summary>
	public const string MiddleClickAutoscroll = "editor.middleClickAutoscroll";

	/// <summary>Wrap long lines.</summary>
	public const string WordWrap = "editor.wordWrap";

	/// <summary>Line-number gutter style.</summary>
	public const string LineNumbers = "editor.lineNumbers";

	/// <summary>Cursor blinking style.</summary>
	public const string CursorBlinking = "editor.cursorBlinking";

	/// <summary>Current-line highlight style.</summary>
	public const string RenderLineHighlight = "editor.renderLineHighlight";

	/// <summary>Pin enclosing scopes to the top while scrolling.</summary>
	public const string StickyScroll = "editor.stickyScroll";

	/// <summary>Enable font ligatures.</summary>
	public const string FontLigatures = "editor.fontLigatures";

	/// <summary>Show indentation guide lines.</summary>
	public const string IndentGuides = "editor.indentGuides";

	/// <summary>Delay (ms) before the hover tooltip appears; 0 = instant.</summary>
	public const string HoverDelay = "editor.hoverDelay";

	/// <summary>Auto-expand the documentation flyout beside the autocomplete list (custom behavior).</summary>
	public const string SuggestExpandDocs = "editor.suggest.expandDocs";

	/// <summary>Which comments render as styled prose — none/documentation/multiline/all (custom behavior).</summary>
	public const string CommentProse = "editor.commentProse";

	/// <summary>Show the Ctrl+N pane-switch shortcut hint badges on the panes (custom behavior).</summary>
	public const string PaneShortcutHints = "editor.paneShortcutHints";

	/// <summary>Start playback when a video file opens in the media pane (custom behavior).</summary>
	public const string VideoAutoplay = "editor.videoAutoplay";

	/// <summary>Which lines carry the faded Git blame annotation — none/the cursor's/every line (custom behavior).</summary>
	public const string GitBlame = "editor.gitBlame";

	/// <summary>Every editor-option key — the host subscribes to all of them to re-push on any change.</summary>
	public static readonly IReadOnlyList<string> Keys = [
		InlayHints, Minimap, BracketPairColorization, SmoothScrolling, CursorSmoothCaretAnimation,
		RenderWhitespace, ScrollBeyondLastLine, MouseWheelScrollSensitivity, FastScrollSensitivity,
		MiddleClickAutoscroll, WordWrap, LineNumbers, CursorBlinking, RenderLineHighlight,
		StickyScroll, FontLigatures, IndentGuides, HoverDelay, SuggestExpandDocs, CommentProse, PaneShortcutHints,
		VideoAutoplay, GitBlame,
	];

	/// <summary>The <see cref="GitBlame"/> value meaning "no annotation" — what the toggle command turns off to.</summary>
	public const string GitBlameOff = "off";

	/// <summary>
	/// The <see cref="GitBlame"/> default: only the cursor's line is annotated. Also what the toggle command
	/// turns back on to.
	/// </summary>
	public const string GitBlameCurrentLine = "currentLine";

	/// <summary>The <see cref="GitBlame"/> value annotating each line that starts a commit's run.</summary>
	public const string GitBlameAll = "all";

	// Monaco's standard default; long enough to avoid flicker on a quick mouse pass. 0 (instant) is the floor.
	private const long DefaultHoverDelay = 300;
	private const long MaxHoverDelay = 5000;

	// Measured in Chromium: a wheel notch scrolls 50px at a multiplier of 1 — under three lines, slower than the
	// rest of the desktop. 5 lands a notch near 14 lines. The two multiply, so Alt-scroll is 25x the baseline.
	private const long DefaultMouseWheelScrollSensitivity = 5;
	private const long DefaultFastScrollSensitivity = 5;
	private const long MinScrollSensitivity = 1;
	private const long MaxScrollSensitivity = 20;

	/// <summary>Registers every editor-behavior setting into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = InlayHints,
			Kind = SettingKind.String,
			Description = "Inline type and parameter-name hints in the editor (the greyed ': Type' and "
				+ "'name:' annotations). 'offUnlessPressed' hides them until you hold Ctrl+Alt; "
				+ "'onUnlessPressed' shows them until you do.",
			Aliases = ["inlay hints", "type hints", "parameter hints", "inline hints", "parameter name hints"],
			AllowedValues = ["on", "off", "offUnlessPressed", "onUnlessPressed"],
			Apply = ApplyMode.Live,
			Default = "on",
		});

		registry.Register(Toggle(Minimap, "Show the minimap (code overview) on the editor's right edge.",
			["minimap", "code overview", "code map"], false));
		registry.Register(Toggle(BracketPairColorization,
			"Colorize matching bracket pairs by nesting depth.",
			["bracket pair colorization", "bracket colors", "rainbow brackets"], true));
		registry.Register(Toggle(SmoothScrolling, "Animate editor and terminal scrolling instead of jumping.",
			["smooth scrolling", "animated scrolling"], true));
		registry.Register(Choice(CursorSmoothCaretAnimation, "Animate the cursor's caret as it moves.",
			["cursor animation", "caret animation", "smooth caret"], ["off", "on", "explicit"], "off"));
		registry.Register(Choice(RenderWhitespace, "Render whitespace characters (spaces and tabs).",
			["render whitespace", "show whitespace", "show spaces and tabs"],
			["none", "boundary", "selection", "trailing", "all"], "none"));
		registry.Register(Toggle(ScrollBeyondLastLine, "Allow scrolling past the last line of the file.",
			["scroll beyond last line", "scroll past end"], true));

		registry.Register(Ranged(MouseWheelScrollSensitivity,
			"How far one mouse-wheel notch scrolls the editor, as a multiplier. Defaults to 5; Monaco's own "
				+ "default of 1 scrolls under three lines a notch, which feels slower than the rest of the desktop.",
			["scroll speed", "mouse wheel speed", "scroll sensitivity", "mouse wheel scroll sensitivity",
				"lines per scroll", "wheel scroll speed", "scroll faster", "scroll slower"],
			DefaultMouseWheelScrollSensitivity, MinScrollSensitivity, MaxScrollSensitivity, "times"));
		registry.Register(Ranged(FastScrollSensitivity,
			"How far the editor scrolls while Alt is held, as a multiplier applied on top of "
				+ "editor.mouseWheelScrollSensitivity — the two multiply, so the defaults make Alt-scroll 25x a "
				+ "normal notch.",
			["fast scroll", "fast scroll sensitivity", "alt scroll speed", "fast scrolling"],
			DefaultFastScrollSensitivity, MinScrollSensitivity, MaxScrollSensitivity, "times"));
		registry.Register(Toggle(MiddleClickAutoscroll,
			"Middle-click, then move the mouse to scroll continuously — further from the click point scrolls "
				+ "faster. Middle-click again (or press Escape) to stop. Applies to the editor on every platform, "
				+ "and to the structured-agent transcript on Linux (elsewhere the system scrolls it). On by default.",
			["middle click autoscroll", "autoscroll", "auto scroll", "middle mouse scrolling", "middle click scroll",
				"scroll on middle click", "drag to scroll", "Linux autoscroll"],
			true));

		// Common preferences (defaults = Monaco's).
		registry.Register(Choice(WordWrap, "Wrap long lines so they stay within the viewport.",
			["word wrap", "line wrap", "wrap lines"], ["off", "on", "wordWrapColumn", "bounded"], "off"));
		registry.Register(Choice(LineNumbers, "How line numbers are shown in the gutter.",
			["line numbers", "gutter numbers", "relative line numbers"], ["on", "off", "relative", "interval"], "on"));
		registry.Register(Choice(CursorBlinking, "Cursor blinking style.",
			["cursor blinking", "caret blinking", "cursor style"], ["blink", "smooth", "phase", "expand", "solid"], "blink"));
		registry.Register(Choice(RenderLineHighlight, "How the current line is highlighted.",
			["line highlight", "current line highlight", "active line highlight"], ["none", "gutter", "line", "all"], "line"));
		registry.Register(Toggle(StickyScroll,
			"Pin the enclosing scopes (namespace/class/method) to the top of the editor as you scroll.",
			["sticky scroll", "sticky headers", "pinned scopes"], true));
		registry.Register(Toggle(FontLigatures, "Enable font ligatures (for fonts that provide them).",
			["font ligatures", "ligatures", "coding ligatures"], false));
		registry.Register(Toggle(IndentGuides, "Show indentation guide lines.",
			["indent guides", "indentation guides", "indent lines"], true));

		registry.Register(Ranged(HoverDelay,
			"Delay in milliseconds before the hover tooltip appears over a symbol. "
				+ "Defaults to 300 (Monaco's standard); 0 means it appears instantly.",
			["hover delay", "hover duration", "hover time", "tooltip delay", "tooltip duration"],
			DefaultHoverDelay, 0, MaxHoverDelay, "milliseconds"));

		registry.Register(Toggle(SuggestExpandDocs,
			"Auto-expand the documentation panel beside the autocomplete list, so a function's docs and "
				+ "signature show automatically without pressing Ctrl+Space.",
			["suggestion docs", "completion documentation", "expand suggestion docs", "autocomplete docs",
				"show completion documentation"],
			true));

		registry.Register(Choice(CommentProse,
			"Render comments as styled prose — markers stripped, italic, with inline `code` chips — line-for-line, "
				+ "preserving your line breaks. Click a rendered comment (or arrow into it) to edit its source. "
				+ "'none' renders nothing; 'documentation' only doc comments (///, /** */), including single-line; "
				+ "'multiline' also any comment spanning 2+ lines; 'all' also lone single-line comments.",
			["comment prose", "render comments", "pretty comments", "comment rendering", "prose comments",
				"format comments"],
			["none", "documentation", "multiline", "all"], "documentation"));

		registry.Register(Toggle(PaneShortcutHints,
			"Show the Ctrl+N pane-switch shortcut hint badges in the pane headers and the editor tab bar. "
				+ "On by default; turn it off to hide them once the shortcuts are familiar.",
			["pane shortcuts", "pane shortcut hints", "shortcut badges", "ctrl+n hints", "pane numbers",
				"hide shortcut hints"],
			true));

		registry.Register(Toggle(VideoAutoplay,
			"Start playback automatically when a video file opens in the media pane. On by default; "
				+ "turn it off to open videos paused.",
			["video autoplay", "autoplay video", "autoplay", "auto play videos", "play videos automatically"],
			true));

		registry.Register(Choice(GitBlame,
			"Show who last changed a line as a faded annotation at the end of it — author, when, and the commit "
				+ "subject. Click one to see the change that line came from, its pull request, and the other commits "
				+ "that touched the line or the file. 'currentLine' (the default) annotates only the line the cursor "
				+ "is on; 'all' annotates every line that starts a commit's run, so a stretch of lines from one "
				+ "commit is labelled once at its top rather than on every line; 'off' none.",
			["git blame", "blame", "blame annotations", "git lens", "gitlens", "line authorship", "who wrote this",
				"inline blame", "commit annotations"],
			[GitBlameOff, GitBlameCurrentLine, GitBlameAll], GitBlameCurrentLine));
	}

	/// <summary>
	/// Serializes the resolved editor options for bootstrap injection or a settings feature event.
	/// </summary>
	public static string BuildJson(SettingsStore store) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.ToText(writer => WriteOptions(writer, store));
	}

	// Fallback-free Require* accessors: a literal default here would be a second source that can drift, so a
	// misregistered setting throws rather than silently serializing a stale literal.
	private static void WriteOptions(Utf8JsonWriter writer, SettingsStore store) {
		writer.WriteStartObject();
		writer.WriteString("inlayHints", store.RequireString(InlayHints));
		writer.WriteBoolean("minimap", store.RequireBool(Minimap));
		writer.WriteBoolean("bracketPairColorization", store.RequireBool(BracketPairColorization));
		writer.WriteBoolean("smoothScrolling", store.RequireBool(SmoothScrolling));
		writer.WriteString("cursorSmoothCaretAnimation", store.RequireString(CursorSmoothCaretAnimation));
		writer.WriteString("renderWhitespace", store.RequireString(RenderWhitespace));
		writer.WriteBoolean("scrollBeyondLastLine", store.RequireBool(ScrollBeyondLastLine));
		writer.WriteNumber("mouseWheelScrollSensitivity", store.RequireInt(MouseWheelScrollSensitivity));
		writer.WriteNumber("fastScrollSensitivity", store.RequireInt(FastScrollSensitivity));
		writer.WriteBoolean("middleClickAutoscroll", store.RequireBool(MiddleClickAutoscroll));
		writer.WriteString("wordWrap", store.RequireString(WordWrap));
		writer.WriteString("lineNumbers", store.RequireString(LineNumbers));
		writer.WriteString("cursorBlinking", store.RequireString(CursorBlinking));
		writer.WriteString("renderLineHighlight", store.RequireString(RenderLineHighlight));
		writer.WriteBoolean("stickyScroll", store.RequireBool(StickyScroll));
		writer.WriteBoolean("fontLigatures", store.RequireBool(FontLigatures));
		writer.WriteBoolean("indentGuides", store.RequireBool(IndentGuides));
		writer.WriteNumber("hoverDelay", store.RequireInt(HoverDelay));
		writer.WriteBoolean("suggestExpandDocs", store.RequireBool(SuggestExpandDocs));
		writer.WriteString("commentProse", store.RequireString(CommentProse));
		writer.WriteBoolean("paneShortcutHints", store.RequireBool(PaneShortcutHints));
		writer.WriteBoolean("videoAutoplay", store.RequireBool(VideoAutoplay));
		writer.WriteString("gitBlame", store.RequireString(GitBlame));
		writer.WriteEndObject();
	}

	private static SettingDefinition Toggle(
		string key, string description, IReadOnlyList<string> aliases, bool def) =>
		new() {
			Key = key,
			Kind = SettingKind.Bool,
			Description = description,
			Aliases = aliases,
			Apply = ApplyMode.Live,
			Default = def,
		};

	private static SettingDefinition Choice(
		string key, string description, IReadOnlyList<string> aliases, IReadOnlyList<string> allowed, string def) =>
		new() {
			Key = key,
			Kind = SettingKind.String,
			Description = description,
			Aliases = aliases,
			AllowedValues = allowed,
			Apply = ApplyMode.Live,
			Default = def,
		};

	// A bounded integer setting. Out-of-range fails loudly at the surface that set it — never clamped.
	private static SettingDefinition Ranged(
		string key, string description, IReadOnlyList<string> aliases, long def, long min, long max,
		string unit) =>
		new() {
			Key = key,
			Kind = SettingKind.Int,
			Description = description,
			Aliases = aliases,
			Apply = ApplyMode.Live,
			Default = def,
			Validate = value => value is long number && number >= min && number <= max
				? ValidationResult.Success
				: ValidationResult.Failure($"{key} must be between {min} and {max} {unit}."),
		};
}
