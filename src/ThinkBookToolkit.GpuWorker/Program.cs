namespace ThinkBookToolkit.GpuWorker;

internal static class WorkerProgram
{
    // Execute the existing isolated protocol without starting the UIAccess
    // apphost, which CreateProcess rejects with ERROR_ELEVATION_REQUIRED.
    [System.STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 ||
            !(string.Equals(args[0], "--gpu-worker", System.StringComparison.OrdinalIgnoreCase) ||
              string.Equals(args[0], "--nvpcf-worker", System.StringComparison.OrdinalIgnoreCase)))
            return 2;

        // This host must enter the isolated protocol directly. Calling the
        // WPF application's Main method makes the worker inherit unrelated
        // UI startup behavior and can cause the host to terminate before the
        // named-pipe handshake.
        if (!ThinkBookToolkit.Guardian.GuardianEntryPoint.TryRun(args))
            return 2;

        return System.Environment.ExitCode;
    }
}
