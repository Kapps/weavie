using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SpellCommandTests {
	[Theory]
	[InlineData(CoreCommands.SpellingShowActions, "F7")]
	[InlineData(CoreCommands.SpellingAddToProjectDictionary, "$mod+alt+p")]
	[InlineData(CoreCommands.SpellingAddToUserDictionary, "$mod+alt+u")]
	public void RegisteredCommands_UseTheDeclaredKeyboardPaths(string commandId, string key) {
		var command = CoreCommands.CreateRegistry().Require(commandId);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("editorFocused", command.When);
		Assert.Equal(key, Assert.Single(command.DefaultKeybindings).Key);
	}

	[Fact]
	public void ApplySuggestion_IsParameterizedAndContextual() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.SpellingApplySuggestion);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.False(command.ShowInPalette);
		Assert.Equal("editorFocused", command.When);
		Assert.Contains("replacement", command.ArgsSchemaJson);
	}
}
