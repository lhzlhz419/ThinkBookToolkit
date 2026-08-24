# ThinkBook Toolkit

## Disclaimer

ThinkBook Toolkit is an independent, experimental project. It is not affiliated
with Lenovo, is not an official Lenovo project, and is not endorsed, supported,
or sponsored by Lenovo.

This software reads and writes hardware and firmware settings. Incorrect or
incompatible settings may affect cooling, performance, stability, hardware
lifespan, warranty coverage, or data safety. You use the software entirely at
your own risk and are solely responsible for every consequence. If you do not
understand or accept these risks, do not install or use it.

Before requesting after-sales service, uninstall ThinkBook Toolkit or remove
the drive on which it is installed to avoid unnecessary complications. On the
first launch of every software version, the in-app risk acknowledgement must be
typed manually before the application can continue.

**Every feature has been tested only on ThinkBook 16p G6 IAX with BIOS
R2CN57WW. No feature is guaranteed to work or be safe on another model or BIOS
version. Capability detection only controls what the interface displays; it is
not a compatibility or safety guarantee.**

[简体中文](README.zh-CN.md)

[Changelog](CHANGELOG.md)

## Overview

ThinkBook Toolkit is a native Windows control center for selected Lenovo
ThinkBook hardware. Its WPF interface keeps routine controls in one window and
loads device capabilities progressively so the overview becomes usable first.
It runs independently and does not require ThinkBook Fan Control.

Current feature groups include:

- performance mode, GPU working mode, live temperatures, power readings, and
  fan control;
- fixed fan targets, editable CPU/GPU curves, add/remove-point advanced curves
  with hysteresis thresholds, profiles, include/exclude-aware game detection,
  and full-speed control;
- battery charging modes, overnight charging, always-on USB, flip-to-start,
  and detailed battery information;
- fixed/dynamic display refresh rates, eye care, color modes, Dolby settings,
  speaker/microphone noise reduction, keyboard, function-key, OSD, and
  touchpad controls;
- device, firmware, storage, and Lenovo warranty information;
- power-limit viewing, supported-device adjustment, and independent locks for
  each available power parameter;
- independent scanning and installation of applicable drivers, firmware, and
  BIOS updates from Lenovo's public catalog, without the Lenovo System Update
  DLL;
- BIOS startup actions, boot-logo customization, and capability-gated I/O and
  virtualization controls;
- ordered automations combining device controls, application launches, keyboard
  macros, and delays, with power/game triggers, Fn-key discovery, and single/double-press
  bindings; any discovered WMI or driver key can be added to the custom list;
- recordable keyboard macros with editable keys, down/up states, event delays,
  ordinary-key bindings, and automation-step integration;
- light/dark themes, Chinese/English UI, tray controls, and Windows startup.

Unavailable controls are hidden from their pages. A complete capability summary
is available in Settings.

## Requirements and safety

- Windows 11 x64 and administrator privileges.
- The Lenovo drivers and services required by each device feature.
- [PawnIO](https://pawnio.eu/) must be installed and available for
  LibreHardwareMonitor to read CPU temperature on the tested device. PawnIO
  is a system-level component and is not bundled with Toolkit or the external
  dependency directory.
- Continuous temperature monitoring whenever custom fan control is active.
- A verified way to return fan control to firmware automatic mode before relying
  on a replacement fan backend.

Changing GPU mode or some firmware options may require a system restart. The app
restores firmware-automatic fan control before normal exit and before actions
that restart the computer.

## Replaceable fan backend

Toolkit loads `ThinkBookToolkit.FanBackend.dll` from the application directory.
This repository contains the WMI implementation. Replacing that one file changes
how fan telemetry and control are performed; Toolkit does not choose or reject a
backend based on the device model.

A replacement assembly must target a compatible .NET Windows runtime, reference
`ThinkBookToolkit.FanBackend.Contracts.dll`, expose one public non-abstract type
with a parameterless constructor, and implement `IFanBackend`. It must declare:

- fan-backend API version `1.1` through `ApiVersion`;
- `Name` and `Transport` for identification;
- an optional localized startup notice, or `null` when none is needed;
- whether fan control can be released before sleep and resumed afterward;
- minimum ordinary read and write intervals (one two-fan write is a single
  batch, while full-speed and restore-automatic operations are exempt);
- whether a target of `0` releases that fan to firmware control or stops it while
  manual control remains active;
- how automatic control is restored;
- how full-speed mode is enabled and disabled;
- fan telemetry/range reading, two-fan target writing, automatic restore, and
  full-speed operations.

Minimal declaration example:

```csharp
using System;
using System.Collections.Generic;
using ThinkBookToolkit.FanBackend;

public sealed class ExampleFanBackend : IFanBackend
{
    public Version ApiVersion => FanBackendContract.CurrentVersion;
    public string Name => "Example fan backend";
    public string Transport => "Vendor WMI";
    public FanBackendStartupNotice? StartupNotice => null;
    public bool SupportsDisableControlOnSleep => false;
    public TimeSpan MinimumReadInterval => TimeSpan.FromSeconds(0.5);
    public TimeSpan MinimumWriteInterval => TimeSpan.FromSeconds(6);

    public FanBackendControlSemantics ControlSemantics { get; } = new(
        FanTargetZeroBehavior.ReleaseFanToFirmwareControl,
        FanAutomaticControlRestoreMechanism.WriteZeroToBothTargets,
        "Write zero to both fan targets",
        new(
            FanFullSpeedControlMechanism.FeatureToggle,
            "Enable the vendor full-speed feature",
            "Disable the vendor full-speed feature"));

    public FanBackendSnapshot ReadSnapshot() =>
        throw new NotImplementedException("Add hardware-specific telemetry here.");

    public void Apply(int fan1Rpm, int fan2Rpm) =>
        throw new NotImplementedException("Write both targets as one batch here.");

    public void RestoreAuto() =>
        throw new NotImplementedException("Add the declared restore operation here.");

    public void SetFullSpeed(bool enabled) =>
        throw new NotImplementedException("Add full-speed enable and disable here.");
}
```

A backend that needs a startup notice can declare localized titles and content
with fallback text:

```csharp
private static readonly FanBackendStartupNoticeText EnglishNotice = new(
    "Fan backend notice",
    "Important information supplied by this backend.");

public FanBackendStartupNotice? StartupNotice { get; } = new(
    new Dictionary<string, FanBackendStartupNoticeText>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["zh-CN"] = new("风扇后端提示", "由此后端提供的重要信息。"),
        ["en-US"] = EnglishNotice
    },
    EnglishNotice);
```

Toolkit selects the text for the current UI language. The user can acknowledge
the notice once or suppress it for future launches. Suppression applies only to
the current DLL contents; replacing the backend resets the preference even when
the file name is unchanged.

The declarations must describe the implementation exactly. In particular,
Toolkit never assumes that `0 RPM` means automatic control or that full speed is
implemented by writing a maximum RPM value.

## Build and test

Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0), then
run from the repository root:

```powershell
.\scripts\build.ps1
```

Create the public, framework-dependent `0.2.4` release under
`dist\v0.2.4\ThinkBookToolkit-0.2.4-win-x64-framework-dependent`:

```powershell
.\scripts\build.ps1 -Configuration Release -Publish
```

Create the online installer (requires
[Inno Setup 6](https://jrsoftware.org/isdl.php)):

```powershell
.\scripts\build.ps1 -Configuration Release -Installer
```

The result is `dist\v0.2.4\ThinkBookToolkit-0.2.4-Setup.exe`. Its default destination
is `Program Files\ThinkBook Toolkit`, and the destination can be changed in the
wizard. If the selected destination is not empty, Setup warns that all of its
contents will be removed and proceeds only after explicit confirmation; always
select a folder dedicated to Toolkit. If a compatible fan-backend file version
already exists there, Setup offers to preserve it and selects that choice by
default. If an existing backend has an incompatible or unreadable file version,
Setup explicitly warns that the custom DLL will be replaced by the bundled
backend. The completion page launches Toolkit by default. The installer checks
for the 64-bit .NET 9 Desktop Runtime. If it is missing, the installer
downloads Microsoft's official .NET 9.0.18 Desktop
Runtime installer, verifies its pinned SHA-256, and installs it. Use
`-Publish -SelfContained` only when a larger portable build that bundles the
runtime is specifically required.

Public release publishing excludes local proprietary Lenovo DLLs by default.
For a private build used only on your own machine, add
`-IncludeLocalProprietaryDependencies`. The application version is `0.2.4`;
the replaceable fan-backend API is `1.1`, and the configuration-file format is
`1.0`.

Run the hardware-write-free UI smoke test:

```powershell
dotnet run --project .\tests\ThinkBookToolkit.UiSmokeTests\ThinkBookToolkit.UiSmokeTests.csproj -c Release
```

Run the non-admin UI preview:

```powershell
dotnet run --project .\tests\ThinkBookToolkit.UiPreview\ThinkBookToolkit.UiPreview.csproj -c Release
```

## Optional proprietary components

Some display, audio, and firmware functions load DLL components installed by
[Lenovo Vantage](https://apps.microsoft.com/detail/9wzdncrfj4mv) or
[Lenovo PC Manager](https://guanjia.lenovo.com.cn/). Those proprietary files are
not stored in this repository. Their respective Lenovo software, services, and
drivers may still be required at runtime.

The installer offers an optional custom Lenovo DLL directory, disabled by
default. When enabled, its value is saved in
`%USERPROFILE%\.thinkbook_toolkit\app_settings.csharp.json` as
`UseCustomLenovoDllDirectory` and `CustomLenovoDllDirectory`. Toolkit searches
an enabled, valid custom directory first, then uses its existing fallback
locations (the application directory, plus installed Vantage add-ins where
applicable). The custom root must contain
`VantageAddins` and/or `LenovoPcManager`; the installer references the directory
and does not copy those files into the installation.

For a local publish that bundles files obtained from your own installation, set
the MSBuild property `ExternalDependenciesRoot` to a directory containing
`VantageAddins` and/or `LenovoPcManager`. The default local path is the sibling
directory `ThinkBookToolkit.Dependencies`. A clean checkout without that
directory still builds; affected optional features will be unavailable unless
the required installed components can be found.

The current local dependency directory is organized as follows. Version folder
names identify the component versions used during development; they are not a
compatibility guarantee.

```text
ThinkBookToolkit.Dependencies/
|-- LibreHardwareMonitorLib.dll        # 0.9.7-pre717 local reference copy; NuGet is used by the build
|-- LenovoPcManager/
|   `-- WrapPlugin.dll
`-- VantageAddins/
    |-- LenovoProductivitySystemAddin/
    |   `-- 1.0.0.138/                 # BIOS utility, metadata, notices
    |-- LenovoSystemUpdateAddin/
    |   `-- 1.0.34.37/                 # official driver and firmware update engine
    |-- MultimediaAddin/
    |   `-- 1.1.4.10/                  # Dolby support and native runtimes
    |-- SmartColorAddin/
    |   `-- 1.1.4.22/                  # color add-in and x64 helpers
    |-- SmartInteractAddin/
    |   `-- 1.0.8.209/                 # interaction add-in, data, x64 helpers
    `-- SmartNoiseCancelledAddin/
        `-- 1.3.1.77/                  # audio add-in, resources, x64 helpers
```

`VantageAddins` and `LenovoPcManager` contain proprietary vendor components.
They are deliberately kept outside the repository, are not covered by the
Toolkit license, and must not be redistributed without permission from their
respective rightsholders. Each component's accompanying license and notice
files take precedence. PawnIO is installed separately and therefore does not
appear in this directory.

## Data and privacy

Opening warranty information sends the device serial number to Lenovo's warranty
services. Results are cached in
`%USERPROFILE%\.thinkbook_toolkit\warranty_cache.csharp.json`. The cache stores a
SHA-256 digest of the serial number and warranty dates, not the plain serial
number.

## Acknowledgements

- [Lenovo Legion Toolkit (LLT)](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit)
  is a reference for the structure and interaction design of a focused Lenovo
  device utility.
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
  provides hardware sensor access.
- [WindowsDisplayAPI](https://github.com/LenovoLegionToolkit-Team/WindowsDisplayAPI)
  provides access to Windows display paths and Dynamic Refresh Rate.
- [NAudio](https://github.com/naudio/NAudio) provides Windows audio endpoint
  state and microphone mute control used by Fn-key takeover.
- [PawnIO](https://github.com/namazso/PawnIO) provides the system-level access
  used for CPU temperature telemetry on the tested device.
- Some capabilities interoperate with components from
  [Lenovo Vantage](https://apps.microsoft.com/detail/9wzdncrfj4mv) and
  [Lenovo PC Manager](https://guanjia.lenovo.com.cn/).

Lenovo, ThinkBook, Vantage, and related names are trademarks of their respective
owners.

## License

Unless a file states otherwise, the original source code in this repository is
licensed under the [Mozilla Public License 2.0](LICENSE). Third-party components
retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
The MPL-2.0 license does not grant any rights to Lenovo software, trademarks, or
other proprietary components used alongside Toolkit.
