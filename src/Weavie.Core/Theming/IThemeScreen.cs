namespace Weavie.Core.Theming;

/// <summary>What the pages this host serves are showing, which settles <c>system</c> mode for the theme tools.</summary>
public interface IThemeScreen {
	/// <summary>The polarity (<c>light</c>/<c>dark</c>) a page this host serves last reported, or null when none is connected.</summary>
	string? Showing { get; }
}
