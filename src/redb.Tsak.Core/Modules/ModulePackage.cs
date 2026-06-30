using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Loads a .tpkg (ZIP archive) containing manifest.json + module DLLs + per-module config.
/// Each package gets its own <see cref="ModuleAssemblyLoadContext"/>: entry point DLLs are
/// loaded into the package ALC (isolating module code), while companion DLLs go through
/// <see cref="LoadedAssemblyTracker"/> into the Default ALC (shared across packages).
/// Like WSO2 MI CAR files — drop into Libs/, auto-discovered and hot-reloaded.
/// </summary>
public sealed class ModulePackage : IDisposable
{
    private readonly ILogger? _logger;

    public ModuleManifest Manifest { get; }
    public string PackagePath { get; }
    public DateTime LastWriteUtc { get; }

    /// <summary>
    /// The isolated ALC owning this package's entry point assemblies.
    /// Null only for legacy callers that don't pass <c>collectible</c>.
    /// Call <see cref="Dispose"/> to unload (if collectible).
    /// </summary>
    public ModuleAssemblyLoadContext? Alc { get; private set; }

    /// <summary>Entry point assemblies loaded into <see cref="Alc"/> for module discovery.</summary>
    public List<Assembly> LoadedAssemblies { get; } = [];

    /// <summary>Companion (non-entry-point) assemblies loaded into Default ALC via Tracker.</summary>
    public List<Assembly> CompanionAssemblies { get; } = [];

    private ModulePackage(ModuleManifest manifest, string packagePath, DateTime lastWriteUtc, ILogger? logger)
    {
        Manifest = manifest;
        PackagePath = packagePath;
        LastWriteUtc = lastWriteUtc;
        _logger = logger;
    }

    /// <summary>
    /// Opens a .tpkg file, reads manifest, loads ALL DLLs from the archive.
    /// Companion DLLs are loaded before entry points so dependencies resolve correctly.
    /// Returns null if the file is not a valid .tpkg.
    /// </summary>
    /// <param name="forceReload">
    /// When true, companion assemblies are force-replaced in the tracker
    /// (hot-reload scenario: shared dependency DLL was updated).
    /// Entry points are always loaded fresh into a new ALC.
    /// When false (default / first load), existing tracked companions are reused.
    /// </param>
    /// <param name="collectible">
    /// When true, the package ALC is collectible (supports Unload).
    /// When false (default), the ALC stays in memory but is compatible with Emit-based APIs.
    /// </param>
    public static ModulePackage? Open(string tpkgPath, string[]? probePaths = null, ILogger? logger = null, bool forceReload = false, bool collectible = false)
    {
        if (!File.Exists(tpkgPath))
            return null;

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(tpkgPath);

            using var zip = ZipFile.OpenRead(tpkgPath);

            // Read manifest
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                logger?.LogWarning("No manifest.json in {Path}, skipping", tpkgPath);
                return null;
            }

            ModuleManifest manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<ModuleManifest>(stream)
                           ?? throw new InvalidDataException("Failed to deserialize manifest.json");
            }

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                logger?.LogWarning("Manifest in {Path} has no Name, skipping", tpkgPath);
                return null;
            }

            var package = new ModulePackage(manifest, tpkgPath, lastWrite, logger);

            LoadedAssemblyTracker.EnsureResolverRegistered();

            // Create per-package ALC for entry point isolation
            var alcName = $"pkg:{manifest.Name}";
            var alc = new ModuleAssemblyLoadContext(alcName, probePaths, collectible);
            package.Alc = alc;

            var entryPointSet = new HashSet<string>(manifest.EntryPoints, StringComparer.OrdinalIgnoreCase);

            // Phase 1: Load companion DLLs (non-entry-point) so dependencies are available
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entryPointSet.Contains(entry.FullName))
                    continue;

                try
                {
                    using var dllStream = entry.Open();
                    using var memStream = new MemoryStream();
                    dllStream.CopyTo(memStream);
                    var bytes = memStream.ToArray();

                    var asmName = GetAssemblyNameFromBytes(bytes);
                    var assembly = forceReload
                        ? LoadedAssemblyTracker.Replace(asmName, bytes)
                        : LoadedAssemblyTracker.LoadOrReuse(asmName, bytes);

                    package.CompanionAssemblies.Add(assembly);

                    logger?.LogDebug("Loaded companion {DLL} from package {Name} (reused={Reused})",
                        entry.FullName, manifest.Name, !forceReload);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to load companion {DLL} from package {Name}",
                        entry.FullName, manifest.Name);
                }
            }

            // Phase 2: Load entry point DLLs into the package's isolated ALC.
            // Entry points always get a fresh load (per-package ALC is new).
            // NOT tracked in LoadedAssemblyTracker — entry points are isolated per-package.
            // If package B needs code from package A's entry point, it should declare
            // that as a companion (shared) dependency, not an entry point.
            foreach (var entryPoint in manifest.EntryPoints)
            {
                var dllEntry = zip.GetEntry(entryPoint);
                if (dllEntry is null)
                {
                    logger?.LogWarning("Entry point {EP} not found in {Path}", entryPoint, tpkgPath);
                    continue;
                }

                using var dllStream = dllEntry.Open();
                using var memStream = new MemoryStream();
                dllStream.CopyTo(memStream);
                memStream.Position = 0;

                // Load into package ALC (isolated entry point code)
                var assembly = alc.LoadFromStream(memStream);

                package.LoadedAssemblies.Add(assembly);

                logger?.LogInformation("Loaded entry point {EP} from package {Name} into ALC {Alc}",
                    entryPoint, manifest.Name, alcName);
            }

            if (package.CompanionAssemblies.Count > 0)
                logger?.LogInformation("Package {Name}: {EP} entry points, {Comp} companion DLLs loaded",
                    manifest.Name, package.LoadedAssemblies.Count, package.CompanionAssemblies.Count);

            return package;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to open package {Path}", tpkgPath);
            return null;
        }
    }

    /// <summary>
    /// Reads the per-module config ({moduleName}.config.json) from the package as raw JSON string.
    /// Returns null if no config for this module is embedded in the package.
    /// </summary>
    public string? ReadModuleConfigJson(string moduleName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(PackagePath);
            var configEntry = zip.GetEntry($"{moduleName}.config.json");
            if (configEntry is null)
                return null;

            using var reader = new StreamReader(configEntry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read config for module {Module} from {Path}",
                moduleName, PackagePath);
            return null;
        }
    }

    public void Dispose()
    {
        Alc?.TryUnload();
        Alc = null;
    }

    /// <summary>
    /// Reads assembly name from raw bytes without loading the assembly into the runtime.
    /// </summary>
    private static string GetAssemblyNameFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var assemblyDef = metadataReader.GetAssemblyDefinition();
        return metadataReader.GetString(assemblyDef.Name);
    }
}
