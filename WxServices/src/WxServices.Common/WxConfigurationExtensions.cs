using Microsoft.Extensions.Configuration;

namespace WxServices.Common;

/// <summary>
/// Configuration-builder helpers shared by the Wx services.
/// </summary>
public static class WxConfigurationExtensions
{
    /// <summary>
    /// Projects the already-resolved install root into configuration under the single
    /// key <c>InstallRoot</c>, so that <c>IConfiguration["InstallRoot"]</c> — the value
    /// the workers read to build their <see cref="WxPaths"/> — always equals
    /// <see cref="WxPaths.ReadInstallRoot"/>, the one authority for where the runtime tree
    /// (Logs, plots, appsettings.local.json) lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, install root had two sources of truth: startup code used
    /// <see cref="WxPaths.ReadInstallRoot"/> (env <c>WXSERVICES_INSTALL_ROOT</c> →
    /// <c>appsettings.shared.json</c>), while the workers used
    /// <c>IConfiguration["InstallRoot"]</c> (<c>appsettings.shared.json</c> only). On
    /// Windows the two coincide, so the split was invisible; inside a container, where the
    /// env var moves the install root to a Linux path, they diverged — the workers resolved
    /// runtime paths (heartbeat, plots) under the baked-in <c>C:\HarderWare</c> instead.
    /// </para>
    /// <para>
    /// <see cref="WxPaths.ReadInstallRoot"/> also decides where <c>appsettings.local.json</c>
    /// is loaded from, so that file cannot coherently define its own location: the env/shared
    /// resolution is the only sound authority. This helper therefore MUST be added AFTER the
    /// JSON sources (last source wins) so any residual <c>InstallRoot</c> key in a local
    /// config is overridden rather than left to disagree. On Windows the injected value equals
    /// the shared-config value, so it is a no-op.
    /// </para>
    /// </remarks>
    /// <param name="builder">The configuration builder to append to.</param>
    /// <param name="installRoot">The resolved install root (from <see cref="WxPaths.ReadInstallRoot"/>).</param>
    public static IConfigurationBuilder AddInstallRoot(this IConfigurationBuilder builder, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstallRoot"] = installRoot,
        });
    }
}