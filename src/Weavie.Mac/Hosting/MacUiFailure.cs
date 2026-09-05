namespace Weavie.Mac.Hosting;

internal static class MacUiFailure {
	internal static void Report(Exception failure) {
		Console.Error.WriteLine(failure);
		using var alert = new NSAlert {
			AlertStyle = NSAlertStyle.Critical,
			MessageText = "Weavie couldn't complete an internal action",
			InformativeText = failure.ToString(),
		};
		alert.RunModal();
	}
}
