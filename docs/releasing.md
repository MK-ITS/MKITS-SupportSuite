# Releasing

Versions follow [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`, with pre-releases as
`MAJOR.MINOR.PATCH-rc.N`. There is one version for the whole suite – `VersionPrefix` in
[`Directory.Build.props`](../Directory.Build.props). Builds from `main` carry the suffix `-dev`
(`0.1.0-dev+<commit>`); only a release tag builds without it.

## What a release tag does

Pushing a tag `vX.Y.Z` runs the *Publish server image* workflow ([`publish.yml`](../.github/workflows/publish.yml)):

1. It checks that the tag matches `VersionPrefix` and that the agent update release key is set up – otherwise it fails
   before building anything.
2. It builds the server image with the version `X.Y.Z` and the bundled clients, signs the agent updates (ADR-0023), and
   pushes `ghcr.io/mk-its/mkits-supportsuite-server` with the tags `X.Y.Z`, `X.Y` and `latest` (pre-releases only
   `X.Y.Z-rc.N`). The image gets a build provenance attestation.
3. It copies the clients out of that image – byte for byte what the server hands out – and creates a GitHub release:

   | File | What it is |
   |---|---|
   | `MKITS-Support-X.Y.Z-windows-x64.exe` | Windows client and agent |
   | `MKITS-Support-X.Y.Z-x86_64.AppImage`, `…-aarch64.AppImage` | Linux ad-hoc client |
   | `mkits-agent_X.Y.Z_amd64.deb`, `…_arm64.deb` | Installed Linux agent |
   | `SHA256SUMS` | SHA-256 of all files |

   Every file has a build provenance attestation (Sigstore, stored by GitHub), and the release notes are the
   section `## [X.Y.Z]` of [`CHANGELOG.md`](../CHANGELOG.md).

Nobody needs to download the clients from the release to run the server – the image already contains them. The release
is for checking (hashes, attestations) and for distributing clients outside the server (e.g. Intune, ADR-0024).

## One-time setup (owner)

1. **Release key for agent updates** – as described in [development.md](development.md#agent-update-release-key-adr-0023):
   secret `AGENT_UPDATE_SIGNING_KEY` and the public key in `deploy/keys/agent-update.pub` (committed via a PR). Without
   both, release tags fail on purpose: agents of that release could never be updated automatically.
2. **Tag protection** (recommended): *Settings → Rules → Rulesets → New tag ruleset*, target `v*`, restrict creation,
   update and deletion to maintainers. Whoever can push a `v*` tag can publish a release with signed updates.
3. **Package visibility**: after the first publish, set the package `mkits-supportsuite-server` to *public*
   (*Packages → Package settings*) so that servers can pull it without logging in.
4. Optional: **Authenticode certificate** for the Windows EXE (ADR-0009). Until then SmartScreen warns; signed builds can
   be placed in `/data/agents/windows-x64/` on the server (see the installation guide).

## Making a release

1. Open a PR from a branch `release/vX.Y.Z` that
   - sets `VersionPrefix` in `Directory.Build.props` to `X.Y.Z` (if it isn't already),
   - dates the version's section in `CHANGELOG.md` as `## [X.Y.Z] - YYYY-MM-DD` (renaming `## [Unreleased]`, or
     replacing `unreleased` in a prepared `## [X.Y.Z] - unreleased`), keeps an empty `## [Unreleased]` above it and
     updates the links at the bottom.
2. Merge it after CI is green.
3. Tag the merge commit on `main` and push the tag:

   ```bash
   git switch main && git pull
   git tag -a vX.Y.Z -m "MKITS-SupportSuite X.Y.Z"
   git push origin vX.Y.Z
   ```

   Push the tag from the command line rather than drafting a release in the GitHub UI: the workflow creates the
   release itself.
4. Watch *Actions → Publish server image*. When it is green, the release is on the *Releases* page.
5. Bump `VersionPrefix` to the next planned version in a follow-up PR (e.g. `0.2.0`), so that builds from `main` are
   `0.2.0-dev` and sort after the release.

**Pre-release:** tag `vX.Y.Z-rc.N` on `main`; the changelog needs a section `## [X.Y.Z] - unreleased` (no date yet),
which becomes the release notes; the release is marked as pre-release, and `latest` is not moved.

**If the workflow fails**, look at the failed job (see [development.md](development.md#diagnosing-ci-failures-without-log-access)).
A temporary problem (registry, download) is solved by re-running the failed jobs. If the code or the changelog has to
change: as long as nothing was published, delete the tag (`git push origin :vX.Y.Z`), fix the cause
on `main` and tag again; once the image or the release is out, never move the tag – release the fix as the next patch version.

## Checking a release

```bash
sha256sum -c SHA256SUMS
gh attestation verify MKITS-Support-X.Y.Z-windows-x64.exe --repo MK-ITS/MKITS-SupportSuite
gh attestation verify oci://ghcr.io/mk-its/mkits-supportsuite-server:X.Y.Z --repo MK-ITS/MKITS-SupportSuite
```

The attestation proves that the file was built by this repository's workflow from the tagged commit. Agents check
updates independently of that with the release key (ADR-0023).
