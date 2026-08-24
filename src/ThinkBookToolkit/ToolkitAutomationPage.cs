using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed class ToolkitAutomationPage : ToolkitPageBase
{
    private readonly StackPanel _automationRows = new();
    private readonly StackPanel _automationDetails = new();
    private readonly StackPanel _editor = new();
    private Border _editorCard = new();
    private readonly StackPanel _stepRows = new();
    private readonly TextBox _name = new() { MinWidth = 260 };
    private readonly ComboBox _category = new() { MinWidth = 160 };
    private readonly ComboBox _action = new() { MinWidth = 230 };
    private readonly ComboBox _option = new() { MinWidth = 210 };
    private readonly TextBox _value = new() { MinWidth = 210 };
    private readonly TextBox _arguments = new() { MinWidth = 180 };
    private readonly TextBlock _argumentsLabel = new();
    private readonly Button _browse;
    private readonly Button _addStep;
    private readonly TextBlock _status;
    private readonly CheckBox _automationEnabled = new();
    private readonly Button _automationExpand;
    private readonly KeyboardMacroPanel _macroPanel;
    private AutomationDefinition? _draft;
    private int? _editingStepIndex;
    private readonly Dictionary<AutomationTriggerKind, CheckBox>
        _triggerToggles = [];

    public ToolkitAutomationPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        DataContext = new AutomationViewModel(runtime);
        _browse = ActionButton(L("浏览", "Browse"));
        _addStep = ActionButton(L("添加步骤", "Add step"), primary: true);
        _automationExpand = ActionButton(L("展开", "Expand"));
        _macroPanel = new KeyboardMacroPanel(
            runtime,
            (text, primary, danger) =>
                ActionButton(text, primary, danger),
            (title, description, control) =>
                SettingRow(title, description, control),
            (title, content, description) =>
                Card(title, content, description, "\uE70F"));
        _status = StatusText();
        Content = BuildLayout();
        RenderAutomations();
        Runtime.MacroChanged += OnMacroChanged;
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();
        var create = ActionButton(L("新建自动化", "New automation"), primary: true);
        create.Click += (_, _) => BeginEdit(null);
        _automationExpand.Click += (_, _) => SetAutomationExpanded(
            _automationDetails.Visibility != Visibility.Visible);
        _automationEnabled.IsChecked = Runtime.Settings.AutomationEnabled;
        _automationEnabled.Click += (_, _) =>
        {
            if (!Runtime.TrySetAutomationEnabled(
                    _automationEnabled.IsChecked == true,
                    out var error))
            {
                _automationEnabled.IsChecked = Runtime.Settings.AutomationEnabled;
                _status.Text = L("设置失败：", "Setting failed: ") + error;
            }
        };
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerActions.Children.Add(new TextBlock
        {
            Text = L("启用自动化", "Enable automation"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _automationEnabled.Margin = new Thickness(0, 0, 14, 0);
        headerActions.Children.Add(_automationEnabled);
        create.Margin = new Thickness(0, 0, 14, 0);
        _automationExpand.Margin = new Thickness(0);
        headerActions.Children.Add(create);
        headerActions.Children.Add(_automationExpand);
        _automationDetails.Visibility = Visibility.Collapsed;
        _automationDetails.Margin = new Thickness(0);
        _automationRows.Margin = new Thickness(0);
        _automationDetails.Children.Add(_automationRows);

        BuildEditor();
        _editorCard = Card(
            L("编辑自动化", "Edit automation"),
            _editor,
            L(
                "步骤严格按照列表顺序执行；某一步失败时会停止后续步骤。",
                "Steps run strictly in list order; a failed step stops the remaining steps."),
            "\uE70F");
        _editorCard.Visibility = Visibility.Collapsed;
        _automationDetails.Children.Add(_editorCard);
        root.Children.Add(Card(
            L("自动化", "Automation"),
            _automationDetails,
            L(
                "将设备控制、应用操作和延迟按任意顺序组合，并可手动运行或绑定到 Fn 快捷键。",
                "Combine device controls, application actions, and delays in any order, then run them manually or bind them to Fn keys."),
            "\uE771",
            headerAction: headerActions));
        root.Children.Add(Card(
            L("键盘宏", "Keyboard macros"),
            _macroPanel,
            L(
                "录制、修改并播放键盘按下/释放序列；可绑定普通按键或作为自动化步骤。",
                "Record, edit, and play keyboard down/up sequences; bind them to a key or use them in automations."),
            "\uE765",
            headerAction: _macroPanel.HeaderActions));
        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        return root;
    }

    private void BuildEditor()
    {
        _editor.Children.Add(SettingRow(
            L("名称", "Name"),
            L("用于列表和 Fn 快捷键映射。", "Used in the list and Fn-key mappings."),
            _name));
        var triggers = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var trigger in Enum.GetValues<AutomationTriggerKind>())
        {
            var toggle = new CheckBox
            {
                Content = TriggerName(trigger),
                Margin = new Thickness(0, 0, 16, 8)
            };
            _triggerToggles[trigger] = toggle;
            triggers.Children.Add(toggle);
        }
        _editor.Children.Add(SettingRow(
            L("事件触发", "Event triggers"),
            L(
                "总开关开启后，在选定事件发生时运行此自动化。游戏事件与固定转速共用同一检测结果。",
                "When automation is enabled, run this automation for selected events. Game events share the fixed-RPM detector."),
            triggers));
        _editor.Children.Add(new TextBlock
        {
            Text = L("步骤", "Steps"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 12, 2, 8)
        });
        _editor.Children.Add(_stepRows);

        var addGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        _category.Margin = new Thickness(0, 0, 8, 0);
        _action.Margin = new Thickness(0, 0, 8, 0);
        addGrid.Children.Add(_category);
        Grid.SetColumn(_action, 1);
        addGrid.Children.Add(_action);
        var valueHost = new Grid();
        valueHost.Children.Add(_option);
        valueHost.Children.Add(_value);
        Grid.SetColumn(valueHost, 2);
        addGrid.Children.Add(valueHost);
        _editor.Children.Add(addGrid);

        var appDetails = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _browse.Margin = new Thickness(0, 0, 8, 0);
        _arguments.Margin = new Thickness(0, 0, 8, 0);
        appDetails.Children.Add(_browse);
        _argumentsLabel.Text = L("启动参数", "Arguments");
        _argumentsLabel.VerticalAlignment = VerticalAlignment.Center;
        _argumentsLabel.Margin = new Thickness(0, 0, 6, 0);
        appDetails.Children.Add(_argumentsLabel);
        appDetails.Children.Add(_arguments);
        appDetails.Children.Add(_addStep);
        _editor.Children.Add(appDetails);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = ActionButton(L("取消", "Cancel"));
        var save = ActionButton(L("保存", "Save"), primary: true);
        cancel.Click += (_, _) => EndEdit();
        save.Click += (_, _) => SaveDraft();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        _editor.Children.Add(footer);

        foreach (var category in AvailableCatalogItems()
                     .Select(item => Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish)
                     .Distinct(StringComparer.Ordinal))
        {
            _category.Items.Add(category);
        }
        _category.SelectionChanged += (_, _) => PopulateActions();
        _action.SelectionChanged += (_, _) => ConfigureStepInput();
        _browse.Click += (_, _) => BrowseApplication();
        _addStep.Click += (_, _) => AddStep();
        if (_category.Items.Count > 0)
            _category.SelectedIndex = 0;
    }

    private void PopulateActions()
    {
        _action.Items.Clear();
        if (_category.SelectedItem is not string category)
            return;
        foreach (var item in AvailableCatalogItems().Where(item =>
                     (Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish) == category))
        {
            _action.Items.Add(new ComboBoxItem
            {
                Content = Runtime.IsChinese
                    ? item.NameChinese
                    : item.NameEnglish,
                Tag = item.Kind
            });
        }
        if (_action.Items.Count > 0)
            _action.SelectedIndex = 0;
    }

    private void ConfigureStepInput()
    {
        _option.Items.Clear();
        _value.Text = string.Empty;
        _arguments.Text = string.Empty;
        if (_action.SelectedItem is not ComboBoxItem
            {
                Tag: AutomationStepKind kind
            })
            return;
        var metadata = AutomationStepCatalog.Metadata(kind);
        _option.Visibility = metadata.InputKind == AutomationStepInputKind.Options
            ? Visibility.Visible
            : Visibility.Collapsed;
        _value.Visibility = metadata.InputKind is
                AutomationStepInputKind.Integer or
                AutomationStepInputKind.ApplicationPath
            ? Visibility.Visible
            : Visibility.Collapsed;
        _browse.Visibility = metadata.InputKind ==
            AutomationStepInputKind.ApplicationPath
            ? Visibility.Visible
            : Visibility.Collapsed;
        _arguments.Visibility = _browse.Visibility;
        _argumentsLabel.Visibility = _browse.Visibility;
        foreach (var option in AutomationStepCatalog.Options(kind, Runtime))
        {
            _option.Items.Add(new ComboBoxItem
            {
                Content = Runtime.IsChinese
                    ? option.Chinese
                    : option.English,
                Tag = option.Value
            });
        }
        if (_option.Items.Count > 0)
            _option.SelectedIndex = 0;
        if (metadata.InputKind == AutomationStepInputKind.Integer)
            _value.Text = "0.5";
    }

    private void BrowseApplication()
    {
        var dialog = new OpenFileDialog
        {
            Filter = L(
                "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                "Applications (*.exe)|*.exe|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _value.Text = dialog.FileName;
    }

    private void AddStep()
    {
        if (_draft is null ||
            _action.SelectedItem is not ComboBoxItem
            {
                Tag: AutomationStepKind kind
            })
            return;
        var metadata = AutomationStepCatalog.Metadata(kind);
        var value = metadata.InputKind switch
        {
            AutomationStepInputKind.None => string.Empty,
            AutomationStepInputKind.Options =>
                (_option.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                string.Empty,
            _ => _value.Text.Trim()
        };
        if (metadata.InputKind != AutomationStepInputKind.None &&
            string.IsNullOrWhiteSpace(value))
        {
            _status.Text = L("请填写步骤参数。", "Enter a value for the step.");
            return;
        }
        if (kind == AutomationStepKind.Delay &&
            (!double.TryParse(
                 value,
                 System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture,
                 out var seconds) ||
             seconds is < 0 or > 86400))
        {
            _status.Text = L(
                "延迟必须是 0–86400 秒的整数。",
                "Delay must be an integer from 0 to 86400 seconds.");
            return;
        }
        var newStep = new AutomationStep
        {
            Kind = kind,
            Value = value,
            SecondaryValue = kind == AutomationStepKind.OpenApplication
                ? _arguments.Text.Trim()
                : string.Empty
        };
        if (_editingStepIndex is { } editingIndex &&
            editingIndex >= 0 && editingIndex < _draft.Steps.Count)
        {
            _draft.Steps[editingIndex] = newStep;
            ResetStepEditor();
            _status.Text = string.Empty;
            RenderSteps();
            return;
        }
        if (_draft.Steps.Count > 0)
        {
            _draft.Steps.Add(new AutomationStep
            {
                Kind = AutomationStepKind.Delay,
                Value = "0.5"
            });
        }
        _draft.Steps.Add(newStep);
        _status.Text = string.Empty;
        RenderSteps();
    }

    private void BeginEdit(AutomationDefinition? source)
    {
        _draft = source is null
            ? new AutomationDefinition
            {
                Name = UniqueDefinitionNames.Create(
                    L("新自动化", "New automation"),
                    Runtime.Settings.Automations.Select(item => item.Name))
            }
            : Clone(source);
        _name.Text = _draft.Name;
        foreach (var pair in _triggerToggles)
            pair.Value.IsChecked = _draft.Triggers.Contains(pair.Key);
        ResetStepEditor();
        SetAutomationExpanded(true);
        _editorCard.Visibility = Visibility.Visible;
        RenderSteps();
    }

    private void EndEdit()
    {
        _draft = null;
        ResetStepEditor();
        _editorCard.Visibility = Visibility.Collapsed;
        _stepRows.Children.Clear();
        _status.Text = string.Empty;
    }

    private void SetAutomationExpanded(bool expanded)
    {
        _automationDetails.Visibility = expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        _automationExpand.Content = expanded
            ? L("收起", "Collapse")
            : L("展开", "Expand");
    }

    private IEnumerable<AutomationStepMetadata> AvailableCatalogItems() =>
        AutomationStepCatalog.Items.Where(item =>
            item.Kind != AutomationStepKind.RunMacro ||
            Runtime.Settings.Macros.Count > 0);

    private void OnMacroChanged(object? sender, EventArgs args)
    {
        var selectedCategory = _category.SelectedItem as string;
        _category.Items.Clear();
        foreach (var category in AvailableCatalogItems()
                     .Select(item => Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish)
                     .Distinct(StringComparer.Ordinal))
        {
            _category.Items.Add(category);
        }
        _category.SelectedItem = selectedCategory is not null &&
                                 _category.Items.Contains(selectedCategory)
            ? selectedCategory
            : _category.Items.Count > 0
                ? _category.Items[0]
                : null;
        PopulateActions();
    }

    private void SaveDraft()
    {
        if (_draft is null)
            return;
        var name = _name.Text.Trim();
        if (name.Length == 0 || _draft.Steps.Count == 0)
        {
            _status.Text = L(
                "名称不能为空，并且至少需要一个步骤。",
                "Enter a name and add at least one step.");
            return;
        }
        if (Runtime.Settings.Automations.Any(item =>
                !item.Id.Equals(
                    _draft.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _status.Text = L(
                "自动化名称不能与现有自动化重复。",
                "Automation names must be unique.");
            return;
        }
        _draft.Name = name;
        _draft.Triggers = _triggerToggles
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToList();
        var values = Runtime.Settings.Automations
            .Where(item => !item.Id.Equals(
                _draft.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .Append(Clone(_draft))
            .ToArray();
        if (!Runtime.TrySaveAutomations(values, out var error))
        {
            _status.Text = L("保存失败：", "Save failed: ") + error;
            return;
        }
        EndEdit();
        RenderAutomations();
    }

    private void RenderAutomations()
    {
        _automationRows.Children.Clear();
        foreach (var automation in Runtime.Settings.Automations)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var run = ActionButton(L("运行", "Run"), primary: true);
            var edit = ActionButton(L("编辑", "Edit"));
            var delete = ActionButton(L("删除", "Delete"), danger: true);
            run.Click += async (_, _) => await RunAsync(automation, run);
            edit.Click += (_, _) => BeginEdit(automation);
            delete.Click += (_, _) => Delete(automation);
            actions.Children.Add(run);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            _automationRows.Children.Add(SettingRow(
                automation.Name,
                L(
                    $"{automation.Steps.Count} 个步骤 · {automation.Triggers.Count} 个事件",
                    $"{automation.Steps.Count} step(s) · {automation.Triggers.Count} trigger(s)"),
                actions));
        }
        if (_automationRows.Children.Count == 0)
        {
            _automationRows.Children.Add(new TextBlock
            {
                Text = L(
                    "尚未定义自动化。",
                    "No automations have been defined."),
                Foreground = Brush(Palette.Muted),
                Margin = new Thickness(2, 4, 2, 4)
            });
        }
    }

    private void RenderSteps()
    {
        _stepRows.Children.Clear();
        if (_draft is null)
            return;
        for (var index = 0; index < _draft.Steps.Count; index++)
        {
            var currentIndex = index;
            var step = _draft.Steps[index];
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var up = ActionButton("↑");
            var down = ActionButton("↓");
            var delete = ActionButton("−", danger: true);
            var edit = ActionButton(L("编辑", "Edit"));
            up.IsEnabled = index > 0;
            down.IsEnabled = index < _draft.Steps.Count - 1;
            up.Click += (_, _) => MoveStep(currentIndex, -1);
            down.Click += (_, _) => MoveStep(currentIndex, 1);
            edit.Click += (_, _) => BeginStepEdit(currentIndex);
            delete.Click += (_, _) =>
            {
                _draft.Steps.RemoveAt(currentIndex);
                RenderSteps();
            };
            actions.Children.Add(up);
            actions.Children.Add(down);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            var metadata = AutomationStepCatalog.Metadata(step.Kind);
            _stepRows.Children.Add(SettingRow(
                $"{index + 1}. {AutomationStepCatalog.DisplayName(step, Runtime)}",
                Runtime.IsChinese
                    ? metadata.CategoryChinese
                    : metadata.CategoryEnglish,
                actions));
        }
    }

    private void MoveStep(int index, int offset)
    {
        if (_draft is null)
            return;
        var target = index + offset;
        if (target < 0 || target >= _draft.Steps.Count)
            return;
        (_draft.Steps[index], _draft.Steps[target]) =
            (_draft.Steps[target], _draft.Steps[index]);
        RenderSteps();
    }

    private void BeginStepEdit(int index)
    {
        if (_draft is null || index < 0 || index >= _draft.Steps.Count)
            return;
        var step = _draft.Steps[index];
        var metadata = AutomationStepCatalog.Metadata(step.Kind);
        _editingStepIndex = index;
        _category.SelectedItem = Runtime.IsChinese
            ? metadata.CategoryChinese
            : metadata.CategoryEnglish;
        _action.SelectedItem = _action.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, step.Kind));
        ConfigureStepInput();
        if (metadata.InputKind == AutomationStepInputKind.Options)
        {
            _option.SelectedItem = _option.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    step.Value,
                    StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _value.Text = step.Value;
        }
        _arguments.Text = step.SecondaryValue;
        _addStep.Content = L("更新步骤", "Update step");
    }

    private void ResetStepEditor()
    {
        _editingStepIndex = null;
        _addStep.Content = L("添加步骤", "Add step");
    }

    private async Task RunAsync(AutomationDefinition automation, Button button)
    {
        button.IsEnabled = false;
        _status.Text = L(
            $"正在运行：{automation.Name}",
            $"Running: {automation.Name}");
        var result = await Runtime.RunAutomationAsync(automation.Id);
        _status.Text = result.Success
            ? L("自动化执行完成。", "Automation completed.")
            : result.Error;
        button.IsEnabled = true;
    }

    private void Delete(AutomationDefinition automation)
    {
        if (MessageBox.Show(
                Window.GetWindow(this),
                L(
                    $"删除自动化“{automation.Name}”？",
                    $"Delete automation “{automation.Name}”?"),
                L("删除自动化", "Delete automation"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var remaining = Runtime.Settings.Automations
            .Where(item => !item.Id.Equals(
                automation.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .ToArray();
        _status.Text = Runtime.TrySaveAutomations(remaining, out var error)
            ? string.Empty
            : L("删除失败：", "Delete failed: ") + error;
        RenderAutomations();
    }

    private static AutomationDefinition Clone(AutomationDefinition source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Steps = source.Steps.Select(step => step with { }).ToList(),
            Triggers = source.Triggers.ToList()
        };

    private string TriggerName(AutomationTriggerKind trigger) => trigger switch
    {
        AutomationTriggerKind.AcAdapterConnected =>
            L("电源适配器插入", "AC adapter connected"),
        AutomationTriggerKind.AcAdapterDisconnected =>
            L("电源适配器断开", "AC adapter disconnected"),
        AutomationTriggerKind.GameStarted => L("游戏开启", "Game started"),
        AutomationTriggerKind.GameStopped => L("游戏结束", "Game stopped"),
        _ => trigger.ToString()
    };

    private sealed class AutomationViewModel : ToolkitViewModelBase
    {
        public AutomationViewModel(ToolkitRuntimeService runtime)
            : base(runtime)
        {
        }
    }

    public override void Dispose()
    {
        Runtime.MacroChanged -= OnMacroChanged;
        _macroPanel.Dispose();
        base.Dispose();
    }
}
