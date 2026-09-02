using System.Text.Json.Serialization;

namespace Weavie.Hosting;

/// <summary>A fully resolved application-menu snapshot produced by the page that owns command selection.</summary>
public sealed record ApplicationMenuState {
	/// <summary>Monotonic page-local revision used to reject activation from a superseded native row.</summary>
	public required long Revision { get; init; }

	/// <summary>The resolved top-level menus in display order.</summary>
	public required IReadOnlyList<ApplicationMenuDefinition> Menus { get; init; }
}

/// <summary>One resolved top-level application menu.</summary>
public sealed record ApplicationMenuDefinition {
	/// <summary>The menu-bar label.</summary>
	public required string Label { get; init; }

	/// <summary>The menu's resolved rows.</summary>
	public required IReadOnlyList<ApplicationMenuEntry> Entries { get; init; }
}

/// <summary>The native presentation role of one resolved application-menu row.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationMenuEntryKind>))]
public enum ApplicationMenuEntryKind {
	/// <summary>An invokable command row.</summary>
	Command,

	/// <summary>A visual separator.</summary>
	Separator,

	/// <summary>A row that owns nested entries.</summary>
	Submenu,
}

/// <summary>One command, separator, or submenu in a resolved application menu.</summary>
public sealed record ApplicationMenuEntry {
	/// <summary>The row's native presentation role.</summary>
	public required ApplicationMenuEntryKind Kind { get; init; }

	/// <summary>The resolved command or submenu label; empty for a separator.</summary>
	public required string Label { get; init; }

	/// <summary>Whether the row is currently available under the page's command context.</summary>
	public required bool Enabled { get; init; }

	/// <summary>Opaque page-owned identity used for command activation; empty for non-command rows.</summary>
	public required string Token { get; init; }

	/// <summary>The live raw key specs advertised by the active command catalog.</summary>
	public required IReadOnlyList<string> Keys { get; init; }

	/// <summary>Optional native tooltip, such as the full path behind an Open Recent row.</summary>
	public string? ToolTip { get; init; }

	/// <summary>Resolved child rows for a submenu; empty for other row kinds.</summary>
	public required IReadOnlyList<ApplicationMenuEntry> Entries { get; init; }
}

/// <summary>An activation raised by a native command row.</summary>
public readonly record struct ApplicationMenuActivation(long Revision, string Token);

/// <summary>The platform surface that presents a page-resolved application menu.</summary>
public interface IApplicationMenu {
	/// <summary>Raised when the user activates one native command row.</summary>
	event Action<ApplicationMenuActivation> Activated;

	/// <summary>Replaces the platform's menu snapshot for this host window.</summary>
	void Apply(ApplicationMenuState state);

	/// <summary>Removes this host window's snapshot from the platform menu.</summary>
	void Clear();
}

/// <summary>An application-menu surface for platforms whose menu remains web-rendered.</summary>
public sealed class NoopApplicationMenu : IApplicationMenu {
	private NoopApplicationMenu() { }

	/// <summary>The shared no-op instance.</summary>
	public static NoopApplicationMenu Instance { get; } = new();

	/// <inheritdoc/>
	public event Action<ApplicationMenuActivation> Activated {
		add { }
		remove { }
	}

	/// <inheritdoc/>
	public void Apply(ApplicationMenuState state) => ArgumentNullException.ThrowIfNull(state);

	/// <inheritdoc/>
	public void Clear() { }
}
