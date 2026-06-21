namespace Weavie.Core.Terminal;

/// <summary>
/// A capped, on-disk record of one terminal pane's <em>output</em>, so a client that (re)attaches to a
/// session can replay a coherent screen instead of a blank pane — and a <em>resumed</em> session (the
/// process restarted, or the whole backend rebooted) can show the previous process's output faded above
/// the live stream. This is the shell's analogue to claude's <c>--resume</c>: the shell has no durable
/// state of its own, so its scrollback is persisted here. Only the shell pane is logged (claude is a
/// full-screen TUI that repaints itself).
///
/// <para>The log is a single byte stream — raw PTY output — plus an in-memory <em>boundary</em>: the byte
/// offset of the most recent process (re)start. Bytes before the boundary are previous-process history
/// (rendered faded on replay); bytes after it are the current live process (rendered raw, to reconstruct
/// its screen). The boundary is tracked in memory rather than as a sentinel in the stream, so it can never
/// clash with real output or be split by a trim.</para>
///
/// <para>All file access is serialized by an internal lock, and every operation degrades to a no-op on an
/// I/O error (the log is best-effort: losing scrollback must never break a session). The cap is enforced
/// by trimming the front at a newline boundary — ANSI escape sequences never contain <c>\n</c>, so a
/// newline-aligned cut never splits one.</para>
/// </summary>
public sealed class ScrollbackLog : IDisposable {
	// Dim-grey SGR wrapping the faded (previous-process) region, and a faint separator marking the restart
	// point. Inner SGR is stripped from the faded text (see SanitizeForFaded) so nothing re-colors it.
	private static readonly byte[] FadedPrefix = "[90m"u8.ToArray();
	private static readonly byte[] FadedSuffix = "[0m"u8.ToArray();
	private static readonly byte[] Separator = "\r\n[90m── session resumed ──[0m\r\n"u8.ToArray();

	private readonly object _gate = new();
	private readonly int _capBytes;
	private FileStream? _stream;
	private long _boundary;
	private bool _disposed;

	/// <summary>
	/// Opens (or creates) the log at <paramref name="path"/>, capped at <paramref name="capBytes"/>. The
	/// boundary starts at the existing file length, so any content already on disk (a prior process, a
	/// prior boot) is treated as faded history until the next <see cref="MarkBoundary"/>. An I/O failure
	/// here leaves the log disabled (all operations become no-ops) rather than throwing — a session must
	/// come up even if its scrollback can't be persisted.
	/// </summary>
	public ScrollbackLog(string path, int capBytes) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		_capBytes = capBytes;
		try {
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir)) {
				Directory.CreateDirectory(dir);
			}

			// ReadWrite so BuildReplay can read back through the same handle; Open seeks to end for appends.
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
	/// Composes the replay blob for an attaching client: the faded region (sanitized to plain text and
	/// wrapped in dim grey, with a separator) followed by the live region written raw. Returns an empty
	/// array when there's nothing to replay (or the log is disabled).
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
	/// Strips ANSI escape sequences (CSI, OSC, and other ESC-prefixed forms) and stray control bytes from
	/// <paramref name="data"/>, keeping printable bytes plus <c>\r</c>/<c>\n</c>/<c>\t</c>. Replayed faded
	/// history is shown as plain dim text, so old cursor moves / screen clears can't corrupt the current
	/// screen and old color codes can't override the dim. UTF-8 multibyte sequences are preserved.
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
