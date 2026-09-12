using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weavie.Hosting;

/// <summary>Owns the native bridge capability and the sole document allowed to receive it.</summary>
public sealed class NativeBridgeSecurity {
	private readonly SemaphoreSlim _loads = new(1);
	private Uri? _document;
	private string _token = string.Empty;
	private int _revision;

	/// <summary>Revokes authority before rendering recovery HTML or closing the window.</summary>
	public void Revoke() {
		Interlocked.Increment(ref _revision);
		_token = string.Empty;
		_document = null;
	}

	/// <summary>Allows only the selected app document; subframes must stay outside its origin.</summary>
	public bool Allows(string? url, bool mainFrame) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate)) return false;
		if (_document is not { } document) return mainFrame && url == "about:blank";
		bool sameOrigin = candidate.Scheme == document.Scheme && candidate.IdnHost == document.IdnHost && candidate.Port == document.Port;
		return mainFrame
			? sameOrigin && candidate.UserInfo.Length == 0 && candidate.AbsolutePath == document.AbsolutePath
				&& (candidate.Query.Length == 0 || candidate.Query == document.Query)
			: !sameOrigin;
	}

	/// <summary>Replaces startup scripts atomically with the capability for the host-selected document.</summary>
	public async Task LoadAsync(string url, string startup, string sender, Func<string, Task> install, Action<string> navigate) {
		int revision = Interlocked.Increment(ref _revision);
		await _loads.WaitAsync().ConfigureAwait(false);
		try {
			if (revision != _revision) return;
			var document = new Uri(url);
			if (document.Scheme is not ("http" or "https" or "app") || document.Host.Length == 0 || document.UserInfo.Length != 0)
				throw new ArgumentException("The app document requires an explicit origin.", nameof(url));
			_document = document;
			_token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			await install(Script(url, startup, sender)).ConfigureAwait(false);
			if (revision == _revision) navigate(url);
		} finally {
			_loads.Release();
		}
	}

	/// <summary>Unwraps authenticated strings before welcome or workspace dispatch sees them.</summary>
	public string? Authenticate(string message) {
		int length = _token.Length;
		return length > 0 && message.Length > length && message[length] == ':'
			&& CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(message[..length]))
			? message[(length + 1)..] : null;
	}

	private string Script(string url, string startup, string sender) => $$"""
		(function () {
			if (this !== this.top) return;
			const target = this;
			const url = this.location.href.split('#')[0];
			const allowed = this.origin === {{Literal(_document!.GetLeftPart(UriPartial.Authority))}}
				&& (url === {{Literal(url)}} || url === {{Literal(_document.GetLeftPart(UriPartial.Path))}});
			Object.defineProperty(this, '__weavieDeliver', {
				value: json => { if (allowed) target.__weavieReceive?.(json); }
			});
			if (!allowed) return;
			const token = {{Literal(_token)}};
			const send = {{sender}};
			Object.defineProperty(this, '__weaviePostMessage', {
				value: body => { if (typeof body === 'string') send(token + ':' + body); }
			});
			{{startup}}
		})();
		""";

	private static string Literal(string value) => $"\"{JsonEncodedText.Encode(value)}\"";
}
