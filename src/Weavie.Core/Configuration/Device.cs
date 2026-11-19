using System.Text.Json;
using Weavie.Core.Theming;

namespace Weavie.Core.Configuration;

/// <summary>A device tool's answer: the text for the agent, and whether it is an error.</summary>
public sealed record ToolReply(string Text, bool IsError);

/// <summary>One theme tool call, run at the device's home: the tool's name and its arguments object.</summary>
public sealed record ThemeRequest(string Tool, JsonElement Args);

/// <summary>The device a session belongs to, whose home answers the device tools. See <c>docs/specs/settings-ownership.md</c>.</summary>
public interface IDevice {
	/// <summary>Answers a settings tool for a device key at the device's home, or throws <see cref="DeviceUnavailableException"/>.</summary>
	Task<ToolReply> SettingsAsync(SettingsRequest request, CancellationToken ct);

	/// <summary>Answers a theme tool at the device's home, or throws <see cref="DeviceUnavailableException"/>.</summary>
	Task<ToolReply> ThemesAsync(ThemeRequest request, CancellationToken ct);
}

/// <summary>A session's device can't be reached to carry a request to its home.</summary>
public sealed class DeviceUnavailableException(string message) : Exception(message);

/// <summary>This host answering as the home of its own device.</summary>
public sealed class HomeDevice(SettingsTools settings, ThemeTools themes) : IDevice {
	/// <inheritdoc/>
	public Task<ToolReply> SettingsAsync(SettingsRequest request, CancellationToken ct) => Task.FromResult(settings.Device(request));

	/// <inheritdoc/>
	public Task<ToolReply> ThemesAsync(ThemeRequest request, CancellationToken ct) => Task.FromResult(themes.Run(request));
}
