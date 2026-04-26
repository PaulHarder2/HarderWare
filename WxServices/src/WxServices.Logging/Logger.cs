using System.Runtime.CompilerServices;

using log4net;
using log4net.Config;

namespace WxServices.Logging;

/// <summary>
/// Static application logger backed by log4net.
/// Call <see cref="Initialise(string)"/> once at application startup before any
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
    /// Configures log4net by loading <c>log4net.shared.config</c> from the same
    /// directory as the running executable.  The log file path is set via a
    /// log4net <c>GlobalContext</c> property before configuration, so the shared
    /// config can use <c>%property{LogFile}</c> in its appender.
    /// </summary>
    /// <param name="logFilePath">
    /// Full path to the log file for this service (e.g.
    /// <c>C:\HarderWare\Logs\wxparser-svc.log</c>).  The directory is created
    /// if it does not exist.
    /// </param>
    /// <sideeffects>
    /// Creates the log directory if it does not exist.
    /// Sets <c>log4net.GlobalContext.Properties["LogFile"]</c>.
    /// Initialises and starts watching <c>log4net.shared.config</c> via <see cref="XmlConfigurator"/>.
    /// Writes a warning to <c>stderr</c> if the log directory cannot be created.
    /// </sideeffects>
    public static void Initialise(string logFilePath)
    {
        // Ensure the log directory exists.
        var logDir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(logDir))
        {
            try
            {
                Directory.CreateDirectory(logDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: could not create log directory '{logDir}': {ex.Message}");
            }
        }

        // Set the property BEFORE loading the config so PatternString can resolve it.
        GlobalContext.Properties["LogFile"] = logFilePath;

        var configPath = Path.Combine(AppContext.BaseDirectory, "log4net.shared.config");
        XmlConfigurator.ConfigureAndWatch(new FileInfo(configPath));
    }

    /// <summary>
    /// Legacy overload for callers that do not yet supply a log file path.
    /// Loads <c>log4net.shared.config</c> without setting a log file property;
    /// the appender will use whatever default log4net assigns (typically the
    /// working directory).
    /// </summary>
    [Obsolete("Pass a log file path via Initialise(string logFilePath).")]
    public static void Initialise()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "log4net.shared.config");
        if (File.Exists(configPath))
        {
            XmlConfigurator.ConfigureAndWatch(new FileInfo(configPath));
            return;
        }

        // Fallback: try the old per-service config name.
        configPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");
        if (File.Exists(configPath))
            XmlConfigurator.ConfigureAndWatch(new FileInfo(configPath));
    }

    /// <summary>Logs a DEBUG-level message.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Debug(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsDebugEnabled)
            _log.Debug(Format(message, member, file, line));
    }

    /// <summary>Logs an INFO-level message.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Info(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsInfoEnabled)
            _log.Info(Format(message, member, file, line));
    }

    /// <summary>Logs a WARN-level message.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Warn(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsWarnEnabled)
            _log.Warn(Format(message, member, file, line));
    }

    /// <summary>Logs a WARN-level message with an exception.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">The exception to attach to the log entry.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Warn(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsWarnEnabled)
            _log.Warn(Format(message, member, file, line), ex);
    }

    /// <summary>Logs an ERROR-level message.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Error(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsErrorEnabled)
            _log.Error(Format(message, member, file, line));
    }

    /// <summary>Logs an ERROR-level message with an exception.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">The exception to attach to the log entry.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Error(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsErrorEnabled)
            _log.Error(Format(message, member, file, line), ex);
    }

    /// <summary>Logs a FATAL-level message.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Fatal(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsFatalEnabled)
            _log.Fatal(Format(message, member, file, line));
    }

    /// <summary>Logs a FATAL-level message with an exception.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">The exception to attach to the log entry.</param>
    /// <param name="member">Caller method name — supplied automatically by the compiler.</param>
    /// <param name="file">Caller source file path — supplied automatically by the compiler.</param>
    /// <param name="line">Caller line number — supplied automatically by the compiler.</param>
    public static void Fatal(
        string message,
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (_log.IsFatalEnabled)
            _log.Fatal(Format(message, member, file, line), ex);
    }

    /// <summary>
    /// Formats a log message by prepending a source location prefix
    /// derived from the CallerInfo attributes.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="member">Calling method name.</param>
    /// <param name="file">Calling source file path (only the filename is used).</param>
    /// <param name="line">Calling line number.</param>
    /// <returns>A formatted string such as <c>"[FetchWorker.cs::FetchCycleAsync:97] message"</c>.</returns>
    private static string Format(string message, string member, string file, int line)
        => $"[{Path.GetFileName(file)}::{member}:{line}] {message}";
}