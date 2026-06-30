using System.Globalization;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Date-time based module version: <c>yyyy.MM.dd.HHmm</c>.
/// Chronological comparison: newer date-time = higher version.
/// </summary>
public readonly struct ModuleVersion : IComparable<ModuleVersion>, IEquatable<ModuleVersion>
{
    private const string Format = "yyyy.MM.dd.HHmm";

    /// <summary>Parsed date-time of the version.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Original version string.</summary>
    public string Value { get; }

    /// <summary>Whether this represents a valid parsed version.</summary>
    public bool IsValid { get; }

    private ModuleVersion(DateTime timestamp, string value, bool isValid)
    {
        Timestamp = timestamp;
        Value = value;
        IsValid = isValid;
    }

    /// <summary>Creates a version from the current UTC time.</summary>
    public static ModuleVersion Now()
    {
        var now = DateTime.UtcNow;
        return new ModuleVersion(now, now.ToString(Format, CultureInfo.InvariantCulture), true);
    }

    /// <summary>Parses a version string in <c>yyyy.MM.dd.HHmm</c> format.</summary>
    public static ModuleVersion Parse(string version)
    {
        if (TryParse(version, out var result))
            return result;
        throw new FormatException($"Invalid module version format: '{version}'. Expected: {Format}");
    }

    /// <summary>Tries to parse a version string.</summary>
    public static bool TryParse(string? version, out ModuleVersion result)
    {
        if (!string.IsNullOrWhiteSpace(version) &&
            DateTime.TryParseExact(version, Format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            result = new ModuleVersion(dt, version, true);
            return true;
        }

        result = new ModuleVersion(default, version ?? string.Empty, false);
        return false;
    }

    /// <summary>Returns true if <paramref name="newer"/> is a higher version than <paramref name="older"/>.</summary>
    public static bool IsNewer(string? newer, string? older)
    {
        if (!TryParse(newer, out var n)) return false;
        if (!TryParse(older, out var o)) return true; // any valid > invalid
        return n.Timestamp > o.Timestamp;
    }

    public int CompareTo(ModuleVersion other) => Timestamp.CompareTo(other.Timestamp);
    public bool Equals(ModuleVersion other) => Timestamp == other.Timestamp;
    public override bool Equals(object? obj) => obj is ModuleVersion other && Equals(other);
    public override int GetHashCode() => Timestamp.GetHashCode();
    public override string ToString() => Value;

    public static bool operator ==(ModuleVersion left, ModuleVersion right) => left.Equals(right);
    public static bool operator !=(ModuleVersion left, ModuleVersion right) => !left.Equals(right);
    public static bool operator <(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) >= 0;
}
