namespace ThinkBookToolkit;

internal static class GpuModeText
{
    public static string Name(GpuWorkingMode mode, bool isChinese) =>
        (mode, isChinese) switch
        {
            (GpuWorkingMode.Hybrid, true) => "混合模式",
            (GpuWorkingMode.IntegratedOnly, true) => "混合核显模式",
            (GpuWorkingMode.HybridAuto, true) => "混合自动模式",
            (GpuWorkingMode.Discrete, true) => "独显直连模式",
            (GpuWorkingMode.IntegratedDirect, true) => "核显直连模式",
            (GpuWorkingMode.Hybrid, false) => "Hybrid mode",
            (GpuWorkingMode.IntegratedOnly, false) => "iGPU only",
            (GpuWorkingMode.HybridAuto, false) => "Hybrid auto",
            (GpuWorkingMode.Discrete, false) => "Discrete graphics",
            (GpuWorkingMode.IntegratedDirect, false) => "Integrated graphics",
            _ => mode.ToString()
        };

    public static string Transition(
        GpuWorkingMode source,
        GpuWorkingMode target,
        bool isChinese) =>
        isChinese
            ? $"将从“{Name(source, true)}”切换到“{Name(target, true)}”"
            : $"Will switch from {Name(source, false)} to {Name(target, false)}";
}
