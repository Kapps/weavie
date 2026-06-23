using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>
/// Editor-behavior settings — Monaco <c>IEditorOptions</c> surfaced as first-class, typed Weavie settings
/// (discoverable via <c>listSettings</c>, drivable via <c>setSetting</c> / natural language), the editor
/// analogue of <see cref="FontSettings"/>. All are <see cref="ApplyMode.Live"/>: a change re-pushes the
/// resolved options to the web, which applies them with <c>editor.updateOptions</c>.
/// <para>
/// The one exception is <see cref="SuggestExpandDocs"/>: there is no Monaco option that force-expands the
/// suggest-widget documentation flyout, so the web maps it to a small custom behavior rather than an
/// <c>updateOptions</c> field. Everything else is a straight passthrough.
/// </para>
/// <para>
/// Adding an option is one <c>Register</c> call here (+ one line in <c>BuildJson</c>) and one mapping line in
/// the web's <c>editor-options.ts</c> — no per-OS host work, since the host just relays <see cref="BuildJson"/>
/// like it does for fonts.
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

	/// <summary>Every editor-option key — the host subscribes to all of them to re-push on any change.</summary>
	public static readonly IReadOnlyList<string> Keys = [
		InlayHints, Minimap, BracketPairColorization, SmoothScrolling, CursorSmoothCaretAnimation,
		RenderWhitespace, ScrollBeyondLastLine, WordWrap, LineNumbers, CursorBlinking, RenderLineHighlight,
		StickyScroll, FontLigatures, IndentGuides, HoverDelay, SuggestExpandDocs, CommentProse,
	];

	// 300ms before the hover reveals — Monaco's standard default; long enough to avoid flicker on a quick mouse
	// pass. MaxHoverDelay keeps a fat-fingered value sane; 0 (instant) is the floor.
	private const long DefaultHoverDelay = 300;
	private const long MaxHoverDelay = 5000;

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
		registry.Register(Toggle(SmoothScrolling, "Animate editor scrolling instead of jumping.",
			["smooth scrolling", "animated scrolling"], false));
		registry.Register(Choice(CursorSmoothCaretAnimation, "Animate the cursor's caret as it moves.",
			["cursor animation", "caret animation", "smooth caret"], ["off", "on", "explicit"], "off"));
		registry.Register(Choice(RenderWhitespace, "Render whitespace characters (spaces and tabs).",
			["render whitespace", "show whitespace", "show spaces and tabs"],
			["none", "boundary", "selection", "trailing", "all"], "none"));
		registry.Register(Toggle(ScrollBeyondLastLine, "Allow scrolling past the last line of the file.",
			["scroll beyond last line", "scroll past end"], true));
		
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

		registry.Register(new SettingDefinition {
			Key = HoverDelay,
			Kind = SettingKind.Int,
			Description = "Delay in milliseconds before the hover tooltip appears over a symbol. "
				+ "Defaults to 300 (Monaco's standard); 0 means it appears instantly.",
			Aliases = ["hover delay", "hover duration", "hover time", "tooltip delay", "tooltip duration"],
			Apply = ApplyMode.Live,
			Default = DefaultHoverDelay,
			Validate = ValidateHoverDelay,
		});

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
	}

	/// <summary>
	/// Serializes the resolved editor options as JSON. With <paramref name="messageType"/> set, wraps them as
	/// <c>{"type":…,"options":{…}}</c> for a bridge push; otherwise emits the bare <c>{…}</c> object for the
	/// injected <c>window.__WEAVIE_EDITOR_OPTIONS__</c> global. Keys are the short camelCase names the web's
	/// editor-options.ts maps onto Monaco's nested option shape.
	/// </summary>
	public static string BuildJson(SettingsStore store, string? messageType) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.ToText(writer => {
			if (messageType is not null) {
				writer.WriteStartObject();
				writer.WriteString("type", messageType);
				writer.WritePropertyName("options");
				WriteOptions(writer, store);
				writer.WriteEndObject();
			} else {
				WriteOptions(writer, store);
			}
		});
	}

	// Values are read with the fallback-free Require* accessors: the resolved value already carries the
	// registered default (env → file → default), so a literal default here would be a second source that can
	// drift. A misregistered setting throws rather than silently serializing a stale literal.
	private static void WriteOptions(Utf8JsonWriter writer, SettingsStore store) {
		writer.WriteStartObject();
		writer.WriteString("inlayHints", store.RequireString(InlayHints));
		writer.WriteBoolean("minimap", store.RequireBool(Minimap));
		writer.WriteBoolean("bracketPairColorization", store.RequireBool(BracketPairColorization));
		writer.WriteBoolean("smoothScrolling", store.RequireBool(SmoothScrolling));
		writer.WriteString("cursorSmoothCaretAnimation", store.RequireString(CursorSmoothCaretAnimation));
		writer.WriteString("renderWhitespace", store.RequireString(RenderWhitespace));
		writer.WriteBoolean("scrollBeyondLastLine", store.RequireBool(ScrollBeyondLastLine));
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

	private static ValidationResult ValidateHoverDelay(object? value) =>
		value is long ms && ms is >= 0 and <= MaxHoverDelay
			? ValidationResult.Success
			: ValidationResult.Failure($"hover delay must be between 0 and {MaxHoverDelay} milliseconds.");
}
