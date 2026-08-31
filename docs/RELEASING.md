# Release checklist

This project publishes Windows builds from annotated semantic-version tags. Keep the
working tree clean except for the intended release changes.

## Before tagging

1. Review `git diff` and `git diff --cached` for screenshots, session logs, database files,
   API keys, personal templates, internal URLs and absolute local paths.
2. Confirm `Templates/private/` is ignored and only generic templates are tracked.
3. Update `Directory.Build.props` (`VersionPrefix`) and add a matching section to
   `CHANGELOG.md`.
4. Run the complete local checks:

   ```powershell
   dotnet restore Timenotifier.Tests\Timenotifier.Tests.csproj
   dotnet build Timenotifier.csproj --configuration Release --no-restore
   dotnet test Timenotifier.Tests\Timenotifier.Tests.csproj --configuration Release --no-restore
   python -m pytest AgentRuntime\tests -q
   ```

5. Build the release output into the repository `publish` directory and verify that it
   contains only runtime files. Do not copy screenshots, settings or evidence into it.

## Tag and publish

```powershell
git tag -a v0.1.1 -m "Release v0.1.1"
git push origin main
git push origin v0.1.1
```

The `release.yml` workflow runs on `v*.*.*` tags. It restores, tests and publishes a
framework-dependent Windows x64 application, then attaches a ZIP archive to the GitHub
Release. A failed workflow must be fixed and rerun from the same tag only after confirming
the generated files are correct.

## Rollback

Do not rewrite a published tag. If a release is defective, publish the next patch version,
mark the broken release as superseded, and document the correction in `CHANGELOG.md`.
