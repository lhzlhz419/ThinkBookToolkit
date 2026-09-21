using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.FanBackend;

/// <summary>No caller-controlled absolute paths cross the guardian service boundary.</summary>
public sealed record PluginFanBackendSelection(string PluginId, string Fingerprint, string Assembly, string Type)
{
    public string Identity => $"plugin:{PluginId}:{Fingerprint}:{Assembly}:{Type}";
}

/// <summary>Immutable, administrator-approved packages shared by Toolkit and its recovery service.</summary>
public static class PluginFanBackendPackage
{
    private static readonly ConcurrentDictionary<string, Lazy<Assembly>> Assemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ThinkBookToolkit.PluginFanBackends");

    public static void ValidateSelection(PluginFanBackendSelection selection)
    {
        if (!Regex.IsMatch(selection.PluginId ?? "", @"^[a-z0-9][a-z0-9._-]{0,100}$") ||
            !Regex.IsMatch(selection.Fingerprint ?? "", "^[A-F0-9]{64}$") ||
            string.IsNullOrWhiteSpace(selection.Assembly) || selection.Assembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(selection.Assembly) != selection.Assembly || !selection.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(selection.Type) || selection.Type.Length > 256)
            throw new InvalidDataException("Invalid fan backend selection.");
    }

    public static string Fingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; var count = 0;
        foreach (var file in PackageFiles(directory).OrderBy(x => x, StringComparer.Ordinal))
        {
            total += new FileInfo(file).Length;
            if (++count > 2048 || total > 256L * 1024 * 1024) throw new InvalidDataException("Plugin package size limit exceeded.");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file)));
            using var input = File.OpenRead(file); hash.AppendData(SHA256.HashData(input));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> PackageFiles(string directory)
    {
        RejectLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry))
                foreach (var file in PackageFiles(entry)) yield return file;
            else yield return entry;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Plugin package links are not allowed.");
    }

    public static void Stage(string source, PluginFanBackendSelection selection)
    {
        ValidateSelection(selection);
        ValidatePackage(source, selection);
        // Create with restrictive ACLs from the outset, not after copying executable code.
        new DirectoryInfo(CacheRoot).Create(DirectorySecurity());
        VerifyProtected(new DirectoryInfo(CacheRoot));
        var destination = Path.Combine(CacheRoot, selection.Fingerprint);
        if (Directory.Exists(destination)) { VerifyCached(selection); return; }
        var staging = Path.Combine(CacheRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        new DirectoryInfo(staging).Create(DirectorySecurity(allowUsersRead: false));
        // Incomplete staging directories are deliberately not loaded or automatically removed.
        long copied = 0; var count = 0;
        foreach (var file in PackageFiles(source))
        {
            if (++count > 2048) throw new InvalidDataException("Plugin package size limit exceeded.");
            var target = Path.Combine(staging, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = File.OpenRead(file))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    copied += read;
                    if (copied > 256L * 1024 * 1024) throw new InvalidDataException("Plugin package size limit exceeded.");
                    output.Write(buffer, 0, read);
                }
            }
        }
        // Until every copied byte matches the approval, staging stays admin-only.
        // A source-file race must not expose privileged data through an incomplete copy.
        ValidatePackage(staging, selection);
        foreach (var target in PackageFiles(staging))
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Administrators);
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(SystemAccount, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(target).SetAccessControl(security);
        }
        foreach (var directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories))
            new DirectoryInfo(directory).SetAccessControl(DirectorySecurity());
        new DirectoryInfo(staging).SetAccessControl(DirectorySecurity());
        Directory.Move(staging, destination);
        VerifyCached(selection);
    }

    private static DirectorySecurity DirectorySecurity(bool allowUsersRead = true)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Administrators);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var account in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (allowUsersRead)
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void VerifyProtected(FileSystemInfo entry)
    {
        RejectLink(entry.FullName);
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        VerifySecurity(security);
    }

    private static void VerifySecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Administrators.Equals(owner) && !SystemAccount.Equals(owner))
            throw new UnauthorizedAccessException("Fan backend cache must be administrator-owned.");
        const FileSystemRights readOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !Administrators.Equals(rule.IdentityReference) &&
                !SystemAccount.Equals(rule.IdentityReference) && (rule.FileSystemRights & ~readOnly) != 0)
                throw new UnauthorizedAccessException("Fan backend cache is writable by a non-administrator.");
    }

    public static string VerifyCached(PluginFanBackendSelection selection)
    {
        ValidateSelection(selection);
        VerifyProtected(new DirectoryInfo(CacheRoot));
        var directory = Path.Combine(CacheRoot, selection.Fingerprint);
        VerifyProtected(new DirectoryInfo(directory));
        foreach (var file in PackageFiles(directory)) VerifyProtected(new FileInfo(file));
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            VerifyProtected(new DirectoryInfo(child));
        ValidatePackage(directory, selection);
        return directory;
    }

    public static void ValidatePackage(string directory, PluginFanBackendSelection selection)
    {
        ValidateSelection(selection);
        if (Fingerprint(directory) != selection.Fingerprint) throw new InvalidDataException("Fan backend package fingerprint changed.");
        var manifestPath = Path.Combine(directory, "plugin.json");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("Manifest too large.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
        if (manifest?.Id != selection.PluginId || manifest.ApiVersion != PluginApiVersion.Current ||
            manifest.Permissions?.Contains("fan.backend") != true || manifest.FanBackend?.Assembly != selection.Assembly ||
            manifest.FanBackend.Type != selection.Type || !File.Exists(Path.Combine(directory, selection.Assembly)))
            throw new InvalidDataException("Fan backend is not declared by the approved plugin.");
    }

    public static IFanBackend Load(PluginFanBackendSelection selection) => LoadFromVerifiedDirectory(VerifyCached(selection), selection);

    internal static IFanBackend LoadFromVerifiedDirectory(string directory, PluginFanBackendSelection selection)
    {
        ValidatePackage(directory, selection);
        var path = Path.GetFullPath(Path.Combine(directory, selection.Assembly));
        // Detection and runtime may construct separate instances. Reuse the load
        // context so backend static locks/state retain the legacy DLL semantics.
        var assembly = Assemblies.GetOrAdd(path, p => new Lazy<Assembly>(() =>
            new BackendAssemblyContext(p).LoadFromAssemblyPath(p))).Value;
        var type = assembly.GetType(selection.Type, throwOnError: true)!;
        if (type.IsAbstract || !typeof(IFanBackend).IsAssignableFrom(type))
            throw new InvalidDataException("Plugin fan backend must implement IFanBackend.");
        try
        {
            var backend = (IFanBackend)Activator.CreateInstance(type)!;
            if (backend.ApiVersion != FanBackendContract.CurrentVersion)
                throw new NotSupportedException($"Fan backend API {backend.ApiVersion} is incompatible with {FanBackendContract.CurrentVersion}.");
            return backend;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed class BackendAssemblyContext(string entry) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IFanBackend).Assembly.GetName().Name) return typeof(IFanBackend).Assembly;
            if (name.Name == typeof(IToolkitPlugin).Assembly.GetName().Name) return typeof(IToolkitPlugin).Assembly;
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is null && File.Exists(Path.Combine(Path.GetDirectoryName(entry)!, name.Name + ".dll")))
                path = Path.Combine(Path.GetDirectoryName(entry)!, name.Name + ".dll");
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
