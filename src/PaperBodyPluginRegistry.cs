using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class PaperBodyProviderIds
{
    public const string Markdown = "builtin.markdown";
}

internal enum PaperBodyPluginKind
{
    BuiltIn,
    Native,
    Web
}

internal sealed record PaperBodyPluginDescriptor(
    string Id,
    string DisplayName,
    string Description,
    Version Version,
    string ApiVersion,
    int StateVersion,
    PaperBodyPluginKind Kind,
    PaperBodyCapabilities Capabilities,
    IReadOnlySet<string> Permissions,
    string PluginDirectory,
    string SourcePath,
    string Fingerprint,
    Type? NativePluginType = null,
    PaperBodyPluginManifest? Manifest = null);

internal sealed record PaperBodyPluginLoadIssue(
    string SourcePath,
    string Message);

internal sealed record PaperBodyNativePluginActivation(
    IPaperBodyPlugin Plugin,
    PaperBodyPluginDescriptor Descriptor);

internal sealed class PaperBodyPluginManifest
{
    public string Kind { get; set; } = "web";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string ApiVersion { get; set; } = "";
    public int StateVersion { get; set; } = 1;
    public int MaxPaperInstances { get; set; } = 1;
    public string[] Permissions { get; set; } = [];
    public string Entry { get; set; } = "index.html";
    public string MiniEntry { get; set; } = "";
    public string Runtime { get; set; } = "";
    public PaperBodyPluginMiniSizeManifest? MiniSize { get; set; }
    public PaperBodyPluginMiniSizeManifest? MiniMaxSize { get; set; }
    public string[] Capabilities { get; set; } = [];
    public bool AdvancedSettings { get; set; }
    public int? PrimarySettings { get; set; }
    public PaperBodyPluginSettingCategoryManifest[] SettingCategories { get; set; } = [];
    public PaperBodyPluginSettingManifest[] Settings { get; set; } = [];
    public PaperBodyPluginStartupManifest? StartupPaper { get; set; }

    public string DirectoryPath { get; internal set; } = "";
    public string EntryPath { get; internal set; } = "";
    public string MiniEntryPath { get; internal set; } = "";
    public string RuntimePath { get; internal set; } = "";
}

internal sealed class PaperBodyPluginMiniSizeManifest
{
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 220;
}

/// <summary>
/// Discovers one fully trusted, unsandboxed native or local Web plugin from each self-contained
/// plugins/&lt;plugin-id&gt;/plugin.json folder. Protocol 2.1 has no plugin hot-reload contract: code,
/// manifest and Web file changes are discovered on the next app start. Loaded native assemblies
/// remain loaded for the process lifetime.
/// </summary>
internal sealed partial class PaperBodyPluginRegistry : IDisposable
{
    internal const string SupportedPluginApiVersion = "2.1";
    private static readonly Regex PluginIdPattern = PluginIdRegex();
    private static readonly StringComparer UiDisplayNameComparer =
        StringComparer.Create(UiLanguages.EffectiveUiCulture, ignoreCase: true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed record LoadedNativePlugin(
        string DirectoryPath,
        string Fingerprint,
        PaperBodyPluginDescriptor Descriptor,
        NativePluginLoadContext LoadContext);

    private readonly Dictionary<string, PaperBodyPluginDescriptor> _descriptors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedNativePlugin> _loadedNativeByDirectory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PaperBodyPluginLoadIssue> _issues = [];
    private bool _disposed;

    public PaperBodyPluginRegistry()
    {
        PluginRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
        LoadInitial();
    }

    public string PluginRoot { get; }

    public IReadOnlyList<PaperBodyPluginDescriptor> Descriptors =>
        _descriptors.Values
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.DisplayName, UiDisplayNameComparer)
            .ToArray();

    public IReadOnlyList<PaperBodyPluginLoadIssue> Issues => _issues.ToArray();
    public bool TryGet(string? id, out PaperBodyPluginDescriptor descriptor)
    {
        var normalized = string.IsNullOrWhiteSpace(id)
            ? PaperBodyProviderIds.Markdown
            : id.Trim();
        return _descriptors.TryGetValue(normalized, out descriptor!);
    }

    public PaperBodyNativePluginActivation CreateNativePlugin(
        PaperBodyPluginDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (descriptor.Kind != PaperBodyPluginKind.Native ||
            descriptor.Manifest == null)
        {
            throw new InvalidOperationException("The descriptor is not a native plugin.");
        }

        if (_loadedNativeByDirectory.TryGetValue(
                descriptor.PluginDirectory,
                out var loaded))
        {
            if (!string.Equals(
                    loaded.Descriptor.Id,
                    descriptor.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The loaded native plugin does not match the requested descriptor.");
            }

            var pluginType = loaded.Descriptor.NativePluginType
                ?? throw new InvalidOperationException(
                    "The loaded native plugin has no factory type.");
            var plugin = (IPaperBodyPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException(
                    $"Could not create native plugin {pluginType.FullName}.");
            return new PaperBodyNativePluginActivation(
                plugin,
                loaded.Descriptor);
        }

        return LoadNativePlugin(descriptor);
    }

    private void LoadInitial()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _issues.Clear();
        _descriptors.Clear();

        _descriptors[PaperBodyProviderIds.Markdown] = new PaperBodyPluginDescriptor(
            PaperBodyProviderIds.Markdown,
            Strings.Get("BodyProviderMarkdown"),
            Strings.Get("BodyProviderMarkdownDescription"),
            typeof(PaperWindow).Assembly.GetName().Version ?? new Version(1, 0),
            SupportedPluginApiVersion,
            1,
            PaperBodyPluginKind.BuiltIn,
            PaperBodyCapabilities.TextZoom | PaperBodyCapabilities.NoteLinks,
            PaperTodoPermissionNames.None,
            AppContext.BaseDirectory,
            typeof(PaperWindow).Assembly.Location,
            "builtin");

        var pluginDirectories = Directory.Exists(PluginRoot)
            ? EnumeratePluginDirectories()
            : Array.Empty<string>();
        foreach (var directory in pluginDirectories)
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = ReadManifest(manifestPath, directory);
                var descriptor = NormalizeKind(manifest.Kind) switch
                {
                    PaperBodyPluginKind.Web => LoadWebDescriptor(manifest, manifestPath),
                    PaperBodyPluginKind.Native => LoadNativeDescriptor(manifest, manifestPath),
                    _ => throw new InvalidDataException("Built-in plugins cannot be loaded from disk.")
                };
                AddDescriptor(_descriptors, descriptor);
            }
            catch (Exception ex)
            {
                _issues.Add(new PaperBodyPluginLoadIssue(
                    manifestPath,
                    ex.GetBaseException().Message));
            }
        }
    }

    private IEnumerable<string> EnumeratePluginDirectories()
    {
        return Directory.EnumerateDirectories(PluginRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return !string.IsNullOrEmpty(name) &&
                    name[0] is not '.' and not '_' &&
                    !string.Equals(name, "data", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase);
    }

    private PaperBodyPluginManifest ReadManifest(string manifestPath, string directory)
    {
        var manifest = JsonSerializer.Deserialize<PaperBodyPluginManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions)
            ?? throw new InvalidDataException("plugin.json deserialized to null.");
        ValidatePluginId(manifest.Id);
        var id = manifest.Id.Trim();
        if (!string.Equals(Path.GetFileName(directory), id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Plugin folder name must match plugin id '{id}'.");
        }
        manifest.ApiVersion = NormalizeApiVersion(manifest.ApiVersion);
        ValidateManifestApiVersion(manifest.ApiVersion);
        if (manifest.StateVersion < 1)
        {
            throw new InvalidDataException("stateVersion must be at least 1.");
        }
        ValidateSettings(manifest);
        ValidateStartupPaper(manifest);
        ValidateProtocolFeatures(manifest);

        var kind = NormalizeKind(manifest.Kind);
        manifest.DirectoryPath = Path.GetFullPath(directory);
        manifest.EntryPath = ResolveContainedPath(directory, manifest.Entry);
        if (!File.Exists(manifest.EntryPath))
        {
            throw new FileNotFoundException("Plugin entry was not found.", manifest.EntryPath);
        }

        var webRoot = kind == PaperBodyPluginKind.Web
            ? Path.GetDirectoryName(manifest.EntryPath)
              ?? throw new InvalidDataException("Web plugin entry has no containing directory.")
            : null;

        if (!string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            if (kind != PaperBodyPluginKind.Web)
            {
                throw new InvalidDataException(
                    "miniEntry is only valid for Web plugins.");
            }

            manifest.MiniEntryPath = ResolveContainedPath(
                directory,
                manifest.MiniEntry);
            if (!File.Exists(manifest.MiniEntryPath))
            {
                throw new FileNotFoundException(
                    "Plugin mini entry was not found.",
                    manifest.MiniEntryPath);
            }
            EnsurePathInsideDirectory(
                webRoot!,
                manifest.MiniEntryPath,
                "miniEntry");

            if (manifest.MiniSize is { } miniSize)
            {
                ValidateMiniSize(miniSize, "miniSize");
            }
        }
        else if (manifest.MiniSize != null)
        {
            throw new InvalidDataException(
                "miniSize requires a Web miniEntry.");
        }

        if (manifest.MiniMaxSize is { } miniMaximum)
        {
            if (kind == PaperBodyPluginKind.Web &&
                string.IsNullOrWhiteSpace(manifest.MiniEntry))
            {
                throw new InvalidDataException(
                    "miniMaxSize requires a Web miniEntry for Web plugins.");
            }
            ValidateMiniSize(miniMaximum, "miniMaxSize");
            if (manifest.MiniSize is { } preferred &&
                (preferred.Width > miniMaximum.Width || preferred.Height > miniMaximum.Height))
            {
                throw new InvalidDataException(
                    "miniSize cannot exceed the declared miniMaxSize.");
            }
        }

        var hasPluginRuntime = manifest.Capabilities.Contains(
            "runtime",
            StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(manifest.Runtime) &&
            kind != PaperBodyPluginKind.Web)
        {
            throw new InvalidDataException(
                "runtime is only valid for Web plugins.");
        }
        if (!string.IsNullOrWhiteSpace(manifest.Runtime) && !hasPluginRuntime)
        {
            throw new InvalidDataException(
                "runtime requires the runtime capability.");
        }
        if (kind == PaperBodyPluginKind.Web && hasPluginRuntime)
        {
            manifest.RuntimePath = string.IsNullOrWhiteSpace(manifest.Runtime)
                ? Path.Combine(webRoot!, "runtime.html")
                : ResolveContainedPath(directory, manifest.Runtime);
            EnsurePathInsideDirectory(webRoot!, manifest.RuntimePath, "runtime");
            if (!File.Exists(manifest.RuntimePath))
            {
                throw new FileNotFoundException(
                    "Plugin runtime entry was not found.",
                    manifest.RuntimePath);
            }
        }

        return manifest;
    }

    private static void ValidateMiniSize(
        PaperBodyPluginMiniSizeManifest size,
        string fieldName)
    {
        if (!double.IsFinite(size.Width) ||
            !double.IsFinite(size.Height) ||
            size.Width <= 0 ||
            size.Height <= 0)
        {
            throw new InvalidDataException(
                $"{fieldName} width and height must be positive finite numbers.");
        }
    }

    private static void EnsurePathInsideDirectory(
        string rootDirectory,
        string path,
        string fieldName)
    {
        var relative = Path.GetRelativePath(rootDirectory, path);
        if (relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{fieldName} must stay inside the Web entry directory.");
        }
    }

    private static PaperBodyPluginKind NormalizeKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "web" => PaperBodyPluginKind.Web,
            "native" => PaperBodyPluginKind.Native,
            _ => throw new InvalidDataException("plugin kind must be 'web' or 'native'.")
        };

    private PaperBodyPluginDescriptor LoadWebDescriptor(
        PaperBodyPluginManifest manifest,
        string manifestPath)
    {
        var fingerprint = DiscoveryFingerprint(
            manifestPath,
            manifest.EntryPath,
            manifest.MiniEntryPath,
            manifest.RuntimePath);
        return new PaperBodyPluginDescriptor(
            manifest.Id.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id.Trim() : manifest.Name.Trim(),
            manifest.Description?.Trim() ?? "",
            ParseVersion(manifest.Version),
            manifest.ApiVersion,
            manifest.StateVersion,
            PaperBodyPluginKind.Web,
            ParseCapabilities(manifest.Capabilities),
            ParsePermissions(manifest.Permissions),
            manifest.DirectoryPath,
            manifestPath,
            fingerprint,
            Manifest: manifest);
    }

    private PaperBodyPluginDescriptor LoadNativeDescriptor(
        PaperBodyPluginManifest manifest,
        string manifestPath)
    {
        var directory = manifest.DirectoryPath;
        if (!string.Equals(
                Path.GetExtension(manifest.EntryPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A native plugin entry must be a .dll file.");
        }

        return new PaperBodyPluginDescriptor(
            manifest.Id.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name)
                ? manifest.Id.Trim()
                : manifest.Name.Trim(),
            manifest.Description?.Trim() ?? "",
            ParseVersion(manifest.Version),
            manifest.ApiVersion,
            manifest.StateVersion,
            PaperBodyPluginKind.Native,
            ParseCapabilities(manifest.Capabilities),
            ParsePermissions(manifest.Permissions),
            directory,
            manifestPath,
            DiscoveryFingerprint(manifestPath, manifest.EntryPath),
            Manifest: manifest);
    }

    private PaperBodyNativePluginActivation LoadNativePlugin(
        PaperBodyPluginDescriptor discoveredDescriptor)
    {
        var manifest = discoveredDescriptor.Manifest
            ?? throw new InvalidOperationException(
                "The native plugin manifest is unavailable.");
        var directory = manifest.DirectoryPath;
        var fingerprint = PluginFolderFingerprint(directory);
        var loadContext = new NativePluginLoadContext(manifest.EntryPath);
        IPaperBodyPlugin? plugin = null;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(manifest.EntryPath);
            var pluginTypes = GetPluginTypes(assembly, manifest.EntryPath);
            if (pluginTypes.Length != 1)
            {
                throw new InvalidDataException(
                    "A native plugin folder must contain exactly one public parameterless IPaperBodyPlugin implementation in its entry assembly.");
            }

            var pluginType = pluginTypes[0];
            plugin = (IPaperBodyPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidDataException($"Could not create {pluginType.FullName}.");
            // plugin.json is the sole metadata authority; the CLR type contributes behavior only.
            var descriptor = discoveredDescriptor with
            {
                Fingerprint = fingerprint,
                NativePluginType = pluginType
            };
            _loadedNativeByDirectory[directory] = new LoadedNativePlugin(
                directory,
                fingerprint,
                descriptor,
                loadContext);
            if (_descriptors.TryGetValue(descriptor.Id, out var current) &&
                string.Equals(
                    current.PluginDirectory,
                    directory,
                    StringComparison.OrdinalIgnoreCase))
            {
                _descriptors[descriptor.Id] = descriptor;
            }
            return new PaperBodyNativePluginActivation(plugin, descriptor);
        }
        catch
        {
            if (plugin is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            try { loadContext.Unload(); } catch { }
            throw;
        }
    }

    private Type[] GetPluginTypes(Assembly assembly, string sourcePath)
    {
        try
        {
            return assembly.GetTypes()
                .Where(IsPluginType)
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.Where(item => item != null))
            {
                _issues.Add(new PaperBodyPluginLoadIssue(sourcePath, loaderException!.Message));
            }
            return ex.Types
                .Where(type => type != null && IsPluginType(type))
                .Cast<Type>()
                .ToArray();
        }
    }

    private static bool IsPluginType(Type type) =>
        type.IsPublic &&
        !type.IsAbstract &&
        !type.IsInterface &&
        typeof(IPaperBodyPlugin).IsAssignableFrom(type) &&
        type.GetConstructor(Type.EmptyTypes) != null;

    private static void AddDescriptor(
        IDictionary<string, PaperBodyPluginDescriptor> target,
        PaperBodyPluginDescriptor descriptor)
    {
        if (target.ContainsKey(descriptor.Id))
        {
            throw new InvalidDataException($"Duplicate plugin id: {descriptor.Id}");
        }
        target.Add(descriptor.Id, descriptor);
    }

    private static void ValidatePluginId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !PluginIdPattern.IsMatch(id.Trim()))
        {
            throw new InvalidDataException(
                "Plugin id must contain 3-120 ASCII letters, digits, '.', '_' or '-'.");
        }
        if (string.Equals(id.Trim(), PaperBodyProviderIds.Markdown, StringComparison.Ordinal) ||
            string.Equals(id.Trim(), "data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The plugin id is reserved by PaperTodo.");
        }
    }

    private static string ResolveContainedPath(string directory, string? relativePath)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(directory, relativePath ?? ""));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plugin entry must stay inside its plugin directory.");
        }
        return combined;
    }

    private static PaperBodyCapabilities ParseCapabilities(IEnumerable<string>? values)
    {
        var result = PaperBodyCapabilities.None;
        foreach (var value in values ?? [])
        {
            result |= value?.Trim().ToLowerInvariant() switch
            {
                "textzoom" => PaperBodyCapabilities.TextZoom,
                "notelinks" => PaperBodyCapabilities.NoteLinks,
                _ => PaperBodyCapabilities.None
            };
        }
        return result;
    }

    private static string NormalizeApiVersion(string? value)
    {
        value = value?.Trim();
        var parts = value?.Split('.', StringSplitOptions.None);
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            major < 0 ||
            minor < 0)
        {
            throw new InvalidDataException(
                "apiVersion must be a quoted major.minor string such as \"2.1\".");
        }

        return $"{major}.{minor}";
    }

    private static void ValidateManifestApiVersion(string pluginApiVersion)
    {
        if (string.Equals(
                pluginApiVersion,
                SupportedPluginApiVersion,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"Unsupported plugin API version {pluginApiVersion}; host requires {SupportedPluginApiVersion}.");
    }

    private static Version ParseVersion(string? value)
    {
        if (!Version.TryParse(value, out var parsed))
        {
            throw new InvalidDataException(
                $"Plugin version '{value}' is not a valid version.");
        }
        return parsed;
    }

    private static string PluginFolderFingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !IsRuntimePath(directory, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(new byte[] { 0 });
            using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string DiscoveryFingerprint(
        string manifestPath,
        string entryPath,
        string? miniEntryPath = null,
        string? runtimePath = null)
    {
        var manifest = new FileInfo(manifestPath);
        var entry = new FileInfo(entryPath);
        var value = $"discovery:{manifest.Length}:{manifest.LastWriteTimeUtc.Ticks}:" +
            $"{entry.Length}:{entry.LastWriteTimeUtc.Ticks}";
        if (!string.IsNullOrWhiteSpace(miniEntryPath))
        {
            var mini = new FileInfo(miniEntryPath);
            value += $":{mini.Length}:{mini.LastWriteTimeUtc.Ticks}";
        }
        if (!string.IsNullOrWhiteSpace(runtimePath))
        {
            var runtime = new FileInfo(runtimePath);
            value += $":{runtime.Length}:{runtime.LastWriteTimeUtc.Ticks}";
        }
        return value;
    }

    private static bool IsRuntimePath(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, ".runtime", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _descriptors.Clear();
        _issues.Clear();
        foreach (var loaded in _loadedNativeByDirectory.Values)
        {
            try { loaded.LoadContext.Unload(); } catch { }
        }
        _loadedNativeByDirectory.Clear();
        _dataStore.Dispose();
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdRegex();

    private sealed class NativePluginLoadContext : AssemblyLoadContext
    {
        private static readonly string AbstractionsAssemblyName =
            typeof(IPaperBodyPlugin).Assembly.GetName().Name ??
            "PaperTodo.Plugin.Abstractions";

        private static readonly HashSet<string> SharedHostAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            AbstractionsAssemblyName,
            "WinRT.Runtime",
            "Microsoft.Windows.SDK.NET",
            "Microsoft.Web.WebView2.Core",
            "Microsoft.Web.WebView2.Wpf",
            "Microsoft.Web.WebView2.WinForms"
        };

        private readonly AssemblyDependencyResolver _resolver;

        public NativePluginLoadContext(string pluginAssemblyPath)
            : base($"PaperTodo.Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && SharedHostAssemblyNames.Contains(assemblyName.Name))
            {
                if (string.Equals(
                        assemblyName.Name,
                        AbstractionsAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(IPaperBodyPlugin).Assembly;
                }
                return null;
            }

            var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath == null
                ? null
                : LoadFromAssemblyPath(dependencyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var dependencyPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return dependencyPath == null
                ? IntPtr.Zero
                : LoadUnmanagedDllFromPath(dependencyPath);
        }
    }
}
