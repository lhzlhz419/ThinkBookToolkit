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
        // The latest-ending entitlement can be a future extension.
        var state = today > endDate ? WarrantyState.Expired : WarrantyState.InWarranty;

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
            return Path.Combine(ToolkitStoragePaths.Configuration, "warranty_cache.csharp.json");
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
                            cached.SchemaVersion == 5 &&
                            string.Equals(
                                cached.SerialHash,
                                serialHash,
                                StringComparison.OrdinalIgnoreCase) &&
                            cached.TryGetDates(out _, out _);

        if (matchingCache && cached!.IsFromToday())
        {
            cached.TryGetDates(out var cachedStart, out var cachedEnd);
            return WarrantySnapshot.FromDates(
                cachedStart,
                cachedEnd,
                entitlements: cached.Entitlements ?? []);
        }

        try
        {
            var result = await FetchChinaWarrantyAsync(
                serialNumber,
                cancellationToken);
            await SaveCacheAsync(
                new WarrantyCacheEntry
                {
                    SchemaVersion = 5,
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
        var serial = Uri.EscapeDataString(serialNumber.Trim());
        var machine = await HttpClient.GetStringAsync(
            "https://newsupport.lenovo.com.cn/api/machine/getmachineinfo?sn=" + serial, cancellationToken);
        using var info = JsonDocument.Parse(machine);
        var data = info.RootElement.GetProperty("data");
        var channel = Text(data, "SalePath");
        if (channel == "中小企业" && Text(data, "SaleArea") == "00")
            throw new InvalidOperationException("Lenovo does not publish a main warranty period for this sales area.");
        var result = ParseChinaWarranty(json, channel);
        if (channel == "消费")
        {
            // deviceGuarantee.js applies a published exception list to consumer summaries.
            var exceptions = await HttpClient.GetStringAsync("https://newsupport.lenovo.com.cn/js/snArr.js", cancellationToken);
            if (exceptions.Contains("\"" + serialNumber + "\"", StringComparison.Ordinal) ||
                exceptions.Contains("'" + serialNumber + "'", StringComparison.Ordinal))
                result = result with { StartDate = new DateOnly(2024, 2, 28), EndDate = new DateOnly(2026, 2, 28) };
        }
        ToolkitLog.Info(
            $"Warranty information loaded from Lenovo China API: " +
            $"entitlements={result.Entitlements.Count}; " +
            $"end={result.EndDate:yyyy-MM-dd}.");
        return result;
    }

    internal static WarrantyQueryResult ParseChinaWarranty(string json, string channel)
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

        if (!TryReadMainServicePeriod(data, channel, out var summaryStart, out var summaryEnd))
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

    // The official deviceGuarantee page groups detail rows differently for each SalePath.
    private static bool TryReadMainServicePeriod(JsonElement data, string channel, out DateOnly startDate, out DateOnly endDate)
    {
        startDate = endDate = default;
        if (!TryGetProperty(data, "detailinfo", out var detail) || channel is not ("消费" or "商用" or "中小企业"))
            return false;

        var seen = new HashSet<(string, string)>();
        DateOnly? first = null, last = null;
        foreach (var group in new[] { "warranty", "onsite", "other" })
        {
            if (!TryGetProperty(detail, group, out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray())
            {
                var number = Text(row, "ServiceProductNumber");
                if (number.Length > 0 && !seen.Add((number, Text(row, "StartDate")))) continue;
                var name = Text(row, "ServiceProductName").Replace("\r\n", "");
                var category = Text(row, "ServiceProductSmallClass").Replace("\r\n", "");
                if (!QualifiesForMainWarranty(channel, name, category)) continue;
                DateOnly? MainDate(string part, string labor) =>
                    channel == "中小企业" || TryGetProperty(row, part, out var field) && field.ValueKind != JsonValueKind.Null
                        ? OptionalDate(row, part) : OptionalDate(row, labor);
                if (MainDate("PartStartDate", "LaborStartDate") is { } start && (!first.HasValue || start < first)) first = start;
                if (MainDate("PartEndDate", "LaborEndDate") is { } end && (!last.HasValue || end > last)) last = end;
            }
        }
        if (first is null || last is null || last < first) return false;
        startDate = first.Value;
        endDate = last.Value;
        return true;
    }

    private static bool QualifiesForMainWarranty(string channel, string name, string category)
    {
        if (name.Contains("部件加装") || name.Contains("保外维修") || channel == "中小企业" && name.Contains("意外保")) return false;
        if (category is "基础服务" or "EB延保类" or "延保类服务" or "上门类" or "延保及上门类服务") return true;
        if (channel == "消费")
            return category is "EB上门类" or "延保类" or "部件延保及上门类服务" or "SB延保类";
        if (category == "EB延保及上门类" || name is "TCM分体台式尊享服务极速版" or "TCM一体台式尊享服务极速版" or
            "ThinkStation P3尊享服务极速版" or "TP尊享服务订阅基础版" or "TP尊享服务极速版" or "TS尊享服务订阅基础版" or
            "XC DT尊享服务极速版" or "XC NB尊享服务极速版" or "启天分体台式尊享服务极速版" or "启天一体台式尊享服务极速版" or
            "商用DT尊享服务订阅基础版" or "商用NB尊享服务订阅基础版" or "商用TB尊享服务订阅基础版" or
            "商用笔记本尊享服务极速版（含一诺闪修、7x24电话和3年1次数据拯救服务）" or "商用平板尊享服务极速版" or
            "扬天台式机三年（有限）保修（上门）") return true;
        return channel == "中小企业" && category is ("寄修类" or "上门区域扩展类-城市扩展" or "部件延保及上门类服务" or
            "电池延保及上门类服务" or "闪修类服务" or "4H闪修类服务" or "意外保" or "部件不回收类" or
            "事前数据恢复服务" or "电话服务支持时间变更类" or "远程电话支持服务" or "联想软件服务" or
            "快速响应及修复类" or "安装验机类" or "安装部署服务" or "基础硬件安装服务" or "系统安装服务" or "硬件加装" or "大客户第三方软件支持");
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
            startDate <= endDate;
        }
    }

    internal sealed record WarrantyQueryResult(
        DateOnly StartDate,
        DateOnly EndDate,
        IReadOnlyList<WarrantyEntitlement> Entitlements);
}
