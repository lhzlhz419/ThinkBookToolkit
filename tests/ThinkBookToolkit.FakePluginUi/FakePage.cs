using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit.PluginUi;

namespace ThinkBookToolkit.Tests.Ui;

public class FakePage : IToolkitPluginPage
{
    protected readonly StackPanel Root = new();
    private readonly TextBlock _value = new() { FontSize = 20 };
    public FrameworkElement CreateView(IPluginPageContext context)
    {
        Root.Children.Add(new TextBlock { Text = "插件自行绘制的页面", FontSize = 26 });
        Root.Children.Add(_value);
        Root.Resources["context"] = context;
        Root.Tag = "alive";
        return Root;
    }
    public virtual void Update(PluginPageState state) => _value.Text = "Value: " + state.Request.PluginSettings["value"].GetString();
    public void Dispose() => Root.Tag = "disposed";
}
public sealed class FailingUpdatePage : FakePage
{
    private int _updates;
    public override void Update(PluginPageState state)
    {
        if (++_updates > 1) throw new InvalidOperationException("Test UI update failure");
        base.Update(state);
    }
}
public sealed class FailingCreatePage : IToolkitPluginPage
{
    public FrameworkElement CreateView(IPluginPageContext context) => throw new InvalidOperationException("Test UI create failure");
    public void Update(PluginPageState state) { }
    public void Dispose() { }
}
