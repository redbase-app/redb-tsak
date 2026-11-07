using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace redb.Tsak.CLI.Config;

/// <summary>
/// Manages connection profiles stored as YAML files in ~/.tsak/profiles/.
/// </summary>
public sealed class ProfileManager
{
    private static readonly string DefaultBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tsak");

    private readonly string _profileDir;
    private readonly string _activeFilePath;

    /// <summary>
    /// Initializes the profile manager with the default base directory (~/.tsak/).
    /// </summary>
    public ProfileManager() : this(DefaultBaseDir) { }

    /// <summary>
    /// Initializes the profile manager with a custom base directory (for testing).
    /// </summary>
    /// <param name="baseDir">Base directory containing profiles/ and active marker.</param>
    public ProfileManager(string baseDir)
    {
        _profileDir = Path.Combine(baseDir, "profiles");
        _activeFilePath = Path.Combine(baseDir, "active");
    }

    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Resolves the effective connection settings.
    /// Priority: explicit flags > environment variables > active profile.
    /// </summary>
    /// <param name="profileName">Profile name from --profile flag.</param>
    /// <param name="url">URL from --url flag.</param>
    /// <param name="apiKey">API key from --key flag.</param>
    /// <returns>Resolved profile, or null if nothing is configured.</returns>
    public ConnectionProfile? Resolve(string? profileName, string? url, string? apiKey)
    {
        // Start from the named or active profile
        ConnectionProfile? profile = null;
        if (profileName is not null)
            profile = Load(profileName);
        else
            profile = LoadActive();

        // Environment variables override profile
        var envUrl = Environment.GetEnvironmentVariable("TSAK_URL");
        var envKey = Environment.GetEnvironmentVariable("TSAK_API_KEY");

        var resolvedUrl = url ?? envUrl ?? profile?.Url;
        var resolvedKey = apiKey ?? envKey ?? profile?.ApiKey;

        if (resolvedUrl is null)
            return null;

        return new ConnectionProfile
        {
            Name = profile?.Name ?? "inline",
            Url = resolvedUrl,
            ApiKey = resolvedKey
        };
    }

    /// <summary>
    /// Lists all saved profiles.
    /// </summary>
    public IReadOnlyList<ConnectionProfile> List()
    {
        if (!Directory.Exists(_profileDir))
            return [];

        var files = Directory.GetFiles(_profileDir, "*.yml");
        var profiles = new List<ConnectionProfile>(files.Length);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var loaded = Load(name);
            if (loaded is not null)
                profiles.Add(loaded);
        }
        return profiles;
    }

    /// <summary>
    /// Loads a profile by name.
    /// </summary>
    public ConnectionProfile? Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path))
            return null;

        var yaml = File.ReadAllText(path);
        var data = YamlReader.Deserialize<ProfileData>(yaml);
        return new ConnectionProfile
        {
            Name = name,
            Url = data.Url,
            ApiKey = data.ApiKey
        };
    }

    /// <summary>
    /// Saves a profile to disk.
    /// </summary>
    public void Save(ConnectionProfile profile)
    {
        Directory.CreateDirectory(_profileDir);
        var data = new ProfileData { Url = profile.Url, ApiKey = profile.ApiKey };
        var yaml = YamlWriter.Serialize(data);
        File.WriteAllText(GetProfilePath(profile.Name), yaml);
    }

    /// <summary>
    /// Deletes a profile.
    /// </summary>
    public bool Delete(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        // If it was the active profile, remove the active marker
        if (GetActiveName() == name)
            DeleteActiveMarker();
        return true;
    }

    /// <summary>
    /// Sets the active profile.
    /// </summary>
    public void SetActive(string name)
    {
        if (!File.Exists(GetProfilePath(name)))
            throw new FileNotFoundException($"Profile '{name}' does not exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(_activeFilePath)!);
        File.WriteAllText(_activeFilePath, name);
    }

    /// <summary>
    /// Gets the name of the active profile.
    /// </summary>
    public string? GetActiveName()
    {
        return File.Exists(_activeFilePath) ? File.ReadAllText(_activeFilePath).Trim() : null;
    }

    private ConnectionProfile? LoadActive()
    {
        var name = GetActiveName();
        return name is not null ? Load(name) : null;
    }

    private void DeleteActiveMarker()
    {
        if (File.Exists(_activeFilePath))
            File.Delete(_activeFilePath);
    }

    private string GetProfilePath(string name) =>
        Path.Combine(_profileDir, $"{name}.yml");

    /// <summary>
    /// Internal YAML shape for profile files.
    /// </summary>
    private sealed class ProfileData
    {
        public string Url { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
    }
}
