using Weavie.Core.Theming;

namespace Weavie.Core.Tests;

/// <summary>A screen whose reported polarity a test sets; null is no page connected.</summary>
internal sealed class TestScreen : IThemeScreen {
	public string? Showing { get; set; }
}
