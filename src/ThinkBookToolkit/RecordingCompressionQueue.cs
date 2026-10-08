using System;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed class RecordingCompressionQueue
{
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private readonly Func<string, string> _compress;
    internal RecordingCompressionQueue(Func<string, string>? compress = null)
        => _compress = compress ?? SensorRecordingArchive.CompressAndDeleteSource;
    internal Task Pending { get { lock (_gate) return _pending; } }
    internal void Enqueue(string path, Action<string>? completed = null)
    {
        lock (_gate)
        {
            var previous = _pending;
            _pending = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    var archive = _compress(path);
                    completed?.Invoke(archive);
                }
                catch (Exception ex)
                {
                    // Source is deleted only after successful compression. A
                    // process exit or failure leaves it available next startup.
                    ToolkitLog.Error("Recording compression failed; source retained: " + path, ex);
                }
            });
        }
    }
}
