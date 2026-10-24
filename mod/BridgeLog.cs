using System;
using System.IO;

namespace Sts2AgentBridge;

/// <summary>File logger; the game captures stdout, so we keep our own log next to the game logs.</summary>
public static class BridgeLog
{
    private static readonly object Gate = new();
    private static string _path;

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "agent_bridge.log");
            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception e = null) => Write("ERROR", message, e);

    private static void Write(string level, string message, Exception e)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" +
                                         (e != null ? Environment.NewLine + e : string.Empty) + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never break the game
        }
    }
}
