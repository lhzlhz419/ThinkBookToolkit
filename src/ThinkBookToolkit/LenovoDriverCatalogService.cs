using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ThinkBookToolkit;

internal sealed record DriverCatalogEntry(
    Uri DescriptorUri,
    string Category,
    string Sha256);

internal sealed record InstalledDriverSnapshot(
    IReadOnlyList<string> HardwareIds,
    string Version,
    DateTime? Date);

internal sealed record DriverSystemSnapshot(
    int WindowsBuild,
    string BiosVersion,
    IReadOnlyList<InstalledDriverSnapshot> Drivers);

internal static class LenovoDriverCatalogService
{
    private enum RuleTruth
    {
        False,
        True,
        Unknown
    }

    private const string DownloadHost = "download.lenovo.com";
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);
    private static readonly ConcurrentDictionary<string, byte> UnknownRules =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAvailable(out string detail)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.OSVersion.Version.Major < 10)
        {
            detail = "The independent Lenovo catalog scanner requires Windows 10 or later.";
            return false;
        }
        if (!TryGetMachineType(out var machineType))
        {
            detail = "无法从产品编号读取 Lenovo Machine Type";
            return false;
        }
        detail = $"内置 Lenovo 公共目录扫描器可用（Machine Type {machineType}），无需 Lenovo System Update DLL";
        return true;
    }

    public static async Task<DriverUpdateScanResult> ScanAsync(
        string language,
        CancellationToken cancellationToken)
    {
        if (!TryGetMachineType(out var machineType))
            throw new NotSupportedException(
                "Lenovo Machine Type could not be determined from the product number.");

        var stopwatch = Stopwatch.StartNew();
        var catalogUri = ValidateLenovoUri(
            new Uri($"https://{DownloadHost}/catalog/{machineType}_Win11.xml"));
        ToolkitLog.Info(
            $"Independent Lenovo driver scan started for {machineType} / Win11.");

        var snapshotTask = Task.Run(CaptureSystemSnapshot, cancellationToken);
        var catalogBytes = await DownloadBytesAsync(
            catalogUri,
            cancellationToken);
        var entries = ParseCatalog(DecodeXml(catalogBytes));
        var catalogAt = stopwatch.Elapsed;
        ToolkitLog.Info(
            $"Lenovo catalog downloaded and parsed: {entries.Count} descriptors; " +
            $"elapsed={catalogAt.TotalMilliseconds:0} ms.");

        var snapshot = await snapshotTask;
        var snapshotAt = stopwatch.Elapsed;
        ToolkitLog.Info(
            $"Driver system snapshot captured: {snapshot.Drivers.Count} devices; " +
            $"step={(snapshotAt - catalogAt).TotalMilliseconds:0} ms.");

        var tasks = entries.Select(entry => ReadCandidateAsync(
            entry,
            snapshot,
            language,
            cancellationToken));
        var candidates = (await Task.WhenAll(tasks))
            .Where(item => item is not null)
            .Cast<DriverUpdateItem>()
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var finishedAt = stopwatch.Elapsed;
        ToolkitLog.Info(
            "Independent Lenovo driver scan completed: " +
            $"{candidates.Count(item => item.IsUpdateRequired)} update(s), " +
            $"{candidates.Count(item => !item.IsUpdateRequired)} up-to-date item(s); " +
            $"descriptor step={(finishedAt - snapshotAt).TotalMilliseconds:0} ms; " +
            $"total={finishedAt.TotalMilliseconds:0} ms.");
        return new("Success", candidates);
    }

    public static async Task<DriverUpdateInstallResult> InstallAsync(
        IReadOnlyCollection<DriverUpdateItem> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
            throw new ArgumentException(
                "At least one update must be selected.",
                nameof(updates));

        var failed = new List<string>();
        var rebootNeeded = false;
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadAndInstallAsync(update, cancellationToken);
                rebootNeeded |= DriverUpdateController.RequiresRestart(
                    update.RebootType);
            }
            catch (Exception ex)
            {
                failed.Add(update.PackageId);
                ToolkitLog.Error(
                    $"Independent Lenovo update {update.PackageId} failed.",
                    ex);
            }
        }
        return new(
            failed.Count == 0 ? "Success" : "PartialFailure",
            rebootNeeded,
            failed);
    }

    internal static IReadOnlyList<DriverCatalogEntry> ParseCatalog(
        string catalogXml)
    {
        var result = new List<DriverCatalogEntry>();
        var document = XDocument.Parse(catalogXml, LoadOptions.None);
        foreach (var package in document.Root?.Elements("package") ?? [])
        {
            var location = package.Element("location")?.Value.Trim();
            var category = package.Element("category")?.Value.Trim();
            var checksum = package.Element("checksum")?.Value.Trim();
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(category) ||
                !IsSha256(checksum))
            {
                continue;
            }
            result.Add(new(
                ValidateLenovoUri(uri),
                category,
                checksum!.ToUpperInvariant()));
        }
        return result;
    }

    internal static DriverUpdateItem? ParseDescriptor(
        string descriptorXml,
        Uri descriptorUri,
        string category,
        DriverSystemSnapshot snapshot,
        string language)
    {
        var document = XDocument.Parse(
            descriptorXml,
            LoadOptions.PreserveWhitespace);
        var package = document.Root;
        if (package is null ||
            !package.Name.LocalName.Equals("Package", StringComparison.Ordinal) ||
            bool.TryParse(package.Attribute("hide")?.Value, out var hidden) &&
            hidden)
        {
            return null;
        }

        var packageId = package.Attribute("id")?.Value.Trim();
        var version = package.Attribute("version")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(packageId) ||
            string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var dependency = EvaluateContainer(
            Child(package, "Dependencies"),
            snapshot);
        var detectNode = Child(package, "DetectInstall");
        var detected = detectNode is null
            ? RuleTruth.False
            : EvaluateContainer(detectNode, snapshot);
        if (dependency != RuleTruth.True ||
            detected == RuleTruth.Unknown)
        {
            return null;
        }

        var title = LocalizedDescription(
            Child(package, "Title"),
            language);
        if (string.IsNullOrWhiteSpace(title))
            title = package.Attribute("name")?.Value ?? packageId;

        var installer = ParseInstallPlan(package, descriptorUri);
        if (installer is null)
            return null;

        var releaseDate = Child(package, "ReleaseDate")?.Value.Trim() ??
                          string.Empty;
        var size = Descendants(package, "Installer")
            .SelectMany(element => element.Elements())
            .Where(element => element.Name.LocalName == "File")
            .Select(element => ParseLong(Child(element, "Size")?.Value))
            .Sum();
        var severity = package.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Severity")?
            .Attribute("type")?.Value switch
        {
            "1" => "Critical",
            "2" => "Recommended",
            "3" => "Optional",
            _ => string.Empty
        };
        var reboot = package.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Reboot")?
            .Attribute("type")?.Value ?? string.Empty;
        return new(
            packageId,
            title,
            version,
            ResolveInstalledVersion(detectNode, snapshot),
            category,
            severity,
            reboot,
            size,
            NormalizeDate(releaseDate),
            IsUpdateRequired: detected != RuleTruth.True,
            InstallPlan: installer);
    }

    internal static bool HardwareIdMatches(string installed, string expected)
    {
        var left = NormalizeHardwareId(installed);
        var right = NormalizeHardwareId(expected);
        if (left.Length == 0 || right.Length == 0)
            return false;
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               left.StartsWith(right + "&", StringComparison.OrdinalIgnoreCase) ||
               left.Contains("\\" + right, StringComparison.OrdinalIgnoreCase) ||
               left.Contains(right, StringComparison.OrdinalIgnoreCase);
    }

    internal static int CompareVersions(string left, string right)
    {
        var leftParts = VersionParts(left);
        var rightParts = VersionParts(right);
        var length = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < length; index++)
        {
            var leftValue = index < leftParts.Count ? leftParts[index] : 0;
            var rightValue = index < rightParts.Count ? rightParts[index] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static async Task<DriverUpdateItem?> ReadCandidateAsync(
        DriverCatalogEntry entry,
        DriverSystemSnapshot snapshot,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadDescriptorAsync(entry, cancellationToken);
            return ParseDescriptor(
                DecodeXml(bytes),
                entry.DescriptorUri,
                entry.Category,
                snapshot,
                language);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"Lenovo descriptor {entry.DescriptorUri.AbsolutePath} was skipped: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]> ReadDescriptorAsync(
        DriverCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(CacheDirectory(), "descriptors");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, entry.Sha256 + ".xml");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, cancellationToken);
            // The cache key is the SHA-256 published by the freshly downloaded
            // catalog. Signature verification was required before this exact
            // content-addressed file was first stored.
            if (HashMatches(cached, entry.Sha256))
                return cached;
        }

        await DownloadGate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await DownloadBytesAsync(
                entry.DescriptorUri,
                cancellationToken);
            if (!HashMatches(bytes, entry.Sha256))
                throw new InvalidDataException(
                    "The descriptor SHA-256 does not match the Lenovo catalog.");
            if (!VerifyDescriptorSignature(bytes))
                throw new InvalidDataException(
                    "The Lenovo descriptor XML signature is invalid.");
            var temporary = path + ".download";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return bytes;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static DriverUpdateInstallPlan? ParseInstallPlan(
        XElement package,
        Uri descriptorUri)
    {
        var installerFile = Descendants(package, "Installer")
            .SelectMany(element => element.Elements())
            .FirstOrDefault(element => element.Name.LocalName == "File");
        var fileName = Child(installerFile, "Name")?.Value.Trim();
        var sha256 = Child(installerFile, "CRC")?.Value.Trim();
        var install = Child(package, "Install");
        var commandLine = install?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Cmdline")?
            .Value.Trim();
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName ||
            !IsSha256(sha256) ||
            string.IsNullOrWhiteSpace(commandLine) ||
            !string.Equals(
                install?.Attribute("type")?.Value,
                "cmd",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefix = "%PACKAGEPATH%\\" + fileName;
        var quotedPrefix = "\"" + prefix + "\"";
        string arguments;
        if (commandLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            arguments = commandLine[prefix.Length..].TrimStart();
        else if (commandLine.StartsWith(
                     quotedPrefix,
                     StringComparison.OrdinalIgnoreCase))
            arguments = commandLine[quotedPrefix.Length..].TrimStart();
        else
            return null;

        var successCodes = (install?.Attribute("rc")?.Value ?? "0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result) ? (int?)result : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();
        if (successCodes.Length == 0)
            successCodes = [0];

        return new(
            ValidateLenovoUri(new Uri(descriptorUri, fileName)),
            fileName,
            sha256!.ToUpperInvariant(),
            arguments,
            successCodes);
    }

    private static async Task DownloadAndInstallAsync(
        DriverUpdateItem update,
        CancellationToken cancellationToken)
    {
        var plan = update.InstallPlan ?? throw new InvalidOperationException(
            "The update has no validated installation plan.");
        var packageDirectory = Path.Combine(
            CacheDirectory(),
            "packages",
            SafePathPart(update.PackageId),
            SafePathPart(update.Version));
        Directory.CreateDirectory(packageDirectory);
        var installerPath = Path.Combine(packageDirectory, plan.FileName);
        if (!File.Exists(installerPath) ||
            !await HashMatchesFileAsync(
                installerPath,
                plan.Sha256,
                cancellationToken))
        {
            var temporary = installerPath + ".download";
            await DownloadFileAsync(
                plan.InstallerUri,
                temporary,
                cancellationToken);
            if (!await HashMatchesFileAsync(
                    temporary,
                    plan.Sha256,
                    cancellationToken))
                throw new InvalidDataException(
                    "The installer SHA-256 does not match the Lenovo descriptor.");
            File.Move(temporary, installerPath, overwrite: true);
        }

        VerifyInstallerTrust(installerPath);
        var arguments = plan.Arguments
            .Replace(
                "%PACKAGEPATH%\\TMP",
                Quote(Path.Combine(packageDirectory, "TMP")),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "%PACKAGEPATH%",
                Quote(packageDirectory),
                StringComparison.OrdinalIgnoreCase);
        ToolkitLog.Info(
            $"Launching validated Lenovo installer for {update.PackageId}.");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            WorkingDirectory = packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException(
            "The validated Lenovo installer did not start.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw;
        }
        if (!plan.SuccessExitCodes.Contains(process.ExitCode))
        {
            throw new Win32Exception(
                process.ExitCode,
                $"The Lenovo installer returned exit code {process.ExitCode}.");
        }
    }

    private static DriverSystemSnapshot CaptureSystemSnapshot()
    {
        var devices = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT DeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var deviceId = Convert.ToString(item["DeviceID"]);
                    if (string.IsNullOrWhiteSpace(deviceId))
                        continue;
                    var ids = devices.TryGetValue(deviceId, out var existing)
                        ? existing
                        : devices[deviceId] = new(
                            StringComparer.OrdinalIgnoreCase);
                    ids.Add(deviceId);
                    AddStrings(ids, item["HardwareID"]);
                    AddStrings(ids, item["CompatibleID"]);
                }
            }
        }

        var drivers = new List<InstalledDriverSnapshot>();
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var deviceId = Convert.ToString(item["DeviceID"]);
                    if (string.IsNullOrWhiteSpace(deviceId))
                        continue;
                    var ids = devices.TryGetValue(deviceId, out var existing)
                        ? existing.ToArray()
                        : [deviceId];
                    drivers.Add(new(
                        ids,
                        Convert.ToString(item["DriverVersion"]) ?? string.Empty,
                        ParseWmiDate(Convert.ToString(item["DriverDate"]))));
                }
            }
        }
        return new(
            Environment.OSVersion.Version.Build,
            DeviceModelDetector.CurrentIdentity.BiosVersion,
            drivers);
    }

    private static RuleTruth EvaluateContainer(
        XElement? container,
        DriverSystemSnapshot snapshot)
    {
        if (container is null)
            return RuleTruth.True;
        var rules = container.Elements()
            .Where(element => element.Name.LocalName != "Comment")
            .ToArray();
        if (rules.Length == 0)
            return RuleTruth.True;
        return rules.Length == 1
            ? EvaluateRule(rules[0], snapshot)
            : CombineAnd(rules.Select(rule => EvaluateRule(rule, snapshot)));
    }

    private static RuleTruth EvaluateRule(
        XElement rule,
        DriverSystemSnapshot snapshot)
    {
        switch (rule.Name.LocalName)
        {
            case "And":
                return CombineAnd(rule.Elements().Select(child =>
                    EvaluateRule(child, snapshot)));
            case "Or":
                return CombineOr(rule.Elements().Select(child =>
                    EvaluateRule(child, snapshot)));
            case "Not":
                return Negate(EvaluateContainer(rule, snapshot));
            case "_OS":
                return rule.Elements().Any(element =>
                    element.Name.LocalName == "OS" &&
                    element.Value.Trim().Equals(
                        "WIN11",
                        StringComparison.OrdinalIgnoreCase))
                    ? RuleTruth.True
                    : RuleTruth.False;
            case "_WindowsBuildVersion":
                return rule.Elements().Any(element =>
                           element.Name.LocalName == "Version" &&
                           int.TryParse(element.Value, out var build) &&
                           build == snapshot.WindowsBuild)
                    ? RuleTruth.True
                    : RuleTruth.False;
            case "_PnPID":
                return snapshot.Drivers.Any(driver =>
                           driver.HardwareIds.Any(id =>
                               HardwareIdMatches(id, rule.Value)))
                    ? RuleTruth.True
                    : RuleTruth.False;
            case "_Driver":
                return EvaluateDriverRule(rule, snapshot);
            case "_Bios":
                return rule.Elements()
                    .Where(element => element.Name.LocalName == "Level")
                    .Select(element => element.Value.Trim())
                    .Any(level => WildcardPrefixMatches(
                        snapshot.BiosVersion,
                        level))
                    ? RuleTruth.True
                    : RuleTruth.False;
            case "Comment":
                return RuleTruth.True;
            default:
                if (UnknownRules.TryAdd(rule.Name.LocalName, 0))
                {
                    ToolkitLog.Warning(
                        $"Independent Lenovo scanner does not evaluate rule {rule.Name.LocalName}; affected packages are skipped.");
                }
                return RuleTruth.Unknown;
        }
    }

    private static RuleTruth EvaluateDriverRule(
        XElement rule,
        DriverSystemSnapshot snapshot)
    {
        var hardwareIds = rule.Elements()
            .Where(element => element.Name.LocalName == "HardwareID")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        var matches = snapshot.Drivers
            .Where(driver => hardwareIds.Any(expected =>
                driver.HardwareIds.Any(installed =>
                    HardwareIdMatches(installed, expected))))
            .ToArray();
        if (matches.Length == 0)
            return RuleTruth.False;

        var required = rule.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Version")?
            .Value;
        if (string.IsNullOrWhiteSpace(required))
            return RuleTruth.True;
        required = MinimumVersion(required);
        return matches.Any(driver =>
                   !string.IsNullOrWhiteSpace(driver.Version) &&
                   CompareVersions(driver.Version, required) >= 0)
            ? RuleTruth.True
            : RuleTruth.False;
    }

    private static string ResolveInstalledVersion(
        XElement? detectNode,
        DriverSystemSnapshot snapshot)
    {
        if (detectNode is null)
            return string.Empty;
        if (detectNode.DescendantsAndSelf().Any(element =>
                element.Name.LocalName == "_Bios"))
            return snapshot.BiosVersion;

        var versions = new List<string>();
        foreach (var driverRule in detectNode.DescendantsAndSelf().Where(
                     element => element.Name.LocalName == "_Driver"))
        {
            var expected = driverRule.Elements()
                .Where(element => element.Name.LocalName == "HardwareID")
                .Select(element => element.Value.Trim())
                .ToArray();
            versions.AddRange(snapshot.Drivers
                .Where(driver => expected.Any(id =>
                    driver.HardwareIds.Any(installed =>
                        HardwareIdMatches(installed, id))))
                .Select(driver => driver.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version)));
        }
        return versions.OrderByDescending(
                version => version,
                Comparer<string>.Create(CompareVersions))
            .FirstOrDefault() ?? string.Empty;
    }

    private static RuleTruth CombineAnd(IEnumerable<RuleTruth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == RuleTruth.False)
                return RuleTruth.False;
            unknown |= value == RuleTruth.Unknown;
        }
        return unknown ? RuleTruth.Unknown : RuleTruth.True;
    }

    private static RuleTruth CombineOr(IEnumerable<RuleTruth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == RuleTruth.True)
                return RuleTruth.True;
            unknown |= value == RuleTruth.Unknown;
        }
        return unknown ? RuleTruth.Unknown : RuleTruth.False;
    }

    private static RuleTruth Negate(RuleTruth value) => value switch
    {
        RuleTruth.True => RuleTruth.False,
        RuleTruth.False => RuleTruth.True,
        _ => RuleTruth.Unknown
    };

    private static async Task<byte[]> DownloadBytesAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ValidateLenovoUri(uri);
        using var response = await Http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task DownloadFileAsync(
        Uri uri,
        string path,
        CancellationToken cancellationToken)
    {
        ValidateLenovoUri(uri);
        using var response = await Http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var target = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool VerifyDescriptorSignature(byte[] bytes)
    {
        try
        {
            var xml = new XmlDocument { PreserveWhitespace = true };
            using var stream = new MemoryStream(bytes, writable: false);
            xml.Load(stream);
            var signature = xml.GetElementsByTagName(
                    "Signature",
                    SignedXml.XmlDsigNamespaceUrl)
                .OfType<XmlElement>()
                .FirstOrDefault();
            if (signature is null)
                return false;
            var signedXml = new SignedXml(xml);
            signedXml.LoadXml(signature);
            foreach (KeyInfoClause clause in signedXml.KeyInfo)
            {
                if (clause is not KeyInfoX509Data data)
                    continue;
                if (data.Certificates is null)
                    continue;
                foreach (var certificateValue in data.Certificates)
                {
                    using var certificate = certificateValue switch
                    {
                        X509Certificate2 value => new X509Certificate2(value),
                        X509Certificate value => new X509Certificate2(value),
                        _ => null
                    };
                    if (certificate is null ||
                        !certificate.Subject.Contains(
                            "O=Lenovo",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (signedXml.CheckSignature(certificate, true))
                        return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static void VerifyInstallerTrust(string path)
    {
        if (WinVerifyTrust(path) != 0)
            throw new InvalidDataException(
                "Windows could not validate the installer Authenticode signature.");
#pragma warning disable SYSLIB0057
        using var certificate = new X509Certificate2(
            X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        if (!certificate.Subject.Contains(
                "O=Lenovo",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The installer is not signed by Lenovo.");
        }
    }

    private static int WinVerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData(filePointer);
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(
                new IntPtr(-1),
                ref action,
                ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
            fileInfo.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"ThinkBookToolkit/{ApplicationUpdateService.CurrentVersionText}");
        return client;
    }

    private static bool TryGetMachineType(out string machineType)
    {
        var product = DeviceModelDetector.CurrentIdentity.ProductNumber
            .Trim()
            .ToUpperInvariant();
        machineType = product.Length >= 4 ? product[..4] : string.Empty;
        return machineType.Length == 4 && machineType.All(char.IsLetterOrDigit);
    }

    private static string CacheDirectory() => Path.Combine(
        Path.GetDirectoryName(CurveProfileStore.SettingsPath)!,
        "driver_update_cache");

    private static Uri ValidateLenovoUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(DownloadHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Only the official Lenovo HTTPS download host is allowed.");
        }
        return uri;
    }

    private static string LocalizedDescription(
        XElement? container,
        string language)
    {
        if (container is null)
            return string.Empty;
        var descriptions = container.Elements()
            .Where(element => element.Name.LocalName == "Desc")
            .ToArray();
        var preferred = language.StartsWith(
                "zh",
                StringComparison.OrdinalIgnoreCase)
            ? new[] { "CHS", "ZH-CN", "CN", "EN" }
            : new[] { "EN" };
        foreach (var id in preferred)
        {
            var match = descriptions.FirstOrDefault(element =>
                string.Equals(
                    element.Attribute("id")?.Value,
                    id,
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match?.Value))
                return match.Value.Trim();
        }
        return descriptions.FirstOrDefault()?.Value.Trim() ?? string.Empty;
    }

    private static IEnumerable<XElement> Descendants(
        XElement root,
        string localName) => root.Descendants().Where(element =>
        element.Name.LocalName == localName);

    private static XElement? Child(XElement? root, string localName) =>
        root?.Elements().FirstOrDefault(element =>
            element.Name.LocalName == localName);

    private static void AddStrings(ISet<string> target, object? value)
    {
        if (value is string text)
            target.Add(text);
        else if (value is string[] values)
            foreach (var item in values)
                if (!string.IsNullOrWhiteSpace(item))
                    target.Add(item);
    }

    private static DateTime? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch
        {
            return null;
        }
    }

    private static string MinimumVersion(string value)
    {
        var separator = value.IndexOf('^');
        return (separator >= 0 ? value[..separator] : value)
            .Trim()
            .TrimEnd('*');
    }

    private static IReadOnlyList<long> VersionParts(string value)
    {
        var result = new List<long>();
        foreach (var part in value.Split(
                     ['.', '-', '_'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            result.Add(long.TryParse(
                digits,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) ? number : 0);
        }
        return result;
    }

    private static bool WildcardPrefixMatches(string value, string pattern)
    {
        pattern = pattern.Trim();
        return pattern.EndsWith('*')
            ? value.StartsWith(
                pattern[..^1],
                StringComparison.OrdinalIgnoreCase)
            : value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHardwareId(string value) =>
        value.Trim().Replace('/', '\\').ToUpperInvariant();

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool HashMatches(byte[] bytes, string expected) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HashMatchesFileAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static long ParseLong(string? value) =>
        long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var number) ? number : 0;

    private static string NormalizeDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;

    private static string SafePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "package" : result;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string DecodeXml(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true,
        SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid actionId,
        ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly uint _structSize =
            (uint)Marshal.SizeOf<WinTrustFileInfo>();
        private IntPtr _filePath;
        private readonly IntPtr _fileHandle = IntPtr.Zero;
        private readonly IntPtr _knownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string path) =>
            _filePath = Marshal.StringToCoTaskMemUni(path);

        public void Dispose()
        {
            if (_filePath == IntPtr.Zero)
                return;
            Marshal.FreeCoTaskMem(_filePath);
            _filePath = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        private uint _structSize;
        private IntPtr _policyCallbackData;
        private IntPtr _sipClientData;
        private uint _uiChoice;
        private uint _revocationChecks;
        private uint _unionChoice;
        private IntPtr _fileInfo;
        private uint _stateAction;
        private IntPtr _stateData;
        private IntPtr _urlReference;
        private uint _providerFlags;
        private uint _uiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            _structSize = (uint)Marshal.SizeOf<WinTrustData>();
            _policyCallbackData = IntPtr.Zero;
            _sipClientData = IntPtr.Zero;
            _uiChoice = 2;
            _revocationChecks = 0;
            _unionChoice = 1;
            _fileInfo = fileInfo;
            _stateAction = 0;
            _stateData = IntPtr.Zero;
            _urlReference = IntPtr.Zero;
            _providerFlags = 0x00000100;
            _uiContext = 0;
        }
    }
}
