# Code signing policy

**Status: applying for [SignPath Foundation] code signing — not yet active.** Until an
application is approved, release builds are unsigned (see the README's Installing section).
This document describes the policy that will apply once signing is enabled, and exists ahead of
that so it's reviewable as part of the application itself.

Free code signing provided by [SignPath.io], certificate by [SignPath Foundation].

## Privacy

See [PRIVACY.md](PRIVACY.md).

## What gets signed

Windows installer/updater binaries (`.exe`) published to
[GitHub Releases](https://github.com/misonyah/VrcPhotoManager/releases) - the `Setup.exe`
installer and the app binary it installs.

## Build and signing process

- All release artifacts are built by [GitHub Actions](.github/workflows/release.yml), triggered
  only by pushing a `v*.*.*` tag to this repository - never from a local machine or a fork.
- Only artifacts built by that CI workflow, from this repository's own source, are ever
  submitted for signing. No third-party or upstream binaries are signed under this project's
  certificate.
- The signing key itself is never available to the project - it's held by SignPath Foundation
  (HSM-backed) and invoked only through their CI integration.
- Every release requires manual approval before signing, per SignPath Foundation's own policy.

## Download / auto-update mechanism (downloader-installer category)

VRC Photo Manager auto-updates: on startup, it checks GitHub's Releases API for a newer signed
version and downloads it in the background via [Velopack](https://velopack.io); the update is
applied the next time you restart the app (see [CHANGELOG.md](CHANGELOG.md)'s 1.0.1 entry). No
user confirmation prompt appears for this download - the trust boundary is that update artifacts
only ever come from this project's own GitHub Releases (Velopack's `GithubSource`, pinned to
`github.com/misonyah/VrcPhotoManager` in `App.xaml.cs`), the same repository and CI pipeline
documented above. There is no plugin, script-downloading, or arbitrary-code-execution mechanism
beyond this self-update path.

## Team roles

This is currently a solo-maintained project - one person (the repository owner) acts as Author,
Reviewer, and Approver for all changes and signing requests. This will be updated here if that
changes.

[SignPath.io]: https://signpath.io
[SignPath Foundation]: https://signpath.org
