# GitHub Actions

## Workflow design

ThinkBook Toolkit uses Windows Server 2022 runners and the .NET 9 SDK to match
its current `net9.0-windows` projects. Both workflows check out recursive Git
submodules at their recorded commits. Third-party actions are pinned to commit
SHAs; update those pins deliberately when upgrading actions.

| Workflow | Trigger | Result |
| --- | --- | --- |
| `ci.yml` — Build and test | Branch pushes, pull requests, manual runs, release validation | Release solution build, separate AMD Helper build, five executable test suites, 14-day test logs; branch pushes/manual runs also upload a signed test installer |
| `release.yml` — Publish release | Manually select an existing `v*` tag and notes source | Validate the tagged source, sign and package it, upload 30-day artifacts, publish a GitHub Release |

CI has only `contents: read` permission. The build/test job needs no signing
secrets; fork and same-repository PRs run checks only. Branch pushes and direct
manual CI runs additionally use the fixed signing secrets to create an installer.
Release validation calls this workflow without producing a test installer. These tests are executable WPF programs, so `dotnet test`
alone would not execute them. `scripts/test_ci.ps1` runs UI smoke, plugin,
NVPCF process isolation, GPU monitor recovery, and recovery/startup suites. A
nonzero exit code fails CI. Test logs are uploaded even after a test failure.

The AMD Power Helper is built explicitly because it is outside the main solution.
Hosted CI cannot validate actual Lenovo firmware, EC access, GPU transitions,
fan fail-safe behavior, or UIAccess installation on supported hardware. Keep
those device checks in the manual release checklist.

## Daily test installers

After each successful branch push, download the installer from **Actions →
Build and test → the run → Artifacts → ThinkBookToolkit-test.…-installer**.
Artifacts are retained for 30 days. The artifact contains only the Setup EXE;
GitHub wraps artifact downloads in ZIP containers, but this is not a portable
application distribution. The separate test-log artifact remains available.

The installer filename and setup display version include the source version,
run number, attempt, and short commit SHA, for example:
`ThinkBookToolkit-1.1.1-test.42.1.g12345678-Setup.exe`.
The application numeric version remains `1.1.1`, and the installer upgrades the
same installation as a stable build; it is not a side-by-side installation.

The command is `./scripts/build.ps1 -Configuration Release -InstallerOnly
-BuildLabel test.42.1.g12345678`. It creates neither a portable ZIP nor a
SHA256SUMS file. Published staging files are temporary build inputs, not uploaded
assets. Daily builds never create a tag or GitHub Release. PR events never run
signed packaging, including PRs from branches in this repository.

## Signing setup (one time)

Create the GitHub environment `release-signing` under repository Settings →
Environments and add these environment secrets (shared by daily installers and releases):

| Secret | Value |
| --- | --- |
| `TBT_CERT_PFX_BASE64` | Base64 of the existing release-signing PFX, including its private key |
| `TBT_CERT_PASSWORD` | Password for that PFX |

You can also configure them from the existing local PFX with:

```powershell
./scripts/configure_github_signing.ps1
```

The script prompts for the PFX password without echoing it, validates the PFX,
then sends its base64 contents and password directly to GitHub CLI over stdin.
It defaults to `lhzlhz419/ThinkBookToolkit` and the local
`signing/ThinkBookToolkit.pfx`. Use `-Repository` and `-CertificatePath` to change
those explicitly. Do not paste the password into chat, commit it, or put it on a
command line. The GitHub account needs environment and secrets management access.

Use the existing signing identity; do not generate a different certificate on
every CI run. The workflow derives the public CER from the PFX, checks that it
has a private key and has not expired, and passes the temporary paths to the
existing build script. Temporary signing files are deleted in an `always()` step
and are excluded from artifact paths. Allow the branches used for daily builds as well as release tags in this
environment. Required reviewers would make each signed build wait for approval;
leave that off if daily installers should run unattended. Only grant repository
write access to contributors trusted to use the signing workflow.

Missing secrets fail signed packaging with a setup message; the independent
build/test job still runs. After setting secrets, rerun failed jobs. Secret values must never be committed to Git or
included in issue/PR text.

## Creating a release

1. Update `Directory.Build.props` and the changelog, commit, and pass CI.
2. Create and push the matching `vMAJOR.MINOR.PATCH` tag, for example `v1.1.2`.
   Pushing a tag does not publish a release. Select **Publish release → Run
   workflow**, enter that tag, and choose `automatic` or `custom` release notes.
   The workflow builds the exact tag, not the current branch tip.
3. The release workflow runs build/tests on that tag before accessing signing
   secrets. Packaging rejects a tag that differs from `VersionPrefix`.
4. It calls `scripts/build.ps1 -Configuration Release -Publish -Installer` and
   creates these assets:
   - `ThinkBookToolkit-<version>-Setup.exe`
   - `ThinkBookToolkit-<version>-win-x64-framework-dependent.zip`
   - `SHA256SUMS-v<version>.txt`
5. After successful validation and packaging, the workflow publishes the GitHub
   Release with the selected notes and all three assets. Perform device checks
   before starting **Publish release**; starting this workflow authorizes publication.

### Release notes: automatic or uploaded Markdown

- `automatic` asks GitHub to generate notes from the changes between releases.
  It is not a fixed prose template and does not read a notes file from the repo.
- `custom` uses the supplied Markdown exactly. You can enter it in the manual
  workflow's `notes` field or upload a local file through GitHub CLI:

```powershell
gh workflow run release.yml --repo lhzlhz419/ThinkBookToolkit --ref main -f tag=v1.1.2 -f notes_source=custom -F "notes=@C:/path/to/my-release.md"
```

`-F` reads the file into the workflow input. Keep it under 60 KB to leave room
for the other dispatch inputs. The runner saves it temporarily outside the
checkout and passes it to `gh release create --notes-file`; it is not committed
to Git. For automatic notes, use `-f notes_source=automatic` and omit `notes`.
These commands are available once the workflow has been merged to `main`.

Version-specific `release-notes-v*.md` files have been removed from the repository.
`CHANGELOG.md` remains the project's cumulative change history. Existing GitHub
Release descriptions are not deleted or changed by removing those source files.

The workflow does not overwrite an existing release. If publishing fails
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
tagged release packaging provide the useful parts without introducing automatic
version bumps, deleting historical releases, or closing user reports on a timer.
An eventual development channel can be added with unique prerelease versions
and explicit retention rules when the application updater supports that channel.
