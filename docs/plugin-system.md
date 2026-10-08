# Toolkit 插件系统（API v1）

## 使用

测试插件**不随正式安装包或主程序发布 ZIP 分发**。运行 `scripts/build_test_plugin.ps1` 单独生成 `dist/test-plugins/PluginTest` 文件夹和 `ThinkBookToolkit.PluginTest.zip`，将其中的插件文件夹放到“插件”页打开的目录，重启并审核启用后，导航栏会增加 **插件测试** 页面。
测试插件 1.1.0 起使用 `ui.custom` 自行绘制 WPF 页面，提供开关、风扇原始转速与平均值预览，适配深浅色、中英文和窄窗口；开关通过 `IPluginPageContext.SetSettingAsync` 保存。包中包含独立的逻辑 DLL 与 `ThinkBookToolkit.PluginTest.Ui.dll`，后台计算不依赖 WPF。也可直接在“导入插件”中选择 ZIP。旧版测试插件已安装时，先退出 Toolkit，再手动替换其文件夹并重启、重新审核权限。
“显示平均风扇转速”默认关闭。开启后，“平均转速”显示在完整概览的风扇转速区域，并作为插件传感器提供给 OSD、传感器记录和插件数据快照；简洁概览不追加这条读数。
数值为有效风扇读数的算术平均值：停转的 0 RPM 有效；负数、缺失或过期读数不参与；没有有效读数时显示 `--`。插件不修改风扇控制参数。

外部插件放入配置目录下的 `plugins/<目录>/`，每个目录包含 `plugin.json`、入口 DLL 和必要依赖。重启后在独立的 **插件** 管理页审核并启用。插件代码或清单指纹改变后，之前的授权不会自动沿用。插件配置位于配置目录下的 `plugin-settings/<插件ID>.json`。

也可以点击插件页的 **导入插件**，选择 `.zip` 文件。压缩包可直接包含 `plugin.json` 和 DLL，或将它们放在一个外层文件夹内；每个包只允许一个插件清单，全部文件必须位于该清单所在文件夹中。导入成功后立即出现在列表，**默认停用**，审核权限后才能运行；无需为发现新插件而重启。风扇后端仍遵守启用后重启才切换的规则。

导入只解压和检查文件，不运行 DLL；拒绝越界路径、链接、重复路径、缺失入口、不兼容清单及超限压缩包（最多 2048 个文件、解压后 256 MB、清单 1 MB）。原 ZIP 保留，临时解压文件自动清理。相同 ID 已安装或目标目录已存在时拒绝覆盖；更新现有插件请继续使用手动替换文件夹、重启后重新审核的方式。

### 卸载与重启提示

每张插件卡片提供 **卸载** 按钮，确认后移除插件文件，保留 `plugin-settings` 中的插件设置，取消启用授权。卸载不会删除其他插件或程序依赖。
也可通过“卸载旧版 → 必要时重启 → 导入新版”更新插件；重新导入后仍默认停用，并恢复与新清单类型兼容的保留设置。

- 普通工作进程插件先停止进程、撤回页面与传感器，然后直接移除文件。
- 当前会话已加载的 WPF 页面、当前选中的风扇后端，或暂时无法删除的文件，会标为“待卸载”，禁止再次启用，并弹出“是否立即重启 Toolkit”。选择“否”可以继续使用，之后手动重启完成。
- 当前风扇后端不会被热切换，也不会提前删除其守护恢复缓存；它保留到安全退出。缓存不在本次卸载中清理。
- 下次启动在加载插件前处理待卸载任务，先核对原插件目录、ID 和指纹，再移动到专用临时清理目录后删除。若用户已改变原目录内容，不会自动删除这些新内容，而是报告未完成。
- 导入的新插件默认停用，不会无意义地要求重启。启用/停用需要重启切换的风扇后端时会弹窗；同 ID 的旧插件还在等待卸载时，导入会提示先重启，完成后再导入。

“重启”仅重启 Toolkit，不重启 Windows。程序先暂停插件宿主操作、恢复自动风扇控制，再退出；如果恢复失败，则取消此次重启。新进程等待旧进程完全结束后才清理已加载的 DLL，并保留 `--disable-plugins` 安全启动模式。选择稍后重启不会自动启动新的进程。

`--disable-plugins` 可跳过全部插件加载，插件管理入口不允许被替换。停用插件会停止其进程并撤回页面、设置和传感器贡献，内置功能恢复。

## 项目结构

- `src/ThinkBookToolkit.PluginApi`：不依赖 WPF 或主程序内部类的契约，程序集版本固定为 1.0.0.0，协议版本为 1。
- `src/ThinkBookToolkit.PluginUi`：可选 WPF 自绘页面契约，目标为 `net9.0-windows`，程序集版本固定为 1.0.0.0。
- `src/ThinkBookToolkit.PluginHost`：独立进程、普通 asInvoker / 非 UIAccess 启动程序。
- `plugins/ThinkBookToolkit.PluginTest`：可独立编译的平均风扇转速示例。
- `plugins/ThinkBookToolkit.PluginTest.Ui`：测试插件的自绘 WPF 页面，与逻辑工程分离；单独构建此项目会同时构建逻辑工程。
- `ToolkitPluginManager`：清单、授权、注册表、配置、进程与故障处理。
- `PluginHostBridge`：宿主数据及经验证操作的适配层。

普通声明式页面、设置及传感器逻辑由各自的 PluginHost 加载，通过有长度限制的命名管道传递 JSON。宿主复用 `PluginApi` 契约程序集；插件依赖通过独立的 AssemblyLoadContext 和 AssemblyDependencyResolver 加载。风扇后端和显式授权的 WPF 自绘页面是例外，会在主程序内执行，具体安全边界和生命周期见下文。

**这是可信插件模型，不是恶意代码沙箱。** 工作进程继承启动者权限；权限声明约束宿主 API，不限制插件自行进行文件、网络或本机 API 调用。只启用审阅过的代码。进程隔离保护主程序免受插件崩溃、卡死影响，不提供操作系统级权限隔离。

## 清单与注册

参考 `plugins/ThinkBookToolkit.PluginTest/plugin.json`。插件 ID 使用小写字母、数字、点、横线和下划线；页面和传感器 ID 必须以 `<插件ID>.` 开头。

- `ApiVersion` 必须为 1，版本不兼容的插件不加载。
- `Author` 为可选作者名称，例如 `"Author": "Example Developer"`，最多 160 字符。声明后显示在插件管理卡片的名称下方；省略、`null` 或空白字符串不显示作者行，兼容原有插件。作者名称是插件自行声明的信息，不代表身份认证。
- `EntryAssembly` 必须为当前插件目录中的 DLL 文件名，不能越出目录。
- `EntryType` 为实现 `IToolkitPlugin` 的公开无参构造类型。
- `Pages`：声明式页面，包含 ID、中文/英文标题、排序及可选的 `Replaces`。
- `Settings`：boolean、number、string、choice 四类控件；包含默认值、范围/选项、所属页面及可选替换目标。
- `Sensors`：名称、单位、分类（`Category`）、ID、可选替换目标。新增读数按硬件分类直接加入现有栏目，不再创建“插件传感器”卡片。

### 标题与设置项分组

插件向内置页面追加设置时，可以通过可选的 `SettingGroups` 和设置的 `GroupId`，生成与本体相同的“标题 + 说明 + 多条设置”卡片。声明式插件页面同样支持；WPF 自绘页面仍由插件自行布局。

```json
"SettingGroups": [
  { "Id": "my.plugin.power", "PageId": "battery",
    "Title": { "Chinese": "扩展供电", "English": "Additional power settings" },
    "Description": { "Chinese": "插件提供的供电设置", "English": "Power settings provided by this plugin" },
    "Glyph": "\uE8B7", "Order": 10 }
],
"Settings": [
  { "Id": "enabled", "PageId": "battery", "GroupId": "my.plugin.power",
    "Title": { "Chinese": "启用功能", "English": "Enable feature" },
    "Description": { "Chinese": "控制插件功能的开关", "English": "Toggle the plugin feature" },
    "Glyph": "\uE7F4", "Kind": "boolean", "DefaultValue": false }
]
```

`Description`、`Glyph` 和分组 `Order` 均可省略；图标使用 Segoe Fluent Icons / Segoe MDL2 Assets 字符。每组最多 160 字符的中英文标题、1024 字符的说明；单个清单最多 32 组。组 ID 必须属于插件命名空间且不与其它贡献 ID 重复；设置必须引用本插件同一页面的分组。组内按 `Settings` 顺序显示，组间按 `Order` 排列；没有成员的组不显示。不同插件的同名标题不会合并。

未声明分组的旧插件仍可使用，默认值、控件类型和保存方式不变。分组只适用于新增设置，不能与设置的 `Replaces` 同时声明；替换项保留原页面位置。简洁概览仍不追加这些设置。新卡片复用本体透明度、主题、图标和窄窗口换行规则，不需要新增权限。

### 传感器绘图范围

传感器还可声明记录图表的纵轴范围：`ChartMinimum`（下限）和 `ChartMaximum`（上限），两者都是可选数字。例如：

```json
{ "Id": "my.plugin.fan", "Name": { "Chinese": "风扇转速", "English": "Fan speed" },
  "Unit": "RPM", "Category": "fans", "ChartMinimum": 0, "ChartMaximum": 6000 }
```

只写 `"ChartMinimum": 0` 时上限自动计算；只写 `"ChartMaximum": 6000` 时下限自动计算；省略或设为 `null` 表示不指定。两项都指定时必须满足下限小于上限，且均为有限数字。上下限只影响传感器记录绘图，不修改读数、保存数据、OSD 或硬件控制；超出指定范围的曲线显示在边界，记录值仍保留原值。

`Replaces` 替换传感器也支持这两个字段。声明自定义范围的替换读数会在原分类下单独绘图，避免把同图的其他内置曲线强制改成相同范围；未声明范围时保留既有内置图表规则。新增传感器不声明范围则使用自动缩放。

与现有的名称、分类一样，图表范围取自当前安装的插件清单，不写入记录文件。替换项优先使用当前启用的提供者；没有启用者但只有一个已安装候选时使用该候选，多个停用候选则不猜测。卸载插件后，历史数据仍可读取，范围回到自动或内置规则。

传感器的 `Category` 决定显示位置：

插件新增传感器会出现在“编辑概览页”（完整模式）、“OSD 设置 → 传感器”和“传感器记录设置”的对应分类中，包含自建分类。选项来自清单，不依赖当前是否有读数；未启用插件的选项也可提前配置。三处开关分别保存，默认开启，关闭一处不影响其他位置、插件自己的页面或插件数据快照。关闭记录只影响后续采样，不删除已经保存的历史数据。替换已有传感器继续使用被替换项原有的开关，不重复添加开关。

| Category | 概览栏目 | OSD 分组 |
| --- | --- | --- |
| `cpu` | CPU | CPU |
| `gpu` / `vram` | GPU | GPU / 显存 |
| `battery` | 电池 | 电池 |
| `memory` / `storage` | 内存与硬盘 | 内存 / 硬盘 |
| `memory-storage` | 内存与硬盘 | 内存 |
| `fans` | 风扇 | 风扇 |
| `power` | 功耗限制（完整版） | 无对应分组 |
| `warranty` | 保修信息 | 无对应分组 |
| `fps` | 无对应栏目 | FPS |

例如 `{"Id":"my.plugin.cpu-temp","Name":{"Chinese":"自定义温度","English":"Custom temperature"},"Unit":"°C","Category":"cpu"}` 会在完整概览的 CPU 卡片内增加读数。简洁概览不追加插件传感器行，用户隐藏的卡片仍保持隐藏，更新读数不会重建卡片。`VisibleSensors` 的动态显隐继续有效，插件停用后新增行撤回。

历史曲线同样并入现有 CPU、GPU、内存/硬盘/电池、风扇等分组。旧版默认 `plugin` 或未知分类不会再生成单独的概览/OSD 卡片；插件自身页面仍可查看，管理页会提示作者指定硬件分类。没有分类、已卸载插件留下的历史数据保留在“其他记录”，不会删除或猜测其硬件类别。传感器替换 `Replaces` 仍在原位置生效，不额外增加一行。

插件也可以显式新建类别，在清单中声明 `SensorCategories`，再由传感器的 `Category` 引用其 ID：

```json
"SensorCategories": [
  { "Id": "my.plugin.environment", "Title": { "Chinese": "环境", "English": "Environment" }, "Order": 10 }
],
"Sensors": [
  { "Id": "my.plugin.ambient-temperature", "Name": { "Chinese": "室内温度", "English": "Ambient temperature" },
    "Unit": "°C", "Category": "my.plugin.environment" }
]
```

这样会新增名为“环境”的完整概览卡片、OSD 分组和历史曲线分组，而不是“插件传感器”。简洁概览不显示新增类别。同一类别的多个读数合并显示；既有类别和自建类别可在同一插件中混用。自建类别排在内置类别之后，按 `Order` 和 ID 稳定排序。ID 必须属于插件自己的命名空间，并且不能与其页面、传感器或概览条目 ID 重复；最多 32 个类别，每个标题最多 160 字符，支持中英文。

自建类别没有可见读数时自动隐藏；后续读数出现时恢复，普通数值更新不会重建概览卡片。停用或挂起插件会撤回实时栏目，但仍安装的插件即使停用，其历史记录仍使用原类别名称；卸载后历史数据继续保留在“其他记录”。

设置的 PageId 可以是自己的插件页面 ID，也可以是内置页面 ID，以在该页末尾加入设置。

内置页面 ID：`overview`、`performance`、`cooling`、`battery`、`display`、`sound`、`input`、`automation`、`sensors-integration`、`device`、`driver-update`、`advanced`、`settings`。
页面 `Replaces` 使用这些 ID，导航入口保持不变，启用时由插件页面接管，禁用或故障时恢复。`plugins` 管理页不能替换。

第一版内置设置替换槽：

| ID | 类型 |
| --- | --- |
| toolkit.setting.IntervalSeconds | number |
| toolkit.setting.Language | choice |
| toolkit.setting.Theme | choice |
| toolkit.setting.OverviewPageMode | choice |
| toolkit.setting.LogLevel | choice |
| toolkit.setting.LogRetentionDays | number |

替换这些设置时，值仍来自宿主，写入仍通过对应的 Toolkit 方法进行校验、保存和应用，不会变成与宿主脱节的插件私有设置。增加新的内置替换槽时，应同时在 `PluginBuiltinSettings` 和 `SettingRow` 中注册稳定 ID。

传感器替换使用 `toolkit.sensor.<MetricKey>`；MetricKey 取自 `SensorRecordingFormat.MetricKeys`，如 `fan1Rpm`、`fan2Rpm`、`cpuTemperatureC`。替换用于界面读数、OSD 和记录；原始硬件快照及风扇安全控制输入不会被改写。同一页面、设置或传感器替换目标只允许一个启用的插件占用，冲突时拒绝后启用者。

## 数据、权限和执行

### 可选 WPF 自绘页面

旧 `Pages` 声明不变，仍由宿主生成设置/传感器界面。需要自行绘制时，增加 `ui.custom` 权限，并给页面添加 `View`：

```json
"Permissions": ["ui.custom", "sensors.read"],
"Pages": [
  {
    "Id": "my.plugin.dashboard",
    "Title": { "Chinese": "自定义仪表盘", "English": "Custom dashboard" },
    "View": { "Assembly": "MyPlugin.UI.dll", "Type": "MyPlugin.UI.DashboardPage" }
  }
]
```

替换内置页面时再指定 `Replaces`（例如 `performance`），并增加原有 `replace` 权限。插件管理页 `plugins` 仍禁止替换，安全启动 `--disable-plugins` 会跳过插件。一个包可以同时包含声明式页面、自绘页面、传感器和风扇后端。

UI 项目使用 `net9.0-windows`、`UseWPF=true`，引用 `ThinkBookToolkit.PluginUi` 与 `ThinkBookToolkit.PluginApi`，无需引用 Toolkit 主程序集。`View.Type` 是公开、非抽象、具有公开无参构造函数的 `IToolkitPluginPage` 实现。`View.Assembly` 只能是插件文件夹中的 DLL 文件名。示例：

```csharp
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit.PluginUi;

namespace MyPlugin.UI;

public sealed class DashboardPage : IToolkitPluginPage
{
    private readonly TextBlock reading = new() { FontSize = 24 };

    public FrameworkElement CreateView(IPluginPageContext context)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "My dashboard", FontSize = 28 });
        panel.Children.Add(reading);
        return panel; // 也可以返回自定义 UserControl / XAML 页面。
    }

    public void Update(PluginPageState state)
    {
        var sensors = state.Request.Context.Sensors;
        reading.Text = sensors.TryGetValue("toolkit.sensor.cpuTemperatureC", out var sensor)
            && sensor.Quality == "valid" && sensor.Value.HasValue
            ? $"{sensor.Value:0.#} °C" : "--";
    }

    public void Dispose() { /* 停止自己的定时器、取消后台工作并退订事件 */ }
}
```

`CreateView`、首次及后续 `Update`、`Dispose` 均在 UI 线程调用，不要阻塞 UI 或同步等待异步操作。每次进入页面会新建实例；切页、停用、主题/语言重建及退出时释放实例。页面提供的 `FrameworkElement` 应是新建且没有父级的控件，宿主不会再自动往它里面添加该页面的声明式控件。

`IPluginPageContext` 提供插件 ID、页面 ID、插件目录、当前 `State` 和页面生命周期的 `CancellationToken`。`State` 包含深浅色、语言和 `PluginRequest`；原始数据和本体设置仍受 `sensors.read` / `data.read` / `settings.read` 约束。`SetSettingAsync(id, value)` 只能写本插件已声明的设置，经过原有类型及范围校验；`ExecuteAsync(command)` 仍要求已有的 `host.control` 权限。**没有增加修改其他插件设置的接口。** 页面释放后上下文拒绝新写入；插件应自行处理按钮/异步事件中的异常。

普通 `EntryAssembly` / `EntryType` 逻辑入口仍必需。建议将无 UI 的逻辑 DLL 和 WPF UI DLL 分开打包：前者继续在 PluginHost 工作进程中运行，后者按需在主程序中加载；不要让工作进程入口依赖 WPF。仅需要页面时，逻辑入口可返回空 `PluginResult`。两个进程不能通过静态字段共享状态，使用宿主持久化的插件设置传递数据。

**`ui.custom` 是高风险、进程内能力，不是沙箱。** 启用确认框和插件卡片都会明确提示：代码具有主程序权限，可能让整个 Toolkit 卡住或退出。宿主会捕获调用创建、更新和释放接口时的托管异常，并挂起插件、撤回贡献、恢复可用的内置页面；这不能隔离任意事件回调、后台线程、原生异常或死锁。首次载入会校验包指纹，变更文件必须重新审核。WPF 的资源和静态缓存无法保证卸载，因此停用只释放页面，不保证从进程移除代码；更新已加载的 UI DLL 需要重启。

### 右下角提示

插件声明 `ui.toast` 权限后，可调用 Toolkit 窗口内的右下角提示，无需申请 `host.control`。

普通工作进程插件在一次 `EvaluateAsync` 响应中设置：

```csharp
return new PluginResult(values)
{
    Toast = new PluginToast("操作已完成")
};
```

自绘页面通过 `IPluginPageContext` 调用：

```csharp
await context.ShowToastAsync("操作已完成");
await context.ShowToastAsync("操作失败，请检查设置", isError: true);
```

普通提示和错误提示使用 Toolkit 原有样式及自动消失行为，自动加上插件名称，悬停可查看插件 ID。文字必须非空且不超过 512 字符。每个插件的提示间隔至少 1 秒；与上一条相同的内容及级别在 10 秒内不重复展示，自绘接口被限频时返回 `false`。应只在操作完成或状态变化时请求，不要每次刷新重复发送。插件停用、挂起、卸载或宿主退出期间不可发送；自绘页面释放后也不可发送。

这是应用窗口内的提示，不是 Windows 通知，也不会将隐藏或最小化的 Toolkit 强行打开。原有 `host.control` 的 `SetStatus` 调用保持兼容。

### 概览卡片的单条内容

清单可选字段 `OverviewItems` 支持在概览的指定卡片中增加、删除或替换一条内容，不需要替换整个页面，也不改变原始硬件数据、OSD 或记录数据。旧插件不声明此字段即可保持原行为。

```json
"OverviewItems": [
  { "Id": "my.plugin.note", "CardId": "cpu", "Action": "add",
    "Label": { "Chinese": "运行状态", "English": "Status" },
    "Text": { "Chinese": "正常", "English": "Normal" }, "Order": 10 },
  { "Id": "my.plugin.hide", "CardId": "gpu", "Action": "remove", "Target": "hotspot-temperature" },
  { "Id": "my.plugin.fan", "CardId": "fans", "Action": "replace", "Target": "fan1-speed",
    "Label": { "Chinese": "平均转速", "English": "Average speed" }, "SensorId": "my.plugin.average-rpm" }
]
```

- `CardId`：`cpu`、`gpu`、`battery`、`memory-storage`、`fans`、`power`、`warranty`。
- `Target` 是该卡片现有的内容 ID，例如 CPU 的 `temperature` / `power`、风扇的 `fan1-speed` / `fan1-target`。完整表见 `OverviewLayoutSettings.cs` 的 `CardDefinitions`；`disk-temperatures` / `disk-health` 分别表示动态磁盘读数组。增加内容不填写 `Target`。
- `Id` 必须在插件命名空间内，且不与其页面或传感器 ID 重复。删除和替换需要 `replace` 权限；同一卡片中的同一目标只能由一个启用插件占用。
- 新内容按 `Order`、ID 排序追加到卡片。替换在原位置进行；修改双列行的一侧不会删除另一侧。删除只在插件启用期间生效，不覆盖用户布局设置，停用或插件出错会恢复原内容。
- `Label` 为双语标签。值可用固定的双语 `Text`，或用 `SensorId` 引用本插件声明的传感器。动态文本可从 `PluginResult.OverviewValues` 返回 `{ "my.plugin.note": "当前状态" }`；文本上限 4096 字符，仅按纯文本渲染。值优先级为 `SensorId` > 动态文本 > `Text` > `--`；超过 10 秒的动态值显示 `--`。
- 用户隐藏的卡片/内置条目不会被插件强制显示。简洁概览只应用 `replace` 和 `remove`，且仅作用于该模式本来就存在的条目；有实际替换/删除的卡片使用逐条读数布局，其他卡片保持原布局。`add` 不增加内容，也不改变卡片布局；即使同一卡片同时声明增加和替换，也只应用替换。概览顶部模式操作区不属于这些传感器卡片。

简洁模式同样不追加插件传感器行、自建传感器类别或通过 `Settings.PageId = "overview"` 新增的设置；切回完整模式后这些内容恢复显示。已有传感器的 `Replaces` 和整页替换仍按原规则生效。此限制只针对简洁概览，不影响 OSD、插件自身页面、历史曲线和插件数据快照。

返回动态值只更新原控件的文字，不重建页面或重新播放切页动画。

### 宿主上下文

`IToolkitPlugin.EvaluateAsync` 接收 `PluginRequest`，返回 `PluginResult`，宿主约每秒调用一次。

- `sensors.read`：`Context.Sensors` 提供内置原始传感器字典、单位、时间、来源和有效性。不要把 unavailable/stale 当成零。
- `data.read`：`Context.Data` 提供运行快照、功能报告、FPS、风扇曲线配置、目录和当前记录缓冲。
- `settings.read`：`Context.Settings` 提供完整的 AppSettings JSON 快照，而非可变对象引用。
- `host.control`：允许结果中的 `Commands` 调用 `Context.Operations` 列出的宿主操作。
- `replace`：允许清单接管已注册的设置、传感器或页面目标，启用提示会列出目标。
- `fan.backend`：允许插件提供风扇控制后端；必须另外声明 `FanBackend`，启用提示会说明进程内加载与重启要求。
- `ui.custom`：允许插件提供 WPF 自绘页面，在主程序进程内运行；启用时单独列出风险提示。
- `ui.toast`：允许发送 Toolkit 窗口内的右下角提示，不包含修改设置或控制硬件的权限。

插件自己的设置始终通过 `PluginRequest.PluginSettings` 提供，由宿主负责类型检查和持久化。
宿主命令参数使用列出的参数名称与 JSON 值，枚举可使用字符串；操作进入原有 Runtime 方法，保留其验证、串行化及硬件保护。宿主不会通过插件协议暴露私有字段、任意反射或任意方法执行。
不要每帧无条件重复写硬件；应由插件根据输入和自身状态判断是否真的需要改变设置。

`PluginResult.Values` 只能返回清单声明的传感器 ID，拒绝非有限数。`VisibleSensors` 可动态显隐传感器。超过 10 秒的旧输出显示为不可用。超时、异常、错误协议或越界输出会挂起该插件并撤销页面/设置/传感器贡献；用户可在插件页重新启用。已经选中的风扇后端不会因此热切换。关闭管道时宿主尝试释放插件，不能及时退出的进程由进程作业终止。

记录保持兼容原 v2 数字索引格式；存在插件字段时使用 v3 字符串字段表，键为 `plugin:<传感器ID>`。查看器支持这些字段；插件未安装时仍以 ID 显示历史数据。`data.read` 的运行时快照包含 `PluginSensors`，提供来源、替换目标和插件读数。

## 替代旧本地联动接口

Toolkit 本体不再启动 localhost HTTP 服务，旧端口和联动开关配置会被忽略。需要此功能时，由用户安装可信的联动插件；不会自动安装或启用替代插件。

逻辑插件可以自行维护 HTTP 监听器和请求队列，使用 `sensors.read` 获取基础传感器、`data.read` 获取运行时快照，并通过 `host.control` 提交 `SetItsModeAsync(mode)`、`SetFanModeAsync(mode)`、`SetFullSpeedAsync(enabled)` 命令，覆盖原接口的读数及控制能力。命令参数名称应以 `Context.Operations` 实际声明为准。普通逻辑插件在宿主轮询时提交排队命令，不能把“已入队”宣称为“硬件已确认”。

网络端口、令牌验证、请求大小限制、跨域和速率限制由插件负责。监听器应在插件 `DisposeAsync` 时关闭；停用、失败或退出时宿主也会结束其工作进程，释放端口。旧客户端需要按所安装插件的协议调整，旧 HTTP 协议不会由本体继续提供。

## 风扇后端插件

选择优先级：**已授权并启用的风扇后端插件 > 程序目录中的 `ThinkBookToolkit.FanBackend.dll`**。后者无论是内置 WMI 后端还是用户手动替换的 DLL，继续按原方式加载。插件不会覆盖或删除这个 DLL；停用插件并重启后即可恢复使用它。`--disable-plugins` 同样使用程序目录中的 DLL。

在常规插件清单中增加以下字段（其余 `EntryAssembly`、`EntryType`、`Pages`、`Settings`、`Sensors` 等仍照常声明）：

```json
{
  "Permissions": ["fan.backend", "sensors.read"],
  "FanBackend": {
    "Assembly": "ThinkBookToolkit.FanBackend.dll",
    "Type": "MyPlugin.MyFanBackend"
  }
}
```

- `Assembly` 必须是插件目录内的 DLL 文件名，`Type` 为公开、非抽象且有公开无参构造函数的 `IFanBackend` 实现类型全名，API 版本必须等于 `FanBackendContract.CurrentVersion`（当前 1.1）。可直接包装既有后端，无须改其接口。
- 同一个插件包可以同时提供风扇后端、页面、设置、传感器及宿主操作。后端与普通逻辑入口可在同一个 DLL，也可以分别放在两个 DLL 中；`EntryType` 仍须实现 `IToolkitPlugin`。仅提供后端时，其普通逻辑入口返回空 `PluginResult` 即可。
- 同时只允许启用一个风扇后端提供者，冲突时拒绝后启用者。仅提供后端无需 `replace` 权限；同时替换页面等仍需 `replace`。
- 启用、停用、更新及改选风扇后端均在**重启后**生效。页面/设置/传感器仍可立即启停。正在使用的后端固定到本次运行结束，不因普通插件工作进程故障而切换控制通道。
- 后端直接参与原有读取、写入、睡眠恢复、全速控制和守护恢复流程，保留最小读写间隔、风扇数量、启动提示及 `ControlSemantics`。零转速不被统一解释为自动控制。普通传感器替换依然不改写闭环安全控制输入。
- 启动时先选择后端，再检测硬件。已选中插件准备/加载失败会明确报错并使风扇控制不可用，不静默降级到另一套硬件控制接口。

**风扇后端不是进程隔离插件。** 它在主程序以及异常退出恢复服务中执行，具有相应权限，因此只应启用可信代码。普通逻辑与后端处于不同进程，不能靠共享静态字段交换设置；后端必须能独立初始化，并独立完成 `RestoreAuto`。依赖文件应随包分发，并相对后端程序集目录定位，不能假定当前工作目录等于插件目录。

通过审核的包会复制到 `%ProgramData%/ThinkBookToolkit.PluginFanBackends/<SHA256>/` 的管理员受保护缓存，保留原文件。主程序与守护服务使用同一份固定副本；服务仅接受包身份、指纹和声明类型，不接受任意 DLL 绝对路径。缓存及包清单、文件内容、目录链接和写入权限均需通过校验。源目录更新或删除不影响本次运行的异常恢复；缓存不会在运行中自动清理。后端使用独立程序集加载上下文，避免与手动替换 DLL 同名时加载错程序集。

包目录必须保持不变：日志、临时文件及可写配置不要写入程序集旁边，否则指纹校验会失败。后端需要的恢复参数应单独、安全地持久化，使守护服务在没有普通插件工作进程的情况下也能恢复自动控制。

## 构建与验证

`dotnet build ThinkBookToolkit.sln -c Release` 会构建插件宿主与独立测试工程；主程序仅复制/发布 PluginHost 和 PluginApi，不复制测试插件。安装器额外排除测试插件文件，正式打包脚本发现测试插件残留时会停止。联想专有 DLL 的排除规则保持不变。
OSD 插件读数按传感器分类并入对应分组，使用和内置传感器相同的字号、标签/数值颜色、纵向左右对齐和横向布局；RPM 在中文 OSD 中显示为“转”。

集成测试：

```powershell
dotnet tests/ThinkBookToolkit.UiSmokeTests/bin/Release/net9.0-windows/win-x64/ThinkBookToolkit.UiSmokeTests.dll --test-plugins
```

测试使用模拟风扇数据和独立临时配置目录，验证进程握手、平均值边界、默认关闭、配置持久化、替换冲突、停用恢复、权限拒绝、子进程异常及安全启动。风扇插件测试另覆盖同名程序集隔离、契约版本、控制语义、插件优先级、重启切换、冲突、失败不回退和守护标记身份。测试用后端仅操作内存，不随主程序发布；生命周期测试用验证回调替代 ProgramData 缓存写入，不安装或启动真实守护服务。

## 第一版边界

当前提供声明式设置/页面、数字传感器扩展，以及显式授权后的 WPF 自绘页面；不提供可靠程序集卸载、热更新或插件市场。传感器替换不用于风扇闭环安全控制。权限模型不是 OS 沙箱。扩展 API 需要保持 v1 的兼容性；不兼容修改应升级协议版本并提供迁移。
