using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using ThinkBookToolkit.PluginApi;

if (args.Length != 3) return 2;
try
{
    using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5000);
    var context = new PluginAssemblyContext(Path.GetFullPath(args[1]));
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
    await using var plugin = Activator.CreateInstance(assembly.GetType(args[2], throwOnError: true)!) as IToolkitPlugin
        ?? throw new InvalidDataException("Entry type does not implement IToolkitPlugin.");
    await PluginWire.WriteAsync(pipe, "TBT_PLUGIN_1", CancellationToken.None);
    while (pipe.IsConnected)
    {
        var request = await PluginWire.ReadAsync<PluginRequest>(pipe, CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await plugin.EvaluateAsync(request, timeout.Token);
            await PluginWire.WriteAsync(pipe, new PluginResponse(true, result), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PluginWire.WriteAsync(pipe, new PluginResponse(false, Error: ex.GetBaseException().Message), CancellationToken.None);
        }
    }
    return 0;
}
catch { return 1; }

sealed class PluginAssemblyContext(string entry) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(entry);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IToolkitPlugin).Assembly.GetName().Name) return typeof(IToolkitPlugin).Assembly;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
