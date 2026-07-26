# Third-party notices

ThinkBook Toolkit's original source code is licensed under the Mozilla Public
License 2.0. The components listed below are separate works and remain subject
to their own copyright notices and license terms. This file is informational
and does not replace or modify those terms.

## NuGet components used by the build

| Component | Resolved version | License | Source |
| --- | ---: | --- | --- |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor> |
| DiskInfoToolkit | 2.1.0 | MPL-2.0 | <https://github.com/Blacktempel/DiskInfoToolkit> |
| RAMSPDToolkit-NDD | 1.5.0 | MPL-2.0 | <https://github.com/Blacktempel/RAMSPDToolkit> |
| BlackSharp.Core | 1.0.12 | MPL-2.0 | <https://github.com/Blacktempel/BlackSharp> |
| HidSharp | 2.6.4 | Apache-2.0 | <https://www.zer7.com/software/hidsharp> |
| Mono.Posix.NETStandard | 1.0.0 | MIT | <https://github.com/mono/mono> |
| System.IO.Ports | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |
| System.Management | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |
| System.ServiceProcess.ServiceController | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |
| System.Threading.AccessControl | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |
| System.CodeDom | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |
| System.Diagnostics.EventLog | 10.0.7 | MIT | <https://github.com/dotnet/dotnet> |

Platform runtime packages resolved transitively from the Microsoft `System.*`
packages use the same .NET repository and MIT license. Package versions and the
complete resolved graph can be inspected with:

```powershell
dotnet list .\src\ThinkBookToolkit\ThinkBookToolkit.csproj package --include-transitive
```

The complete MPL-2.0 text is included in [LICENSE](LICENSE). The Apache-2.0 and
MIT license texts are available from the respective package archives and the
following canonical locations:

- Apache-2.0: <https://www.apache.org/licenses/LICENSE-2.0>
- MIT: <https://opensource.org/license/mit>

LibreHardwareMonitor also contains material under additional licenses. Its
upstream attributions and license texts are maintained in
[LibreHardwareMonitor THIRD-PARTY-NOTICES.txt](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/THIRD-PARTY-NOTICES.txt).

## PawnIO

[PawnIO](https://github.com/namazso/PawnIO) provides the system-level access
used by LibreHardwareMonitor to obtain CPU temperature telemetry on the device
on which ThinkBook Toolkit was tested. PawnIO must be installed separately; its
driver and utilities are not stored in this repository or in the
`ThinkBookToolkit.Dependencies` directory.

PawnIO is licensed under GPL-2.0-or-later with the additional exception stated
in its [COPYING file](https://github.com/namazso/PawnIO/blob/master/COPYING).
PawnIO modules used by LibreHardwareMonitor are identified by LibreHardwareMonitor
as LGPL-2.1 material; see LibreHardwareMonitor's upstream third-party notice
linked above. These licenses apply to PawnIO and its modules, not as a
relicensing of ThinkBook Toolkit's original source code.

## Optional Lenovo components

Local development and runtime installations may use files from Lenovo Vantage
or Lenovo PC Manager, including these component groups:

- Lenovo Productivity System Add-in;
- Multimedia Add-in;
- Smart Color Add-in;
- Smart Interact Add-in;
- Smart Noise Cancellation Add-in;
- Lenovo PC Manager `WrapPlugin.dll`.

Those components, their bundled runtimes, metadata, images, language resources,
and notice files are proprietary or separately licensed vendor material. They
are intentionally kept outside this repository, are not covered by MPL-2.0,
and are not redistributed by the source repository. Their accompanying license
and third-party notice files control their use.

## Installer tooling

Windows setup packages are built with
[Inno Setup](https://jrsoftware.org/isinfo.php). The setup engine embedded in a
compiled installer remains subject to the Inno Setup license. The build script
also downloads the Simplified Chinese message file from the
[official Inno Setup source repository](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl)
and verifies its pinned SHA-256 before compilation. The translation credits its
upstream maintainer in the installer's About information.

## Reference project

[Lenovo Legion Toolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit)
is acknowledged as a design and interaction reference. It is not a NuGet or
binary dependency of this repository. Lenovo Legion Toolkit is distributed
under GPL-3.0 with the additional plugin exception stated by that project.
