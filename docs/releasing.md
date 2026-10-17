# Releasing

A release is made by pushing a tag. Everything else is done by the workflows in `.github/workflows`.

## What a tag does

Push a tag of the form `v1.2.3` (or `v1.2.3-rc.1` for a pre-release), and `release.yml`:

1. runs the same checks as `ci.yml`: the whole test suite on a Linux runner with SANE, Tesseract and a font installed, the web image built and started, and the library package built and checked;
2. publishes the **terminal program** for Linux, x64 and arm64, as a single self-contained file in a `tar.gz` each;
3. publishes the **web image** for amd64 and arm64 to `ghcr.io/godefroi/sane-tui-web`, tagged `1.2.3`, `1.2`, `1` and `latest` (v0.1.0 got `0.1.0`, `0.1`, `0` and `latest`). A pre-release gets only its own tag, as the release candidates did;
4. packs the **SANE library** as the NuGet package `SaneTui.Sane`;
5. creates the **GitHub release** with generated notes, the program archives, the package file and a `SHA256SUMS` file, marked as a pre-release when the tag has a suffix;
6. last of all, **publishes the package to nuget.org**.

All of them get the version from the tag, so the program, the image and the library share one version number. A tag that isn't of the right form stops the workflow at the start.

The package is the one thing that can't be taken back: nuget.org only lets a version be unlisted or marked deprecated, never deleted. That is why it's the last job, why the package is checked before then (`.github/scripts/check-package.sh` verifies the id, version, license, README, the commit, the absence of dependencies and the exact list of files), and why the same check runs on every push and pull request. A pre-release tag publishes a pre-release package. Re-running the publishing job is safe: a version that is already on nuget.org is skipped.

## One-time setup

- **nuget.org, trusted publishing.** There is no API key in the repository. nuget.org has a trusted publishing policy for the package with the repository owner `godefroi`, the repository `sane-tui`, and the workflow file `release.yml` (no environment). During the release, the job asks GitHub for a token, `NuGet/login` exchanges it for a key that lasts about an hour, and the key is used to push.
- **The repository variable `NUGET_USER`**, set to the nuget.org user name that owns the policy (Settings, Secrets and variables, Actions, Variables). It's the user name, not the e-mail address. The publishing job stops with a message if it's missing.
- **The container package** `sane-tui-web` may be private the first time it's pushed. Make it public in its package settings on GitHub for anyone to pull it (v0.0.1-rc.1 could be pulled without logging in).
- **Package ID.** The first push of `SaneTui.Sane` (v0.1.0) made the pushing account its owner, and trusted publishing worked for that first push of a new ID, with the policy created beforehand. The ID can't be renamed afterwards: a new ID is a new package, with the old one deprecated and unlisted.

## Trying a package before releasing

```sh
dotnet pack src/SaneTui.Sane -c Release -p:PackageVersion=0.0.1-local -p:ContinuousIntegrationBuild=true -o dist
bash .github/scripts/check-package.sh dist/SaneTui.Sane.0.0.1-local.nupkg SaneTui.Sane 0.0.1-local
```

To use it from another project without publishing, add `dist` as a local NuGet source.
