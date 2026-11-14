# GitHub Actions

## Workflow design

ThinkBook Toolkit uses Windows Server 2022 runners and the .NET 9 SDK to match
its current `net9.0-windows` projects. Both workflows check out recursive Git
submodules at their recorded commits. Third-party actions are pinned to commit
SHAs; update those pins deliberately when upgrading actions.

| Workflow | Trigger | Result |
| --- | --- | --- |
| `ci.yml` — Build and test | Branch pushes, pull requests, manual runs, release validation | Release solution build, separate AMD Helper build, five executable test suites, 14-day test logs |
| `release.yml` — Package release draft | Push a `v*` tag or manually select an existing tag | Validate the tagged source, sign and package it, upload 30-day artifacts, create a draft GitHub Release |

CI has only `contents: read` permission and needs no signing secrets. Fork PRs
can run the same checks. These tests are executable WPF programs, so `dotnet test`
alone would not execute them. `scripts/test_ci.ps1` runs UI smoke, plugin,
NVPCF process isolation, GPU monitor recovery, and recovery/startup suites. A
nonzero exit code fails CI. Test logs are uploaded even after a test failure.

The AMD Power Helper is built explicitly because it is outside the main solution.
Hosted CI cannot validate actual Lenovo firmware, EC access, GPU transitions,
fan fail-safe behavior, or UIAccess installation on supported hardware. Keep
those device checks in the manual release checklist.

## Signing setup (one time)

Create the GitHub environment `release-signing` under repository Settings →
Environments and add these environment secrets:

| Secret | Value |
| --- | --- |
| `TBT_CERT_PFX_BASE64` | Base64 of the existing release-signing PFX, including its private key |
| `TBT_CERT_PASSWORD` | Password for that PFX |

Use the existing signing identity; do not generate a different certificate on
every CI run. The workflow derives the public CER from the PFX, checks that it
has a private key and has not expired, and passes the temporary paths to the
existing build script. Temporary signing files are deleted in an `always()` step
and are excluded from artifact paths. Configure tag restrictions or required
reviewers on this environment according to who may use the release key.

Missing secrets intentionally fail release packaging with a setup message;
they do not affect ordinary CI. Secret values must never be committed to Git or
included in issue/PR text.

## Creating a release

1. Update `Directory.Build.props` and the changelog, commit, and pass CI.
2. Create and push the matching `vMAJOR.MINOR.PATCH` tag, for example `v1.1.2`.
   Alternatively, select **Package release draft → Run workflow** and enter an
   existing tag. The workflow builds the exact tag, not the current branch tip.
3. The release workflow runs build/tests on that tag before accessing signing
   secrets. Packaging rejects a tag that differs from `VersionPrefix`.
4. It calls `scripts/build.ps1 -Configuration Release -Publish -Installer` and
   creates these assets:
   - `ThinkBookToolkit-<version>-Setup.exe`
   - `ThinkBookToolkit-<version>-win-x64-framework-dependent.zip`
   - `SHA256SUMS-v<version>.txt`
5. Review the draft, release notes, checksums, and installation/device behavior;
   publish the draft when ready.

The workflow does not overwrite an existing release. If draft creation fails
because that tag already has a release, built artifacts remain available in the
Actions run. Inspect the existing release and manage its assets deliberately.
There is no automatic release deletion or tag cleanup.

Packaging reuses the existing build script's proprietary-dependency exclusion,
helper/worker staging, signing, Inno Setup installer, and SHA-256 generation.
The workflow uses the runner's Inno Setup 6 installation, with a pinned 6.4.3
installation fallback. It verifies asset presence and checksum contents before
uploading. It does not install the resulting package on the runner.

The existing dependency downloader resolves the latest PawnIO.Modules release
and downloads InpOut from a branch URL. Thus source and action versions are
pinned, but the complete release is not yet byte-for-byte reproducible. Pinning
and verifying those downloads is a separate build-script improvement.

## Local checks

Run from the repository root on Windows with the .NET 9 SDK and initialized
submodules:

```powershell
dotnet build ThinkBookToolkit.sln -c Release -m:1 --disable-build-servers
dotnet build src/ThinkBookToolkit.AmdPowerHelper/ThinkBookToolkit.AmdPowerHelper.csproj -c Release -m:1 --disable-build-servers
./scripts/test_ci.ps1
```

Tests need normal Windows registry-read and child-process/IPC access. Sandboxes
that block those operations may fail even when the source builds successfully.
No signing certificate is required for these checks.

## LLT comparison

Reviewed the community repository `LenovoLegionToolkit-Team/LenovoLegionToolkit`
on 2026-11-14, using its `dev` branch workflow sources:

- [build.yml](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/dev/.github/workflows/build.yml):
  Windows 2022, .NET 9, restore/build an installer and upload it; master pushes,
  master/dev PRs, and manual runs. Its current ordinary build requires an MSIX
  signing secret, which is unavailable to normal fork PRs.
- [dev-release.yml](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/dev/.github/workflows/dev-release.yml):
  dev pushes/manual runs calculate a version from releases and commit counts,
  build an installer, prune old Dev releases, and publish a Dev release. The
  reviewed file sets `prerelease: false` and `make_latest: false` for Dev.
- [release.yml](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/dev/.github/workflows/release.yml):
  manual Stable/Beta/Dev selection creates a draft; a published-release event
  builds and attaches the installer to that release.
- [stale.yml](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/dev/.github/workflows/stale.yml):
  daily inactive issue/PR handling. The review workflow is currently disabled.

For ThinkBook Toolkit's current single-main-branch development, separate CI and
tagged draft packaging provide the useful parts without introducing automatic
version bumps, deleting historical releases, or closing user reports on a timer.
An eventual development channel can be added with unique prerelease versions
and explicit retention rules when the application updater supports that channel.
