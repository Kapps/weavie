namespace Weavie.Core.Lsp;

/// <summary>
/// The workspace's live set of installable language-server misses, fed by failed <c>lsp-start</c>s (the
/// moment a user actually opened a file and got no server) and consumed by the install suggestion cards. An
/// offer is recorded only when Weavie can honor it deterministically: a candidate carries an install recipe
/// and its toolchain is on <c>PATH</c>. In-memory only — re-derived from real failures each run, never stale.
/// </summary>
public sealed class ServerInstallOffers {
	private readonly Func<string, string?> _findOnPath;
	private readonly Func<LanguageServerDescriptor, ResolvedCommand?> _resolve;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, ServerInstallOffer> _offers = new(StringComparer.Ordinal);

	/// <summary>Creates the set over the PATH/resolution seams (<see cref="ServerResolver"/> in production, fakes in tests).</summary>
	/// <param name="findOnPath">Locates a command on <c>PATH</c> (the toolchain gate).</param>
	/// <param name="resolve">Resolves a descriptor to a launchable server, or null.</param>
	public ServerInstallOffers(Func<string, string?> findOnPath, Func<LanguageServerDescriptor, ResolvedCommand?> resolve) {
		ArgumentNullException.ThrowIfNull(findOnPath);
		ArgumentNullException.ThrowIfNull(resolve);
		_findOnPath = findOnPath;
		_resolve = resolve;
	}

	/// <summary>Descriptor ids with a live offer — the install cards' relevance supplier.</summary>
	public IReadOnlyCollection<string> ActiveIds {
		get {
			lock (_gate) {
				return [.. _offers.Keys];
			}
		}
	}

	/// <summary>All live offers, in no particular order (the omitted-arg install command's target).</summary>
	public IReadOnlyList<ServerInstallOffer> Active {
		get {
			lock (_gate) {
				return [.. _offers.Values];
			}
		}
	}

	/// <summary>The live offer for <paramref name="descriptorId"/>, or <see langword="null"/>.</summary>
	public ServerInstallOffer? For(string descriptorId) {
		ArgumentException.ThrowIfNullOrEmpty(descriptorId);
		lock (_gate) {
			return _offers.GetValueOrDefault(descriptorId);
		}
	}

	/// <summary>
	/// Records a failed start for <paramref name="descriptor"/> as an offer, when one is honorable: the
	/// descriptor (still) doesn't resolve and a candidate carries a recipe whose toolchain is on <c>PATH</c>.
	/// Returns whether the active set changed (a retry storm of the same miss is an idempotent no-op).
	/// </summary>
	public bool RecordUnresolved(LanguageServerDescriptor descriptor) {
		ArgumentNullException.ThrowIfNull(descriptor);
		lock (_gate) {
			if (_offers.ContainsKey(descriptor.Id) || _resolve(descriptor) is not null) {
				return false;
			}

			foreach (var candidate in descriptor.Candidates) {
				if (candidate.Install is { } recipe && _findOnPath(recipe.Toolchain) is not null) {
					_offers[descriptor.Id] = new ServerInstallOffer(descriptor, candidate, recipe);
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>Drops every offer whose descriptor now resolves (after an install). Returns whether the set changed.</summary>
	public bool Recompute() {
		lock (_gate) {
			string[] resolved = [.. _offers.Values.Where(o => _resolve(o.Descriptor) is not null).Select(o => o.Descriptor.Id)];
			foreach (string id in resolved) {
				_offers.Remove(id);
			}

			return resolved.Length > 0;
		}
	}
}
