using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ThinkBookToolkit;

internal sealed record NvPcfRequest(string Operation,
    PowerSettingsState? State = null, PowerSettingsLockSelection? Selection = null);

internal sealed record NvPcfResponse(bool Success, NvPcfPowerSnapshot? Snapshot = null,
    string? Error = null);

internal static class NvPcfWorker
{
    internal const string Ready = "NVPCF_READY_1";

    // The injected handler is used by process-boundary tests without loading
    // NVAPI or changing any real power settings.
    internal static int Run(string pipeName, Func<NvPcfRequest, NvPcfResponse>? handler = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName != Path.GetFileName(pipeName))
            return 2;
        if (handler is null)
            NvPcfPowerController.EnableNativeWorker();
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(5000);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(Ready);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line == "EXIT") return 0;
                NvPcfResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<NvPcfRequest>(line)
                        ?? throw new InvalidDataException("Empty NVPCF request.");
                    response = (handler ?? Execute)(request);
                }
                catch (Exception ex)
                {
                    // Ordinary driver errors are reported. Fatal native
                    // faults cannot be caught; only this worker will exit.
                    response = new(false, Error: ex.GetBaseException().Message);
                }
                writer.WriteLine(JsonSerializer.Serialize(response));
                if (!response.Success) return 1; // Never reuse a failed driver session.
            }
            return 0;
        }
        catch { return 1; }
        finally
        {
            if (handler is null)
                NvPcfPowerController.Shutdown();
        }
    }

    private static NvPcfResponse Execute(NvPcfRequest request)
    {
        switch (request.Operation)
        {
            case "READ": return new(true, NvPcfPowerController.Read());
            case "WRITE":
                return new(true, NvPcfPowerController.WriteAndRead(
                    request.State ?? throw new InvalidDataException("Missing power settings."), request.Selection));
            case "RESET": NvPcfPowerController.ResetToDefaults(); return new(true);
            case "RESET_POWER": NvPcfPowerController.ResetAllPowerOverrides(); return new(true);
            default: throw new InvalidDataException("Unknown NVPCF operation.");
        }
    }
}
