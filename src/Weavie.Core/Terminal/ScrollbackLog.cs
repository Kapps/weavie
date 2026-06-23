namespace Weavie.Core.Terminal;

/// <summary>
/// A capped, on-disk record of one shell pane's raw PTY output, so a (re)attaching client can replay a coherent
/// screen with the previous process's output faded above the live stream — the shell's analogue to claude's
/// <c>--resume</c>.
/// <para>An in-memory boundary (the byte offset of the most recent process restart) splits faded history from
/// live output; keeping it in memory rather than as a stream sentinel means it can never clash with real output
/// or be split by a trim. Access is lock-serialized and best-effort: every operation degrades to a no-op on I/O
/// error so losing scrollback never breaks a session. Trimming cuts at a newline boundary, which never splits an
/// ANSI escape (escapes contain no <c>\n</c>).</para>
/// </summary>
public sealed class ScrollbackLog : IDisposable {
	// Dim-grey SGR wrapping the faded (previous-process) region, plus a separator marking the restart point.
	private static readonly byte[] FadedPrefix = "[90m"u8.ToArray();
	private static readonly byte[] FadedSuffix = "[0m"u8.ToArray();
	private static readonly byte[] Separator = "\r\n[90m── session resumed ──[0m\r\n"u8.ToArray();

	private readonly object _gate = new();
	private readonly int _capBytes;
	private FileStream? _stream;
	private long _boundary;
	private bool _disposed;

	/// <summary>
	/// Opens (or creates) the log at <paramref name="path"/>, capped at <paramref name="capBytes"/>; any existing
	/// content is treated as faded history until the next <see cref="MarkBoundary"/>. An I/O failure here disables
	/// the log (all operations become no-ops) rather than throwing, so a session always comes up.
	/// </summary>
	public ScrollbackLog(string path, int capBytes) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		_capBytes = capBytes;
		try {
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir)) {
				Directory.CreateDirectory(dir);
			}

			// ReadWrite so BuildReplay can read back through the same handle; seek to end for appends.
			_stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
			_stream.Seek(0, SeekOrigin.End);
			_boundary = _stream.Length;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
			_stream = null;
		}
	}

	/// <summary>Appends raw PTY output bytes, trimming the front past the cap (at a newline boundary). No-op when disabled.</summary>
	public void Append(ReadOnlySpan<byte> data) {
		if (data.IsEmpty) {
			return;
		}

		lock (_gate) {
			if (_disposed || _stream is null) {
				return;
			}

			try {
				_stream.Write(data);
				_stream.Flush();
				TrimLocked();
			} catch (IOException) {
				// best-effort: keep the session alive even if a write fails
			}
		}
	}

	/// <summary>Marks "a new process starts here" — everything written so far becomes faded history on replay.</summary>
	public void MarkBoundary() {
		lock (_gate) {
			if (_stream is not null) {
				_boundary = _stream.Length;
			}
		}
	}

	/// <summary>
	/// The replay blob for an attaching client: the faded region (sanitized, dim-grey-wrapped, with a separator)
	/// followed by the live region raw. Empty when there's nothing to replay or the log is disabled.
	/// </summary>
	public byte[] BuildReplay() {
		lock (_gate) {
			if (_stream is null) {
				return [];
			}

			byte[] all;
			try {
				_stream.Flush();
				all = new byte[_stream.Length];
				_stream.Seek(0, SeekOrigin.Begin);
				int read = 0;
				while (read < all.Length) {
					int n = _stream.Read(all, read, all.Length - read);
					if (n == 0) {
						break;
					}

					read += n;
				}

				_stream.Seek(0, SeekOrigin.End);
			} catch (IOException) {
				return [];
			}

			if (all.Length == 0) {
				return [];
			}

			int boundary = (int)Math.Clamp(_boundary, 0, all.Length);
			byte[] faded = SanitizeForFaded(all.AsSpan(0, boundary));
			ReadOnlySpan<byte> live = all.AsSpan(boundary);

			using var blob = new MemoryStream(faded.Length + live.Length + 64);
			if (faded.Length > 0) {
				blob.Write(FadedPrefix);
				blob.Write(faded);
				blob.Write(FadedSuffix);
				blob.Write(Separator);
			}

			blob.Write(live);
			return blob.ToArray();
		}
	}

	/// <summary>
	/// Strips ANSI escapes (CSI, OSC, other ESC forms) and stray control bytes from <paramref name="data"/>,
	/// keeping printable bytes, <c>\r\n\t</c>, and UTF-8 multibyte. Faded history thus replays as plain dim text
	/// that can't corrupt the live screen or override the dim color.
	/// </summary>
	public static byte[] SanitizeForFaded(ReadOnlySpan<byte> data) {
		var output = new List<byte>(data.Length);
		int i = 0;
		while (i < data.Length) {
			byte b = data[i];
			if (b == 0x1b) { // ESC
				i++;
				if (i >= data.Length) {
					break;
				}

				byte next = data[i];
				if (next == (byte)'[') { // CSI ... final byte 0x40-0x7E
					i++;
					while (i < data.Length && (data[i] < 0x40 || data[i] > 0x7e)) {
						i++;
					}

					if (i < data.Length) {
						i++; // consume the final byte
					}
				} else if (next == (byte)']') { // OSC ... terminated by BEL or ST (ESC \)
					i++;
					while (i < data.Length) {
						if (data[i] == 0x07) {
							i++;
							break;
						}

						if (data[i] == 0x1b && i + 1 < data.Length && data[i + 1] == (byte)'\\') {
							i += 2;
							break;
						}

						i++;
					}
				} else {
					i++; // other ESC form (charset select, etc.) — drop the introducer byte
				}

				continue;
			}

			if (b is (byte)'\n' or (byte)'\r' or (byte)'\t' or >= 0x20) {
				output.Add(b);
			}

			i++;
		}

		return [.. output];
	}

	/// <summary>Trims the front of the file back to the cap at a newline boundary once it grows past twice the cap
	/// (so a stream sitting just over the cap doesn't rewrite the whole file on every append).</summary>
	private void TrimLocked() {
		if (_stream is null || _stream.Length <= (long)_capBytes * 2) {
			return;
		}

		byte[] all = new byte[_stream.Length];
		_stream.Seek(0, SeekOrigin.Begin);
		int read = 0;
		while (read < all.Length) {
			int n = _stream.Read(all, read, all.Length - read);
			if (n == 0) {
				break;
			}

			read += n;
		}

		// Keep the last _capBytes, advanced to the next newline so a line/escape isn't split at the top.
		int cut = all.Length - _capBytes;
		int newline = Array.IndexOf(all, (byte)'\n', cut);
		int keepFrom = newline >= 0 ? newline + 1 : cut;

		_stream.Seek(0, SeekOrigin.Begin);
		_stream.Write(all, keepFrom, all.Length - keepFrom);
		_stream.SetLength(all.Length - keepFrom);
		_stream.Flush();
		_stream.Seek(0, SeekOrigin.End);
		_boundary = Math.Max(0, _boundary - keepFrom);
	}

	/// <summary>Closes the log file. The on-disk content is left in place so a later resume can replay it faded.</summary>
	public void Dispose() {
		lock (_gate) {
			_disposed = true;
			_stream?.Dispose();
			_stream = null;
		}
	}
}
