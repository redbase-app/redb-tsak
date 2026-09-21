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

    /// <summary>
    /// XML route artifacts (Route-XML Ф5.1), read during <see cref="Open(byte[],string,DateTime,string[],ILogger,bool,bool,bool)"/>
    /// from the SAME verified bytes the signature gate checked — never re-read from disk.
    /// Order follows <see cref="ModuleManifest.Artifacts"/>. Empty for packages without artifacts.
    /// </summary>
    public IReadOnlyList<(string Name, string Content)> XmlArtifacts => _xmlArtifacts;
    private readonly List<(string Name, string Content)> _xmlArtifacts = [];

    /// <summary>
    /// Directory the package's <see cref="ModuleManifest.Resources"/> entries were extracted to
    /// (the package itself is never unpacked; artifact file-references need real file paths).
    /// Null when the package carries no XML artifacts. Deleted by <see cref="Dispose"/>.
    /// </summary>
    public string? ResourcesRoot { get; private set; }

    /// <summary>The package's context.xml content, when the package carries one (read in Open).</summary>
    public string? ContextXml { get; private set; }

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
            // Read once and open from the buffer — the same bytes a signature gate may have
            // verified must be the bytes that get loaded (review 2026-09-02, К3: the old
            // verify-then-reopen let the file be swapped between the check and the load).
            var bytes = File.ReadAllBytes(tpkgPath);
            return Open(bytes, tpkgPath, File.GetLastWriteTimeUtc(tpkgPath), probePaths, logger, forceReload, collectible);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to open package {Path}", tpkgPath);
            return null;
        }
    }

    /// <summary>
    /// Opens a .tpkg from an in-memory buffer (the caller verified these exact bytes).
    /// <paramref name="isolatedCompanions"/> loads companion DLLs into the package's own ALC
    /// instead of publishing them to the process-wide tracker — for staged validation, whose
    /// probe must not leak a rejected package's dependencies into the Default ALC
    /// (review 2026-09-02, С23).
    /// </summary>
    public static ModulePackage? Open(
        byte[] packageBytes, string tpkgPath, DateTime lastWriteUtc,
        string[]? probePaths = null, ILogger? logger = null,
        bool forceReload = false, bool collectible = false, bool isolatedCompanions = false)
    {
        try
        {
            var lastWrite = lastWriteUtc;

            using var zip = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read);

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

                    Assembly assembly;
                    if (isolatedCompanions)
                    {
                        // Validation probe: keep the companion inside the throwaway ALC.
                        memStream.Position = 0;
                        assembly = alc.LoadFromStream(memStream);
                    }
                    else
                    {
                        var asmName = GetAssemblyNameFromBytes(bytes);
                        assembly = forceReload
                            ? LoadedAssemblyTracker.Replace(asmName, bytes)
                            : LoadedAssemblyTracker.LoadOrReuse(asmName, bytes, logger);
                    }

                    package.CompanionAssemblies.Add(assembly);

                    logger?.LogDebug("Loaded companion {DLL} from package {Name} (reused={Reused}, isolated={Isolated})",
                        entry.FullName, manifest.Name, !forceReload, isolatedCompanions);
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

            // XML route artifacts (Route-XML Ф5): read from the verified zip in manifest order,
            // and extract the resources directory so file-references resolve to real paths.
            if (manifest.Artifacts.Count > 0)
                package.ReadXmlArtifacts(zip, logger);

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

    private void ReadXmlArtifacts(ZipArchive zip, ILogger? logger)
    {
        // The optional context.xml at the package root (Route-XML Ф5.1 layout): context-level
        // sections — components, beans, onInit, package blocks like <redb> — applied by
        // XmlRouteModule BEFORE the route artifacts, from the same verified bytes.
        if (zip.GetEntry("context.xml") is { } contextEntry)
        {
            using var contextReader = new StreamReader(contextEntry.Open());
            ContextXml = contextReader.ReadToEnd();
        }

        foreach (var artifact in Manifest.Artifacts)
        {
            var entry = zip.GetEntry(artifact.Replace('\\', '/'));
            if (entry is null)
            {
                // The packaging gate guarantees presence; a hand-edited manifest may not.
                // Recorded as a missing artifact — XmlRouteModule fails initialization loudly.
                logger?.LogError("Package {Name}: artifact {Artifact} listed in the manifest is not in the package",
                    Manifest.Name, artifact);
                continue;
            }
            using var reader = new StreamReader(entry.Open());
            _xmlArtifacts.Add((artifact, reader.ReadToEnd()));
        }

        var resourcesPrefix = Manifest.Resources.Trim('/') + "/";
        var resourceEntries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith(resourcesPrefix, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(e.Name))
            .ToList();

        // A per-INSTANCE directory, not a per-content one. Several ModulePackage instances of the
        // same file are alive at once by design (registry discovery + the hot-reload tracker + the
        // staged-validation probe), and a content-keyed path made them share one directory — the
        // probe's Dispose deleted the resources out from under the live module (такт 5 E2E find).
        var root = Path.Combine(Path.GetTempPath(), "tsak-pkg",
            $"{Manifest.Name}-{Guid.NewGuid().ToString("N")[..16]}");
        Directory.CreateDirectory(root);
        foreach (var entry in resourceEntries)
        {
            var relative = entry.FullName.Replace('\\', '/')[resourcesPrefix.Length..];
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogError("Package {Name}: resource entry {Entry} escapes the resources directory — skipped",
                    Manifest.Name, entry.FullName);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        ResourcesRoot = root;
        logger?.LogInformation("Package {Name}: {Artifacts} XML artifact(s), {Resources} resource file(s) → {Root}",
            Manifest.Name, _xmlArtifacts.Count, resourceEntries.Count, root);
    }

    public void Dispose()
    {
        Alc?.TryUnload();
        Alc = null;
        if (ResourcesRoot is { } root)
        {
            ResourcesRoot = null;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Package {Name}: could not delete extracted resources at {Root}",
                    Manifest.Name, root);
            }
        }
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
