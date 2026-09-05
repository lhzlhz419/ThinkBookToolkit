using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
    public IReadOnlyList<WarrantyEntitlement> Entitlements { get; init; } = [];

    public int RemainingDays => EndDate.HasValue
        ? Math.Max(
            0,
            EndDate.Value.DayNumber -
            DateOnly.FromDateTime(DateTime.Now).DayNumber)
        : 0;

    public static WarrantySnapshot FromDates(
        DateOnly startDate,
        DateOnly endDate,
        bool isStale = false,
        string? error = null,
        IReadOnlyList<WarrantyEntitlement>? entitlements = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var state = today < startDate
            ? WarrantyState.NotStarted
            : today > endDate
                ? WarrantyState.Expired
                : WarrantyState.InWarranty;

        var progress = CalculateProgress(startDate, endDate, today);
        return new(startDate, endDate, state, progress, isStale, error)
        {
            Entitlements = entitlements ?? []
        };
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

internal sealed record WarrantyEntitlement(
    string Category,
    string Name,
    string ProductNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    string SmallClass,
    string Remark,
    DateOnly? PartStartDate,
    DateOnly? PartEndDate,
    DateOnly? LaborStartDate,
    DateOnly? LaborEndDate,
    DateOnly? OnSiteStartDate,
    DateOnly? OnSiteEndDate);

internal static class WarrantyService
{
    private const string ChinaWarrantyEndpoint =
        "https://newsupport.lenovo.com.cn/api/drive/";

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
    private static readonly SemaphoreSlim QueryGate = new(1, 1);

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
        await QueryGate.WaitAsync(cancellationToken);
        try
        {
            return await GetWarrantyCoreAsync(cancellationToken);
        }
        finally
        {
            QueryGate.Release();
        }
    }

    private static async Task<WarrantySnapshot> GetWarrantyCoreAsync(
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
                            cached.SchemaVersion >= 3 &&
                            string.Equals(
                                cached.SerialHash,
                                serialHash,
                                StringComparison.OrdinalIgnoreCase) &&
                            cached.TryGetDates(out _, out _);

        if (matchingCache &&
            cached!.IsFromToday() &&
            cached.SchemaVersion >= 3)
        {
            cached.TryGetDates(out var cachedStart, out var cachedEnd);
            return WarrantySnapshot.FromDates(
                cachedStart,
                cachedEnd,
                entitlements: cached.Entitlements ?? []);
        }

        try
        {
            var result = await FetchWarrantyAsync(
                serialNumber,
                cancellationToken);
            await SaveCacheAsync(
                new WarrantyCacheEntry
                {
                    SchemaVersion = 3,
                    SerialHash = serialHash,
                    StartDate = result.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    EndDate = result.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Entitlements = result.Entitlements.ToList(),
                    StoredAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
            return WarrantySnapshot.FromDates(
                result.StartDate,
                result.EndDate,
                entitlements: result.Entitlements);
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
                    error: ex.Message,
                    entitlements: cached.Entitlements ?? []);
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

    private static async Task<WarrantyQueryResult>
        FetchWarrantyAsync(
            string serialNumber,
            CancellationToken cancellationToken)
    {
        Exception? chinaError = null;
        try
        {
            return await FetchChinaWarrantyAsync(
                serialNumber,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            chinaError = ex;
            ToolkitLog.Warning(
                "Lenovo China warranty query failed: " + ex.Message);
        }

        try
        {
            var dates = await FetchLenovoSupportWarrantyAsync(
                serialNumber,
                cancellationToken);
            ToolkitLog.Info("Warranty information loaded from Lenovo Support API.");
            return new WarrantyQueryResult(dates.StartDate, dates.EndDate, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception supportError)
        {
            throw new InvalidOperationException(
                "Lenovo did not return warranty information for this device. " +
                $"China: {chinaError.Message} Support: {supportError.Message}");
        }
    }

    private static async Task<WarrantyQueryResult>
        FetchChinaWarrantyAsync(
        string serialNumber,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            ChinaWarrantyEndpoint +
            Uri.EscapeDataString(serialNumber.Trim()) +
            "/drivewarrantyinfo");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ParseChinaWarranty(json);
        ToolkitLog.Info(
            $"Warranty information loaded from Lenovo China API: " +
            $"entitlements={result.Entitlements.Count}; " +
            $"end={result.EndDate:yyyy-MM-dd}.");
        return result;
    }

    internal static WarrantyQueryResult ParseChinaWarranty(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetProperty(root, "statusCode", out var status) ||
            !status.TryGetInt32(out var statusCode) ||
            statusCode != 200 ||
            !TryGetProperty(root, "data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Lenovo China did not return valid warranty data.");
        }

        if (!TryReadBaseSummary(data, out var summaryStart, out var summaryEnd))
        {
            throw new InvalidOperationException(
                "Lenovo China returned no valid base warranty summary.");
        }

        var items = new List<WarrantyEntitlement>();
        if (TryGetProperty(data, "detailinfo", out var detail))
        {
            AddEntitlements(items, detail, "warranty", "Warranty");
            AddEntitlements(items, detail, "onsite", "On-site");
            AddEntitlements(items, detail, "other", "Other");
        }
        var distinct = items
            .DistinctBy(item => new
            {
                item.Name,
                item.ProductNumber,
                item.StartDate,
                item.EndDate
            })
            .OrderBy(item => item.StartDate)
            .ThenBy(item => item.EndDate)
            .ToArray();
        return new WarrantyQueryResult(
            summaryStart,
            summaryEnd,
            distinct);
    }

    private static bool TryReadBaseSummary(
        JsonElement data,
        out DateOnly startDate,
        out DateOnly endDate)
    {
        startDate = default;
        endDate = default;
        if (!TryGetProperty(data, "baseinfo", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var starts = new List<DateOnly>();
        var ends = new List<DateOnly>();
        foreach (var value in values.EnumerateArray())
        {
            AddDate(starts, OptionalDate(value, "StartDate"));
            AddDate(starts, OptionalDate(value, "PartStartDate"));
            AddDate(starts, OptionalDate(value, "LaborStartDate"));
            AddDate(starts, OptionalDate(value, "OnSiteStartDate"));
            AddDate(ends, OptionalDate(value, "EndDate"));
        }

        if (starts.Count == 0 || ends.Count == 0)
            return false;
        startDate = starts.Min();
        endDate = ends.Max();
        return startDate < endDate;
    }

    private static void AddDate(
        ICollection<DateOnly> target,
        DateOnly? value)
    {
        if (value.HasValue)
            target.Add(value.Value);
    }

    private static void AddEntitlements(
        ICollection<WarrantyEntitlement> target,
        JsonElement parent,
        string property,
        string category)
    {
        if (!TryGetProperty(parent, property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (!TryReadDate(value, "StartDate", out var start) ||
                !TryReadDate(value, "EndDate", out var end) ||
                start >= end)
                continue;
            target.Add(new WarrantyEntitlement(
                category,
                Text(value, "ServiceProductName"),
                Text(value, "ServiceProductNumber"),
                start,
                end,
                Text(value, "ServiceProductSmallClass"),
                Text(value, "Remark"),
                OptionalDate(value, "PartStartDate"),
                OptionalDate(value, "PartEndDate"),
                OptionalDate(value, "LaborStartDate"),
                OptionalDate(value, "LaborEndDate"),
                OptionalDate(value, "OnSiteStartDate"),
                OptionalDate(value, "OnSiteEndDate")));
        }
    }

    private static string Text(JsonElement value, string property) =>
        TryGetProperty(value, property, out var item) &&
        item.ValueKind == JsonValueKind.String
            ? item.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool TryReadDate(
        JsonElement value,
        string property,
        out DateOnly date)
    {
        date = default;
        return TryGetProperty(value, property, out var item) &&
               item.ValueKind == JsonValueKind.String &&
               TryParseDate(item.GetString(), out date);
    }

    private static DateOnly? OptionalDate(
        JsonElement value,
        string property) =>
        TryReadDate(value, property, out var date) ? date : null;

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
        public int SchemaVersion { get; set; }
        public string SerialHash { get; set; } = string.Empty;
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public DateTimeOffset StoredAtUtc { get; set; }
        public List<WarrantyEntitlement> Entitlements { get; set; } = [];

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

    internal sealed record WarrantyQueryResult(
        DateOnly StartDate,
        DateOnly EndDate,
        IReadOnlyList<WarrantyEntitlement> Entitlements);
}
