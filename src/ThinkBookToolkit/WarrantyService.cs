using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal enum WarrantyState
{
    Unavailable,
    NotStarted,
    InWarranty,
    Expired
}

internal sealed record WarrantySnapshot(
    DateOnly? StartDate,
    DateOnly? EndDate,
    WarrantyState State,
    int ProgressPercentage,
    bool IsStale,
    string? Error)
{
    public static WarrantySnapshot FromDates(
        DateOnly startDate,
        DateOnly endDate,
        bool isStale = false,
        string? error = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var state = today < startDate
            ? WarrantyState.NotStarted
            : today > endDate
                ? WarrantyState.Expired
                : WarrantyState.InWarranty;

        var progress = CalculateProgress(startDate, endDate, today);
        return new(startDate, endDate, state, progress, isStale, error);
    }

    public static WarrantySnapshot Unavailable(string? error = null) =>
        new(null, null, WarrantyState.Unavailable, 0, false, error);

    internal static int CalculateProgress(
        DateOnly startDate,
        DateOnly endDate,
        DateOnly today)
    {
        if (today <= startDate)
            return 0;
        if (today >= endDate)
            return 100;

        var totalDays = endDate.DayNumber - startDate.DayNumber;
        if (totalDays <= 0)
            return today >= endDate ? 100 : 0;

        var elapsedDays = today.DayNumber - startDate.DayNumber;
        return Math.Clamp(
            (int)Math.Floor(elapsedDays * 100.0 / totalDays + 0.5),
            0,
            100);
    }
}

internal static class WarrantyService
{
    private const string BaiyingWarrantyEndpoint =
        "https://paas.lenovo.com.cn/qrcode-server/account/getSnInfo?sn=";

    private const string LenovoSupportWarrantyEndpoint =
        "https://supportapi.lenovo.com/v2.5/warranty?serial=";

    // Public client identifier embedded in Lenovo's own warranty client.
    // It identifies the calling application and is not a user credential.
    private const string LenovoSupportClientId = "F/YmpVl7yDfUc0gDqXHHYQ==";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static string CachePath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(
                home,
                ".thinkbook_toolkit",
                "warranty_cache.csharp.json");
        }
    }

    public static async Task<WarrantySnapshot> GetWarrantyAsync(
        CancellationToken cancellationToken)
    {
        string serialNumber;
        try
        {
            serialNumber = await Task.Run(
                ReadSerialNumber,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WarrantySnapshot.Unavailable(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(serialNumber))
            return WarrantySnapshot.Unavailable("Device serial number is unavailable.");

        var serialHash = HashSerialNumber(serialNumber);
        var cached = await ReadCacheAsync(cancellationToken);
        var matchingCache = cached is not null &&
                            string.Equals(
                                cached.SerialHash,
                                serialHash,
                                StringComparison.OrdinalIgnoreCase) &&
                            cached.TryGetDates(out _, out _);

        if (matchingCache && cached!.IsFromToday())
        {
            cached.TryGetDates(out var cachedStart, out var cachedEnd);
            return WarrantySnapshot.FromDates(cachedStart, cachedEnd);
        }

        try
        {
            var (startDate, endDate) = await FetchWarrantyAsync(
                serialNumber,
                cancellationToken);
            await SaveCacheAsync(
                new WarrantyCacheEntry
                {
                    SerialHash = serialHash,
                    StartDate = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    EndDate = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StoredAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
            return WarrantySnapshot.FromDates(startDate, endDate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (matchingCache &&
                cached!.TryGetDates(out var cachedStart, out var cachedEnd))
            {
                return WarrantySnapshot.FromDates(
                    cachedStart,
                    cachedEnd,
                    isStale: true,
                    error: ex.Message);
            }

            return WarrantySnapshot.Unavailable(ex.Message);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ThinkBookToolkit/1.0");
        return client;
    }

    private static async Task<(DateOnly StartDate, DateOnly EndDate)>
        FetchWarrantyAsync(
            string serialNumber,
            CancellationToken cancellationToken)
    {
        Exception? baiyingError = null;
        try
        {
            return await FetchBaiyingWarrantyAsync(
                serialNumber,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            baiyingError = ex;
        }

        try
        {
            return await FetchLenovoSupportWarrantyAsync(
                serialNumber,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception supportError)
        {
            throw new InvalidOperationException(
                "Lenovo did not return warranty information for this device. " +
                $"Baiying: {baiyingError.Message} Support: {supportError.Message}");
        }
    }

    private static async Task<(DateOnly StartDate, DateOnly EndDate)>
        FetchBaiyingWarrantyAsync(
            string serialNumber,
            CancellationToken cancellationToken)
    {
        var uri = new Uri(
            BaiyingWarrantyEndpoint +
            Uri.EscapeDataString(serialNumber.Trim()) +
            "&userAuth=");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://iknow.lenovo.com.cn/");
        request.Headers.TryAddWithoutValidation(
            "Origin",
            "https://iknow.lenovo.com.cn");

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!TryGetProperty(root, "data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            var message = TryGetProperty(root, "message", out var error)
                ? error.GetString()
                : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "Baiying returned an invalid response."
                    : message);
        }

        if (!TryGetProperty(data, "warrantyStartDate", out var startValue) ||
            !TryGetProperty(data, "warrantyEndDate", out var endValue) ||
            !TryParseDate(startValue.GetString(), out var startDate) ||
            !TryParseDate(endValue.GetString(), out var endDate) ||
            startDate >= endDate)
        {
            throw new InvalidOperationException(
                "Baiying did not return a valid warranty period.");
        }

        return (startDate, endDate);
    }

    private static async Task<(DateOnly StartDate, DateOnly EndDate)>
        FetchLenovoSupportWarrantyAsync(
            string serialNumber,
            CancellationToken cancellationToken)
    {
        var uri = new Uri(
            LenovoSupportWarrantyEndpoint +
            Uri.EscapeDataString(serialNumber.Trim()));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(
            "clientid",
            LenovoSupportClientId);

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!TryGetProperty(root, "Warranty", out var warrantyRecords) ||
            warrantyRecords.ValueKind != JsonValueKind.Array)
        {
            var message = TryGetProperty(root, "ErrorMessage", out var error)
                ? error.GetString()
                : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "Lenovo did not return warranty information for this device."
                    : message);
        }

        var validRanges = warrantyRecords
            .EnumerateArray()
            .Select(ParseWarrantyRange)
            .Where(range => range.HasValue)
            .Select(range => range!.Value)
            .ToArray();

        if (validRanges.Length == 0)
        {
            throw new InvalidOperationException(
                "Lenovo did not return a valid warranty period for this device.");
        }

        // Match Lenovo Baiying's entitlement aggregation: use the latest
        // valid start and the latest valid end returned by Lenovo Support.
        return (
            validRanges.Max(range => range.StartDate),
            validRanges.Max(range => range.EndDate));
    }

    private static (DateOnly StartDate, DateOnly EndDate)? ParseWarrantyRange(
        JsonElement record)
    {
        if (!TryGetProperty(record, "Start", out var startValue) ||
            !TryGetProperty(record, "End", out var endValue) ||
            !TryParseDate(startValue.GetString(), out var startDate) ||
            !TryParseDate(endValue.GetString(), out var endDate) ||
            startDate >= endDate)
        {
            return null;
        }

        return (startDate, endDate);
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "yyyyMMdd",
            "yyyy年MM月dd日",
            "M/d/yyyy",
            "MM/dd/yyyy"
        };
        if (DateOnly.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out var dateTime) &&
               DateOnly.TryParse(
                   dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out date);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadSerialNumber()
    {
        var serialNumber = ReadWmiValue(
            "SELECT SerialNumber FROM Win32_BIOS",
            "SerialNumber");
        if (IsUsableSerialNumber(serialNumber))
            return serialNumber!.Trim();

        serialNumber = ReadRegistrySerialNumber();
        if (IsUsableSerialNumber(serialNumber))
            return serialNumber!.Trim();

        serialNumber = ReadWmiValue(
            "SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct",
            "IdentifyingNumber");
        if (IsUsableSerialNumber(serialNumber))
            return serialNumber!.Trim();

        throw new InvalidOperationException(
            "Device serial number is unavailable.");
    }

    private static string? ReadWmiValue(string query, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var value = Convert.ToString(
                        item[propertyName],
                        CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ReadRegistrySerialNumber()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS");
            return Convert.ToString(
                key?.GetValue("SystemSerialNumber"),
                CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableSerialNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return !normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("Default string", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashSerialNumber(string serialNumber)
    {
        var bytes = Encoding.UTF8.GetBytes(
            serialNumber.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task<WarrantyCacheEntry?> ReadCacheAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(
                CachePath,
                cancellationToken);
            return JsonSerializer.Deserialize<WarrantyCacheEntry>(
                json,
                CacheJsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveCacheAsync(
        WarrantyCacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(entry, CacheJsonOptions);
            await File.WriteAllTextAsync(
                CachePath,
                json,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Warranty display should still work if the local cache cannot be saved.
        }
    }

    private sealed class WarrantyCacheEntry
    {
        public string SerialHash { get; set; } = string.Empty;
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public DateTimeOffset StoredAtUtc { get; set; }

        public bool IsFromToday() =>
            StoredAtUtc.ToLocalTime().Date == DateTimeOffset.Now.Date;

        public bool TryGetDates(out DateOnly startDate, out DateOnly endDate)
        {
            startDate = default;
            endDate = default;
            return DateOnly.TryParseExact(
                StartDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out startDate) &&
            DateOnly.TryParseExact(
                EndDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out endDate) &&
            startDate < endDate;
        }
    }
}
