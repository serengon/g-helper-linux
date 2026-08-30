# Reproducible build baseline

This branch starts from the upstream `v1.0.90` source tree. It does not use a
floating SDK or a floating dependency graph.

## Upstream provenance

- Repository: <https://github.com/utajum/g-helper-linux>
- Lightweight tag: `v1.0.90`
- Commit: `9e99e21153a7cf75dd1682425bf1bd3d1b1de43a`
- Git tree: `339975db848f2a0d08b27b49a45ed6606540c3da`
- Commit date: `2026-08-02T13:11:08+02:00`
- Canonical archive name: `g-helper-linux-1.0.90-upstream.tar.gz`
- Canonical archive size: `2974052` bytes
- Canonical archive SHA-256:
  `0ba1f34797978af2ff245312d36a687eb5d8316b692ca93b64e7cd0f5aeab88d`

The tag is lightweight. The commit contains a GitHub signature made with key
`B5690EEEBB952194`; the key is not present in the local trust store, so local
signature verification is intentionally recorded as unverified rather than
trusted.

Regenerate and verify the canonical archive with:

```bash
./scripts/archive-upstream.sh
```

The script archives the immutable upstream commit, not the modified working
tree, and uses `gzip -n` so the gzip header has no filename or timestamp. It
writes to a same-directory temporary file, verifies SHA-256, then atomically
renames the verified archive into place.

## Pinned toolchain and dependencies

- .NET SDK: `10.0.400`; `global.json` disables SDK roll-forward.
- Build container base:
  `mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c`
- NuGet dependency graphs: the `packages.lock.json` beside each project.
- Restore mode: locked. A mismatch between a project and its lock file fails.
- Native Skia/HarfBuzz assets: `build.sh` reads the single resolved version
  directly from `src/packages.lock.json`; there is no duplicate version list.
- UPX: intentionally omitted because an optional host tool must not silently
  change the release artifact.

To deliberately update dependencies, edit the relevant `PackageReference`, run
one restore with `-p:RestoreLockedMode=false`, and review the complete lock-file
diff before restoring or building normally again.

Microsoft references used for these controls:

- [`global.json` SDK selection](https://learn.microsoft.com/dotnet/core/tools/global-json)
- [NuGet lock files and locked mode](https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#locking-dependencies)
- [Official .NET container images](https://learn.microsoft.com/dotnet/core/docker/container-images)

No documentation or API implementation was copied from these pages; they are
design references only.

## Build without installing .NET 10 on the host

Docker is the default engine; set `CONTAINER_ENGINE=podman` to use Podman.
The first invocation builds the toolchain image and populates the local NuGet
cache, so it requires network access. Subsequent builds can enforce no network:

```bash
./build.sh
GHELPER_OFFLINE=1 ./build.sh
```

For a quicker non-AOT check:

```bash
./build.sh --no-aot
```

An isolated private snapshot is mounted at `/work` and the container runs with
the invoking user's UID/GID. The snapshot contains the exact committed tree,
tracked binary diff, and sorted untracked source set. Provenance is computed
inside it before build and recomputed afterward; a mismatch fails closed.
Compilation cannot read later edits from the shared checkout. `build.sh` is the
public wrapper; its internal compile mode requires a wrapper-created context
mounted read-only at a fixed path and cannot be enabled with an environment
flag.

The canonical package cache lives outside the checkout under the absolute user
cache root. Its key covers the exact three `packages.lock.json` files, their
three project files, `Directory.Build.props`, `global.json`, and the explicit
`NuGet.Config`. Before an
artifact build, only package-id/version directories in that exact locked
closure are copied into a new private snapshot; unrelated cache contamination
is ignored. Symlinks, FIFOs, devices, sockets, and all other special nodes are
rejected before source-cache copy, private-cache use, and manifesting. Every
regular file is recorded in a deterministic SHA-256 manifest, the private tree
is mounted read-only, and its manifest is recomputed after compilation.
The persistent cache root and each project-owned component must be canonical,
non-symlink, user-owned, and mode `0700`. Both `nuget` and `dotnet` are checked
before and after restore. `cache.lock` is opened relative to the already-opened
cache directory with `O_NOFOLLOW`, must be a single-link regular user-owned
file with mode `0600`, and remains locked across the build wrapper exec.

The artifact embeds the complete package manifest, immutable image ID, image
input hash, cache-input hash, and their environment digest. That digest is
appended as `.env.<sha256>` to `AssemblyInformationalVersion`.
`GHELPER_BUILD_IMAGE` is refused. A plain direct `dotnet publish -c Release`
fails with an explicit `UNATTESTED` diagnostic, but its MSBuild property gate
and the wrapper's read-only nonce handshake prevent accidental misuse only;
they are an accidental-misuse guard, not authentication, and a malicious local
caller can forge them.
The build does not download `latest` release files or content from `master`.
The image tag is derived from a path-independent digest of the contents of
`Containerfile.build`, `.dockerignore`, `global.json`, and
`Directory.Build.props`; its matching label is checked, then
`docker run` receives the immutable `sha256:...` image ID rather than the
mutable tag. Offline mode exits before any image build if that keyed image is
absent.

Build publication accepts only a new absolute output directory under an
owned, non-writable parent, or a directory previously created by this wrapper.
Replacement requires matching protected registry and directory markers and is
performed with same-parent renames; arbitrary existing directories, symlinks,
broad paths, and repository/home ancestors are refused before compilation.

Default builds require a clean tree and stamp the actual fork commit. During
pre-commit review only, `GHELPER_DIRTY_REVIEW=1` permits a dirty tree and stamps
`dirty.<SHA-256>.base.<HEAD>` into the artifact; the digest covers the tracked
binary diff plus sorted untracked source paths, modes, and contents. This mode
does not claim commit provenance and must not be released.

The container image digest pins the .NET SDK filesystem. Its `apt` packages
still come from the Ubuntu repositories when the image is first built, so this
is a controlled build environment, not yet a bit-for-bit reproducible supply
chain. A future packaging phase should pin an immutable Ubuntu snapshot or use
Fedora `mock` with a recorded repository snapshot.

## Hardened runtime and publication boundary

- Version: `1.0.90-x13.1`.
- The Phase 1 binary is technically non-deployable. Every invocation except
  the exact `--print-build-metadata` query exits 64 before Avalonia, AppConfig,
  autostart, native extraction, or hardware/runtime code initializes. Extra or
  misplaced metadata arguments are also refused.
- Exact clean or dirty-review provenance is visible only through
  `./dist/ghelper --print-build-metadata`; the GUI is intentionally blocked.
  Successful output begins `LOCAL-REPRODUCIBLE-UNSIGNED`. The command returns
  70 unless the exact version grammar, local assembly metadata, embedded image
  identity, environment digest, and complete NuGet manifest are internally
  coherent. This self-report is not a trust decision.
- The wrapper emits external `.ghelper-build-manifest-v1`, bound to the binary
  and complete artifact-tree SHA-256 plus source, image, cache closure, and
  environment. `scripts/verify-artifact.sh` independently recomputes those
  inputs and rebuilds; only byte-identical output passes. Because the manifest
  is unsigned, reproduction is the Phase 1 trust mechanism. Signed attestation
  is deliberately deferred to the reviewed RPM phase.
- Artifact verification treats candidate binaries strictly as data: it checks
  tree/manifest hashes, rebuilds independently, and compares exact executable
  bytes and canonical manifest fields. It never executes a rejected candidate;
  metadata is queried only from the independently rebuilt binary after byte
  identity is established.
- User configuration, cache, autostart, KWin, data, and native-cache paths use
  one resolver. Only absolute `XDG_*` roots or an absolute HOME/user profile
  are accepted; if none exists, filesystem access fails closed without a
  relative or shared `/tmp` fallback.
- Runtime self-update and self-replacement are removed.
- The upstream NixOS flake, module, and package definitions are removed because
  they installed unaudited permissions and privilege grants.
- Runtime system-file installation, repair, removal, and generic embedded
  resource extraction are fail-closed. Installer payloads and `gpu-helper` are
  not embedded; the hardened package path remains under development.
- The C# hardware controller has no environment-controlled test-root bypass.
  Its sandbox hook exists only in the separately compiled scenario-test
  assembly and is omitted from the Phase 1 application.
- Every upstream `install/` input is quarantined and non-executable. None is
  embedded or callable from the Phase 1 application/build path. The legacy
  `90-ghelper.rules` file is never accepted as trusted package policy.
- AppImage and all GitHub release/publication workflows are removed until the
  signed RPM phase. A PR-only workflow runs the exact acceptance verifier and
  cannot publish artifacts.
- `scripts/update-ryzenadj.sh` is removed. RyzenAdj is not embedded, discovered,
  or executed; its dependent UI/backend feature is fail-closed. The remaining
  upstream file is a non-executable quarantined input, not a trusted binary.
- `vendor/wlr-randr/update.sh` is removed; vendored sources are never refreshed
  implicitly by the build.
- Changelog images are never downloaded by the renderer; remote image targets
  are represented only by inert text/link placeholders.
- Native libraries and embedded tools are accepted only when the cache content
  matches the exact embedded SHA-256. Mismatches are written to a same-directory
  temporary file, flushed to stable storage, verified, and atomically renamed;
  any failure returns unavailable without a stale/PATH/system fallback.

## Trimmer-root validation

Upstream `TrimmerRoots.xml` named
`Tmds.DBus.SourceGenerator.OrgKdeStatusNotifierItemHandler`, but that type does
not exist in locked `Avalonia.FreeDesktop` 12.1.1. Metadata inspection shows the
current generated contract is
`Avalonia.FreeDesktop.DBus.IStatusNotifierItemHandler`; the actual rooted
`Avalonia.FreeDesktop.StatusNotifierItemDbusObj` remains present. The stale
entry was removed. The acceptance build must have no `IL2008` warning, and the
following metadata assertions are part of `scripts/verify-phase1.sh`:

```bash
strings "${XDG_CACHE_HOME:-$HOME/.cache}/ghelper-x13-build/<build-input-sha256>/nuget/avalonia.freedesktop/12.1.1/lib/net10.0/Avalonia.FreeDesktop.dll" \
  | grep -F Avalonia.FreeDesktop.DBus.IStatusNotifierItemHandler
! grep -F Tmds.DBus.SourceGenerator.OrgKdeStatusNotifierItemHandler src/TrimmerRoots.xml
```
