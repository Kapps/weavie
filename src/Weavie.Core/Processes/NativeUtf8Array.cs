using System.Runtime.InteropServices;

namespace Weavie.Core.Processes;

internal sealed class NativeUtf8Array : IDisposable {
	private readonly nint[] _items;
	private readonly GCHandle _handle;

	public NativeUtf8Array(IEnumerable<string> values) {
		_items = [.. values.Select(Marshal.StringToCoTaskMemUTF8), IntPtr.Zero];
		_handle = GCHandle.Alloc(_items, GCHandleType.Pinned);
	}

	public nint Pointer => _handle.AddrOfPinnedObject();

	public void Dispose() {
		_handle.Free();
		foreach (nint item in _items) if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
	}
}
