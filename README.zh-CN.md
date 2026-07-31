# ThinkBook Toolkit

## 免责声明

ThinkBook Toolkit 是独立开发的实验性项目，与联想公司无关，不是联想官方项目，也未获得联想的认可、支持或赞助。

本软件会读取并写入硬件和固件设置。不正确或不兼容的设置可能影响散热、性能、稳定性、硬件寿命、保修状态或数据安全。使用者须自行判断并承担全部风险和后果。如果您不了解或不接受这些风险，请勿安装或使用本软件。

**所有功能仅在 ThinkBook 16p G6 IAX、BIOS R2CN57WW 上进行过测试。不保证任何功能在其它机型或 BIOS 版本上可用或安全。功能检测只决定界面显示哪些项目，不代表兼容性或安全性已经得到验证。**

[English](README.md)

[更新日志](CHANGELOG.md)

## 项目简介

ThinkBook Toolkit 是面向部分 Lenovo ThinkBook 设备的 Windows 原生控制中心。软件使用 WPF 构建，常用调控都在同一个主窗口中完成，并优先加载概览，再逐步检测其它功能。软件可以独立运行，不依赖 ThinkBook Fan Control。

目前包括以下功能：

- 性能模式、GPU 工作模式、温度与功耗监控、风扇控制；
- 固定转速、CPU/GPU 风扇曲线、可增减点并带迟滞阈值的高级曲线、配置方案、游戏检测和风扇拉满；
- 充电模式、隔夜充电、持续 USB 供电、开盖启动和完整电池信息；
- 护眼、色彩模式、Dolby、扬声器/麦克风降噪、键盘、功能键、OSD 和触摸板设置；
- 设备、固件、存储和联想保修信息；
- 功耗参数查看，以及受支持设备上的参数调整；
- BIOS 启动操作和开机 Logo 更换；
- 深浅色主题、中英文、托盘控制和开机自启。

不可用的功能不会出现在对应调控页面中；设置页可以查看完整的功能可用性汇总。

## 运行要求与安全提示

- Windows 11 x64，并以管理员权限运行；
- 设备对应功能所需的联想驱动和服务；
- 在已测试设备上，LibreHardwareMonitor 读取 CPU 温度需要系统已安装并能够使用 [PawnIO](https://pawnio.eu/)。PawnIO 是系统级组件，不随 Toolkit 或外置依赖目录提供；
- 使用自定义风扇控制时，应持续观察温度；
- 使用可替换风扇后端前，应先确认能够可靠恢复固件自动控制。

切换 GPU 模式或执行部分固件操作可能需要重启。正常退出以及执行会重启计算机的操作前，软件会先尝试恢复固件自动风扇控制。

## 可替换风扇后端

Toolkit 会从程序目录加载 `ThinkBookToolkit.FanBackend.dll`。本仓库只包含 WMI 实现；替换这一个文件即可改变风扇监控与控制方式。Toolkit 不会根据设备型号选择或拒绝某个后端。

替换用的程序集需要面向兼容的 .NET Windows 运行时，引用 `ThinkBookToolkit.FanBackend.Contracts.dll`，并提供一个具有无参构造函数、公开且非抽象的 `IFanBackend` 实现。后端必须声明：

- 通过 `ApiVersion` 声明风扇后端 API 版本 `1.1`；
- 用于识别的 `Name` 和 `Transport`；
- 可选的本地化启动提示；不需要提示时返回 `null`；
- 是否支持在睡眠前释放风扇控制并在唤醒后恢复；
- 普通读取和普通写入的最小间隔（同时写入两个风扇视为一个批次；风扇拉满和恢复自动不受该间隔约束）；
- 目标转速为 `0` 时，是把对应风扇交还固件，还是在保持手动控制的同时关闭风扇；
- 恢复固件自动控制的方式；
- 开启和关闭风扇拉满的方式；
- 风扇状态/范围读取、双风扇目标写入、恢复自动和风扇拉满操作。

最小声明示例：

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

需要启动提示的后端可以按语言代码声明标题和正文，并提供回退文本：

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

Toolkit 会根据当前界面语言选择文本。用户可以只确认，也可以选择“确定并不再显示”；
后者仅对当前 DLL 内容有效。替换风扇后端后，即使文件名相同，Toolkit 也会重新记录是否隐藏提示。

这些声明必须准确描述实际实现。Toolkit 不会假定 `0 RPM` 一定表示恢复自动，也不会假定风扇拉满等同于写入最大转速。

## 构建与测试

安装 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)，然后在仓库根目录运行：

```powershell
.\scripts\build.ps1
```

生成位于 `dist\v0.2.1\ThinkBookToolkit-0.2.1-win-x64-framework-dependent` 的公开、依赖框架版本：

```powershell
.\scripts\build.ps1 -Configuration Release -Publish
```

生成在线安装包（需要安装 [Inno Setup 6](https://jrsoftware.org/isdl.php)）：

```powershell
.\scripts\build.ps1 -Configuration Release -Installer
```

输出文件为 `dist\v0.2.1\ThinkBookToolkit-0.2.1-Setup.exe`。安装位置默认为
`Program Files\ThinkBook Toolkit`，可在安装向导中修改。如果所选文件夹非空，安装器会明确提示
其中所有内容将被删除，并且只有确认后才继续；应始终选择 Toolkit 专用文件夹。如果目录中已有
文件版本兼容的风扇后端，安装器会默认保留并允许改用安装包内置后端。如果已有后端的文件版本
不符合当前要求或无法读取，安装器会明确提示该自定义后端将被内置后端替换。完成页默认勾选
启动 Toolkit。安装器会检测 64 位 .NET 9 Desktop Runtime；如果未安装，则下载微软官方
.NET 9.0.18 Desktop Runtime
安装程序、校验固定的 SHA-256 后静默安装。只有确实需要把运行时一并打包的较大便携版本时，
才使用 `-Publish -SelfContained`。

公开发布构建默认排除本地专有联想 DLL。仅为自己的电脑生成包含本地依赖的私用构建时，可增加
`-IncludeLocalProprietaryDependencies`。当前软件版本为 `0.2.1`，可替换风扇后端 API 版本为 `1.1`，配置文件格式版本为 `1.0`。

运行不会写入硬件的 UI 冒烟测试：

```powershell
dotnet run --project .\tests\ThinkBookToolkit.UiSmokeTests\ThinkBookToolkit.UiSmokeTests.csproj -c Release
```

运行无需管理员权限、不会写入硬件的 UI 预览：

```powershell
dotnet run --project .\tests\ThinkBookToolkit.UiPreview\ThinkBookToolkit.UiPreview.csproj -c Release
```

## 可选专有组件

部分显示、声音和固件功能会加载 [Lenovo Vantage](https://apps.microsoft.com/detail/9wzdncrfj4mv) 或 [联想电脑管家](https://guanjia.lenovo.com.cn/) 安装的 DLL 组件。这些专有文件不存放在本仓库中；运行对应功能时，仍可能需要相关的联想软件、服务和驱动。

安装向导提供“自定义联想 DLL 目录”选项，默认不勾选。启用后，目录保存到
`%USERPROFILE%\.thinkbook_toolkit\app_settings.csharp.json` 的
`UseCustomLenovoDllDirectory` 和 `CustomLenovoDllDirectory` 字段。Toolkit 会先检查有效且已启用的
自定义目录，再使用原有回退位置（程序目录；适用时也包括系统中安装的 Vantage 插件目录）。自定义根目录中应包含
`VantageAddins` 和/或 `LenovoPcManager`；安装器只保存目录引用，不会把这些文件复制到安装目录。

如果需要在本机发布包中加入从您自己的安装环境取得的文件，可把 MSBuild 属性 `ExternalDependenciesRoot` 指向包含 `VantageAddins` 和/或 `LenovoPcManager` 的目录。默认本地路径是仓库旁的 `ThinkBookToolkit.Dependencies`。没有该目录的全新源码副本仍可构建；如果程序也无法从已安装的软件中找到必需组件，对应的可选功能会显示为不可用。

当前本地依赖目录结构如下。版本目录名表示开发时使用的组件版本，不构成兼容性保证。

```text
ThinkBookToolkit.Dependencies/
|-- LibreHardwareMonitorLib.dll        # 本地参考副本；实际构建使用 NuGet 包
|-- LenovoPcManager/
|   `-- WrapPlugin.dll
`-- VantageAddins/
    |-- LenovoProductivitySystemAddin/
    |   `-- 1.0.0.138/                 # BIOS 工具、元数据和声明文件
    |-- MultimediaAddin/
    |   `-- 1.1.4.10/                  # Dolby 支持和原生运行库
    |-- SmartColorAddin/
    |   `-- 1.1.4.22/                  # 色彩插件和 x64 辅助程序
    |-- SmartInteractAddin/
    |   `-- 1.0.8.209/                 # 交互插件、数据和 x64 辅助程序
    `-- SmartNoiseCancelledAddin/
        `-- 1.3.1.77/                  # 音频插件、资源和 x64 辅助程序
```

`VantageAddins` 和 `LenovoPcManager` 中是专有厂商组件，因此有意存放在仓库外，不受 Toolkit 许可证覆盖，也不应在未经相应权利人许可的情况下重新分发。每个组件随附的许可证和第三方声明优先适用。PawnIO 需要单独安装，因此不在此目录中。

## 数据与隐私

打开保修信息时，程序会把设备序列号发送给联想的保修服务。结果缓存在 `%USERPROFILE%\.thinkbook_toolkit\warranty_cache.csharp.json`；缓存只保存序列号的 SHA-256 摘要和保修日期，不保存明文序列号。

## 致谢

- [Lenovo Legion Toolkit（LLT）](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit) 为面向联想设备的专用工具结构和交互设计提供了参考；
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 提供硬件传感器访问；
- [PawnIO](https://github.com/namazso/PawnIO) 提供 LibreHardwareMonitor 在已测试设备上读取 CPU 温度所需的系统级访问；
- 部分功能会与 [Lenovo Vantage](https://apps.microsoft.com/detail/9wzdncrfj4mv) 和 [联想电脑管家](https://guanjia.lenovo.com.cn/) 的组件协作。

Lenovo、ThinkBook、Vantage 及相关名称的商标权归其各自权利人所有。

## 许可证

除非文件中另有声明，本仓库的原创源代码使用 [Mozilla Public License 2.0](LICENSE) 许可。第三方组件继续遵循各自的许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。MPL-2.0 不授予对与 Toolkit 配合使用的联想软件、商标或其它专有组件的任何权利。
