namespace MetarParser.Data.Configuration;

/// <summary>
/// The single definition of which configuration keys are <em>bootstrap-critical</em> — keys that
/// stay file-sourced and are never carried in the DB <c>Config</c> table (WX-313 / WX-307).
///
/// <para>Extracted from <see cref="DbConfigurationProvider"/> (WX-314) so the read side and the
/// write sides share one rule: the provider refuses to overlay these (belt), the setup console
/// refuses to seed them, and WX-315's Configure tab refuses to write them (suspenders). Three
/// copies of this list would be three chances to drift.</para>
/// </summary>
public static class BootstrapKeys
{
    /// <summary>
    /// Config sections that stay file-sourced and are never overlaid from the DB — each is
    /// consumed before the post-schema reload runs (or, for the connection string, is what the
    /// provider needs to reach the DB at all), so a DB value would be read too late or be
    /// circular. Matched case-insensitively, by prefix:
    /// <list type="bullet">
    /// <item><c>ConnectionStrings:</c> — circular: the provider needs the connection string to reach the very DB it would read the override from.</item>
    /// <item><c>Database:StartupRetry:</c> — governs reaching the DB; consumed before this load.</item>
    /// <item><c>Telemetry:</c> — wired at host-build time (AddWxTelemetry), before the reload.</item>
    /// </list>
    /// </summary>
    public static readonly string[] SectionPrefixes =
    {
        "ConnectionStrings:",
        "Database:StartupRetry:",
        "Telemetry:",
    };

    /// <summary>
    /// The one bootstrap key matched by EXACT equality. The rest of <c>Claude:</c> (Model,
    /// MaxTokens, …) is DB-configurable (WX-307), so a prefix match would wrongly block a sibling
    /// such as <c>Claude:TimeoutSecondsOverride</c>.
    /// </summary>
    public const string ClaudeTimeoutKey = "Claude:TimeoutSeconds";

    /// <summary>True when <paramref name="key"/> must stay file-sourced.</summary>
    public static bool IsBootstrapKey(string key) =>
        key.Equals(ClaudeTimeoutKey, StringComparison.OrdinalIgnoreCase)
        || SectionPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}