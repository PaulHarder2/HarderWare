namespace WxServices.Common;

/// <summary>
/// General-purpose utility methods available to all WxServices projects.
/// </summary>
public static class Util
{
    /// <summary>
    /// Does nothing.  Useful as a no-op breakpoint target during manual debugging
    /// sessions when you want to inspect a value without affecting program flow.
    /// </summary>
    /// <param name="obj">Any value; ignored.</param>
    public static void Ignore(object? obj = null) { }
}
