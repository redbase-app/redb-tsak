using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using redb.Core.Models.Configuration;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// Maps a configuration section onto <see cref="RedbServiceConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Both config paths — the unnamed instance in
/// <c>ServiceCollectionExtensions</c> and the named ones in <c>RedbInstanceFactory</c> — used to
/// copy properties across one hand-written line at a time. The lists went stale: of the 37 public
/// properties on <see cref="RedbServiceConfiguration"/>, the named path carried 15 and the unnamed
/// one 12, and the two sets were not even subsets of each other, so the same key behaved
/// differently depending on whether the instance had a name. <c>EnablePvtPrefilter</c> and
/// <c>StringCollation</c>, both added in 3.7, were simply the latest to fall through.
/// </para>
/// <para>
/// <b>The failure was silent, and that is the part worth fixing.</b> An unrecognised key produced
/// no diagnostic at all: <c>Tsak__Redb__EnablePvtPrefilter=true</c> did nothing and said nothing,
/// and so would a typo. Binding by reflection means a new core property becomes configurable on its
/// own, and reporting unknown keys means the next mistake announces itself instead of being found
/// months later by someone reading the source.
/// </para>
/// <para>
/// <b>This binder never overrides an explicit decision.</b> Callers apply it FIRST and then set
/// whatever they compute themselves (the save strategy, <c>EnsureCreated</c>, the legacy
/// <c>Tsak:Redb:Cache</c> subsection). Existing behaviour is therefore unchanged; the binder only
/// reaches properties nobody was setting.
/// </para>
/// </remarks>
internal static class RedbConfigBinder
{
    /// <summary>
    /// Keys that share the section with real settings but are not configuration properties:
    /// they select the provider or the tier. Listed so they are not reported as unknown.
    /// </summary>
    private static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "Provider", "ConnectionString", "UsePro", "License", "Cache", "ContextName", "Instances"
    };

    /// <summary>
    /// Deployed configs already use these names, and they do not match the property they set —
    /// the property is a <see cref="TimeSpan"/> while the key carries minutes. Binding alone would
    /// drop them silently, taking working settings away from everyone who upgrades.
    /// </summary>
    private static readonly Dictionary<string, string> MinuteAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PropsCacheTtlMinutes"] = nameof(RedbServiceConfiguration.PropsCacheTtl),
        ["ListCacheTtlMinutes"]  = nameof(RedbServiceConfiguration.ListCacheTtl),
    };

    /// <summary>
    /// Properties the binder must not touch: the connection string belongs to the instance and is
    /// supplied separately, and letting the same section rewrite it invites hard-to-trace mismatches.
    /// </summary>
    private static readonly HashSet<string> NeverBind = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(RedbServiceConfiguration.ConnectionString)
    };

    private static readonly Lazy<HashSet<string>> KnownProperties = new(() =>
        new HashSet<string>(
            typeof(RedbServiceConfiguration)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase));

    /// <param name="cfg">The configuration object being built.</param>
    /// <param name="section">The section holding the settings.</param>
    /// <param name="origin">How to name this section in diagnostics, e.g. <c>Tsak:Redb</c>.</param>
    /// <param name="report">Receives one line per applied or rejected key.</param>
    public static void Apply(
        RedbServiceConfiguration cfg,
        IConfiguration section,
        string origin,
        Action<string>? report = null)
    {
        if (section is null) return;

        var children = section.GetChildren().ToList();
        if (children.Count == 0) return;

        var applied = new List<string>();

        foreach (var child in children)
        {
            var key = child.Key;

            if (Infrastructure.Contains(key)) continue;

            if (MinuteAliases.TryGetValue(key, out var target))
            {
                if (int.TryParse(child.Value, out var minutes))
                {
                    if (target == nameof(RedbServiceConfiguration.PropsCacheTtl))
                        cfg.PropsCacheTtl = TimeSpan.FromMinutes(minutes);
                    else
                        cfg.ListCacheTtl = TimeSpan.FromMinutes(minutes);
                    applied.Add($"{key}={minutes}m");
                }
                else
                {
                    report?.Invoke($"[WARN] {origin}:{key} — expected a whole number of minutes, got '{child.Value}'. Ignored.");
                }
                continue;
            }

            if (NeverBind.Contains(key))
            {
                report?.Invoke($"[WARN] {origin}:{key} is set elsewhere and is ignored here.");
                continue;
            }

            if (!KnownProperties.Value.Contains(key))
            {
                report?.Invoke(
                    $"[WARN] {origin}:{key} is not a redb setting and does nothing. " +
                    "Check the spelling against RedbServiceConfiguration.");
                continue;
            }

            // A validating setter (StringCollation runs CollationNameValidator) throws from inside
            // the binder, where the message says nothing about which key was at fault. Bind one key
            // at a time so the failure can name itself; the cost is negligible at startup.
            try
            {
                var single = new ConfigurationBuilder()
                    .AddInMemoryCollection(Flatten(child, key))
                    .Build();
                single.Bind(cfg);
                applied.Add($"{key}={Describe(child)}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{origin}:{key} = '{child.Value}' was rejected: {ex.Message}", ex);
            }
        }

        if (applied.Count > 0)
            report?.Invoke($"[redb] {origin}: applied {string.Join(", ", applied)}");
    }

    /// <summary>Re-emits one section (scalar or subtree) as flat key/value pairs.</summary>
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(IConfigurationSection section, string prefix)
    {
        if (section.Value != null)
        {
            yield return new KeyValuePair<string, string?>(prefix, section.Value);
            yield break;
        }

        foreach (var child in section.GetChildren())
            foreach (var pair in Flatten(child, $"{prefix}:{child.Key}"))
                yield return pair;
    }

    private static string Describe(IConfigurationSection section) =>
        section.Value ?? "{…}";
}
