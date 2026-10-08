using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed class KeyboardBacklightStartupRestore
{
    private int _checked;

    internal async Task ApplyOnceAsync(AppSettings settings, bool supported,
        Func<KeyboardBacklightState>? read = null,
        Func<KeyboardBacklightLevel, KeyboardBacklightState>? write = null)
    {
        if (Interlocked.Exchange(ref _checked, 1) != 0 || !supported ||
            !settings.RestoreKeyboardBacklightOnStartup || settings.LastKeyboardBacklightLevel is not { } target || !Enum.IsDefined(target))
            return;
        read ??= KeyboardBacklightController.ReadState;
        write ??= KeyboardBacklightController.SetBrightness;
        await Task.Run(() =>
        {
            var current = read();
            if (current.Level == target) return;
            if (!current.Level.HasValue) throw new InvalidOperationException("Keyboard backlight brightness is unavailable.");
            if (write(target).Level != target)
                throw new InvalidOperationException("Hardware did not confirm the requested keyboard backlight brightness.");
        }).ConfigureAwait(false);
    }
}
