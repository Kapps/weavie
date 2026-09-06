# Releases and update channels

Weavie has one public version across the desktop shells, runner, worker, and embedded web app.
`0.2.1.1507` displays public version `0.2.1` and internal build `1507`. Public versions change only
on a manual stable release. The build number is `release.yml`'s run number, shared by both channels;
ordinary CI and local builds use build zero. `Directory.Build.props` declares the initial public
baseline (`0.1.0`). The latest published `vX.Y.Z` release supplies subsequent public versions.

Assembly and file versions use the three-component public version. Informational version and the
web bootstrap include the internal build number. The runner bundle's manifest and managed
`versions/<build>/` layout use the integer build number independently of public version arithmetic.

## Releasing from GitHub

The repository owner opens **Actions → release → Run workflow** on `main`:

| Input | Meaning |
| --- | --- |
| `channel` | `stable` (default) or `latest` |
| `bump` | `patch` (default), `minor`, or `major`; applies only to stable |
| `source` | Published build number or full 40-character commit SHA; blank pins `main-latest` |

For stable `0.2.1`, patch selects `0.2.2`, minor selects `0.3.0`, and major selects `1.0.0`.
Before the first stable release, the same arithmetic applies to the baseline: minor selects `0.2.0`.
Starting the workflow authorizes publication after validation; there is no additional approval click.

```bash
# Release the current main-latest source as the next minor version.
gh workflow run release.yml --ref main -f channel=stable -f bump=minor

# Release a specific recorded build's source as the next patch version.
gh workflow run release.yml --ref main -f channel=stable -f bump=patch -f source=1507

# Build a specific main-history commit for the latest channel.
gh workflow run release.yml --ref main -f channel=latest -f source=<full-commit-sha>
```

The source selector chooses **source code**, not an existing binary. A new run builds that exact
commit with its newly allocated build identity and, for stable, the new public version. The source
must belong to `main`'s history. Manual latest builds can explicitly select an earlier source;
automatic latest publication refuses to replace a newer main commit with an older one. Latest publication also checks the published runner manifest and rejects older
or identical build numbers, so retrying a superseded run cannot rewind the feed.

Every validated publication records an immutable annotated `build-<number>` tag containing its
source commit, public version, build number, and channel. Numbered selection uses that provenance,
not the workflow run's `head_sha` (which can identify the workflow code rather than the build source).
Build-number selection is available for builds recorded by this release system. Commit selection
requires source that supports the current release contract.

## Validation and publication

Normal PR/push CI runs web checks, shared .NET tests, Linux native checks, and Linux E2E. Linux also
runs full .NET formatting for every evaluable project, including Windows source, and whitespace
formatting for macOS source. Full macOS semantic formatting runs on stable release validation.
The project list comes from `weavie.slnx`; the platform catalog lives in `.github/platforms.json`.

Automatic latest builds start only after successful push CI for this repository's `main`. They
publish the Linux desktop archive and runner bundle to the rolling `main-latest` GitHub prerelease.
A manual latest build runs Linux CI against its pinned source before publishing.

Stable runs the same checks plus Windows/macOS native checks and E2E. Every checkout, including
E2E bundle, shards, and report, uses the pinned source commit. The write-privileged final job runs
publisher code from the trusted workflow revision, with no credentials persisted in build checkouts.
Setup actions also come from that trusted revision. Jobs validating selected release sources never
save shared dependency or browser caches.

After all checks and packages pass, stable publication:

1. Creates annotated `vX.Y.Z` with the exact source and build provenance.
2. Uploads all native archives, runner bundle, and `release-plan.json` to a draft GitHub Release.
3. Publishes that complete, permanent release with generated release notes.
4. Advances `stable` to the **same annotated tag object** as `vX.Y.Z`.
5. Marks the versioned release as GitHub's latest stable release for easy discovery.

There is no separate mutable `stable` Release with duplicated assets. The `stable` Git ref is a
single atomic pointer to the permanent versioned release. Downloads are the exact validated
artifacts, never rebuilt during promotion. Users download stable releases from
[GitHub Releases](https://github.com/Kapps/weavie/releases/latest).

## Failure and concurrency

Stable attempts serialize planning through publication, so simultaneous requests cannot allocate
the same public version. GitHub concurrency retains the active run and one pending successor;
another queued request can replace that pending run. The latest channel has its own concurrency group.

Failed checks do not create a versioned release or move stable. A failed upload cleans its draft and
unpublished version tag, leaving stable intact. A release that was published before stable promotion
failed remains permanent: that public version is consumed. **Re-run failed jobs** to finish its
promotion from the same plan and artifacts, without rebuilding or replacing published assets.
An intervening newer stable release blocks promotion of the superseded attempt.

Re-running all jobs is rejected because it would recalculate the version under an existing build
number. Start a new workflow when you intend to rebuild. API/cleanup failures remain visible as
failed workflow steps; they never authorize moving a channel to an incomplete release.

## Runner channels

```bash
./current/Weavie.Runner --auto-update          # stable
./current/Weavie.Runner --auto-update stable   # stable
./current/Weavie.Runner --auto-update latest   # main-latest
```

Without the flag, updates are off. Unknown channels and duplicate flags fail at startup. The
selected channel is visible on the runner status page. Authentication uses `--github-token` for
private release feeds.

Stable resolves `git/ref/tags/stable`, loads the annotated tag object to obtain `vX.Y.Z`, then reads
that version's release assets and digests. Once resolved, the immutable version pins the download
even if another stable release is promoted concurrently. Latest reads the `main-latest` release.

Switching from latest to stable does not downgrade an installed newer build: the status page
explains that it is waiting for a newer stable build. Installation and worker draining follow
[the runner update lifecycle](runner-auto-update.md).
