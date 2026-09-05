namespace Weavie.Win.Hosting;

internal static class WinUiFailure {
	internal static void Report(Exception failure) {
		Console.Error.WriteLine(failure);
		MessageBox.Show(failure.ToString(), "Weavie couldn't complete an internal action",
			MessageBoxButtons.OK, MessageBoxIcon.Error);
	}
}
