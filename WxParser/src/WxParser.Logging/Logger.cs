using log4net;
using log4net.Config;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace WxParser.Logging;

/// <summary>
/// Static application logger backed by log4net.
/// Call <see cref="Initialise"/> once at application startup before any
/// logging calls are made.
/// <para>
/// All log methods accept an interpolated string and automatically capture
/// the caller's source file name, method name, and line number at compile
/// time via CallerInfo attributes — zero runtime overhead, correct in both
/// Debug and Release builds.
/// </para>
/// </summary>
public static class Logger
{
    private static readonly ILog _log = LogManager.GetLogger("MetarParser");

    /// <summary>
    /// Configures log4net by loading <c>log4net.config</c> from the same
    /// directory as the running executable, and watches the file for changes
    /// so the log level can be adjusted at runtime without restarting.
    /// </summary>
    public static void Initialise()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");

        // Ensure the configured log directory exists before log4net tries to write to it.
        var logFile = XDocument.Load(configPath)
            .Descendants("file")
            .FirstOrDefault()
            ?.Attribute("value")
            ?.Value;
        if (!string.IsNullOrEmpty(logFile))
        {
            var logDir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(logDir))
            {
                try
                {
                    Directory.CreateDirectory(logDir); // no-op if it already exists
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: could not create log directory '{logDir}': {ex.Message}");
                }
            }
        }

        XmlConfigurator.ConfigureAndWatch(new FileInfo(configPath));
    }

    /// <summary>Logs a DEBUG-level message.</summary>
    public static void Debug(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsDebugEnabled)
            _log.Debug(Format(message, member, file, line));
    }

    /// <summary>Logs an INFO-level message.</summary>
    public static void Info(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsInfoEnabled)
            _log.Info(Format(message, member, file, line));
    }

    /// <summary>Logs a WARN-level message.</summary>
    public static void Warn(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsWarnEnabled)
            _log.Warn(Format(message, member, file, line));
    }

    /// <summary>Logs a WARN-level message with an exception.</summary>
    public static void Warn(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsWarnEnabled)
            _log.Warn(Format(message, member, file, line), ex);
    }

    /// <summary>Logs an ERROR-level message.</summary>
    public static void Error(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsErrorEnabled)
            _log.Error(Format(message, member, file, line));
    }

    /// <summary>Logs an ERROR-level message with an exception.</summary>
    public static void Error(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsErrorEnabled)
            _log.Error(Format(message, member, file, line), ex);
    }

    /// <summary>Logs a FATAL-level message.</summary>
    public static void Fatal(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsFatalEnabled)
            _log.Fatal(Format(message, member, file, line));
    }

    /// <summary>Logs a FATAL-level message with an exception.</summary>
    public static void Fatal(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath]   string file   = "",
        [CallerLineNumber] int    line   = 0)
    {
        if (_log.IsFatalEnabled)
            _log.Fatal(Format(message, member, file, line), ex);
    }

    private static string Format(string message, string member, string file, int line)
        => $"[{Path.GetFileName(file)}::{member}:{line}] {message}";
}
