# Roslynk distribution

Roslynk ships as a Windows service. Two install paths are provided.

## 1. Build the payload

```powershell
pwsh installer/publish.ps1
```

This produces a self-contained `win-x64` build in `installer/publish/` (no .NET runtime required on
the target machine).

## 2a. MSI (WiX) — the distributable installer

Requires the **free WiX v5** toolset (v6+/v7 need the paid OSMF licence — pin v5):

```powershell
dotnet tool install --global wix --version 5.0.2
wix build installer/Roslynk.wxs -d PublishDir=installer/publish -arch x64 -o Roslynk-1.0.0-x64.msi
```

The MSI installs the published files into `C:\Program Files\Roslynk` and registers + starts the
`Roslynk` Windows service (loopback only, auto-start). `MajorUpgrade` handles version upgrades; the
`UpgradeCode` in `Roslynk.wxs` is the stable product identity and must not change between releases.
(`Morris.Roslynk.Installer.wixproj` is also provided for an MSBuild-SDK build, but its `WixToolset.Sdk`
version must match your installed WiX; the `wix build` command above is the verified path.)

> Status: **verified** — this builds a ~51 MB self-contained MSI with WiX v5.0.2. WiX auto-generates a
> fresh `ProductCode` per build, so capture it (and the MSI's SHA256) from the actual release build for
> the WinGet manifest.

## 2b. No-MSI — register the service directly

For local installs without building an MSI, run from an **elevated** prompt:

```powershell
pwsh installer/install-service.ps1     # register + start
pwsh installer/uninstall-service.ps1   # stop + remove
```

## 3. WinGet

Manifests live in `installer/winget/` (schema 1.6.0, three-file form) and **pass `winget validate`**.
The `InstallerSha256` and `ProductCode` currently reflect a local build; before submitting to
`microsoft/winget-pkgs`, refresh them (and `InstallerUrl`) against the published release MSI:

```powershell
winget validate --manifest installer/winget
```

Install once published:

```powershell
winget install Morris.Roslynk
```

> Status: **schema-valid**. Only the release-specific values (download URL, and the SHA256/ProductCode
> of the actually-released MSI) need refreshing at publish time.
