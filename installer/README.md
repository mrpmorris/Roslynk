# Roslynk distribution

Roslynk ships as a Windows service. Two install paths are provided.

## 1. Build the payload

```powershell
pwsh installer/publish.ps1
```

This produces a self-contained `win-x64` build in `installer/publish/` (no .NET runtime required on
the target machine).

## 2a. MSI (WiX) — the distributable installer

Requires the WiX toolset:

```powershell
dotnet tool install --global wix
dotnet build installer/Morris.Roslynk.Installer.wixproj -c Release
```

The MSI installs the published files into `C:\Program Files\Roslynk` and registers + starts the
`Roslynk` Windows service (loopback only, auto-start). `MajorUpgrade` handles version upgrades; the
`UpgradeCode` in `Roslynk.wxs` is the stable product identity and must not change between releases.

> Status: the `.wxs`/`.wixproj` are authored but have **not** been built/validated in this
> environment (the WiX toolset is not installed here). Build once on a machine with WiX before relying
> on the MSI.

## 2b. No-MSI — register the service directly

For local installs without building an MSI, run from an **elevated** prompt:

```powershell
pwsh installer/install-service.ps1     # register + start
pwsh installer/uninstall-service.ps1   # stop + remove
```

## 3. WinGet

Manifests live in `installer/winget/` (schema 1.6.0, three-file form). Before submitting to
`microsoft/winget-pkgs`, fill in the released MSI's `InstallerUrl`, `InstallerSha256`, and
`ProductCode` in `Morris.Roslynk.installer.yaml`, then validate:

```powershell
winget validate --manifest installer/winget
```

Install once published:

```powershell
winget install Morris.Roslynk
```

> Status: the manifests are templates — the installer URL, SHA256, and product code require a real
> GitHub release of the MSI to be filled in.
