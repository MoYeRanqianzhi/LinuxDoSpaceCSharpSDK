# Development Guide

## Workdir

```bash
cd sdk/csharp
```

## Validate

CI-aligned validation:

```bash
dotnet build -c Release
```

Release-aligned package validation:

```bash
dotnet pack -c Release -o release-artifacts
```

## Release model

- Workflow file: `../../../.github/workflows/release.yml`
- Trigger: push tag `v*`
- Current release output is a GitHub Release `.nupkg` asset
- There is no public NuGet feed publication in the current workflow

## Keep aligned

- `../../../Sdk.cs`
- `../../../LinuxDoSpaceSdk.csproj`
- `../../../README.md`
- `../../../.github/workflows/ci.yml`
- `../../../.github/workflows/release.yml`

