// kai_tactics_log.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Central logging helper. Everything in these files runs inside a native hook
// or a per-tick listener, where there is no debugger and no useful stack
// trace, only the console. So every function calls KaiLog.Event at least
// once, and the noisy per-tick paths are gated behind a verbosity level that
// can be changed at runtime without a rebuild.
//
//   kai_log 0   errors only
//   kai_log 1   lifecycle, role assignment, phase changes (default)
//   kai_log 2   everything, including per-tick detail
//
// FILE OUTPUT
//
// Every call that reaches Event or Throttled also writes to a log file under
// kai_tactics/logs/, alongside the ordinary console output. Each line carries
// its own UTC timestamp, because the server console does not stamp its own
// output, so this is the way to review what the plugin decided without the
// rest of the server's console noise mixed in.
//
// A file is opened when the plugin loads and rolled over on every map change,
// so a hot reload mid-map still produces a log rather than waiting silently
// for the next map. Files are named with the map and the open time, so
// sessions never overwrite each other.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace KaiBotTactics;

public enum KaiLogLevel
{
    // Only failures. These mean something is not working.
    Error = 0,

    // Lifecycle and decisions. Safe to leave on during normal play.
    Info = 1,

    // Per-tick detail. Floods the console, use only while investigating.
    Verbose = 2,
}

public static class KaiLog
{
    // Current verbosity, changed at runtime by the kai_log console command.
    // Applies to both the console and the file; there is deliberately no
    // separate file verbosity, since the point is to capture what kai_log N
    // is actually showing.
    // Defaults to Verbose. The per-bot decision lines are the whole point of
    // the file sink, and at Info they are all suppressed, which makes the log
    // look healthy while telling you nothing about what the bots actually did.
    // kai_log 1 quietens it again if the volume becomes a problem.
    public static KaiLogLevel Level { get; set; } = KaiLogLevel.Verbose;

    // Whether the file sink is active. On by default; kai_logfile off stops
    // writing without losing the console output.
    public static bool FileEnabled { get; set; } = true;

    // How many log files to keep. Older ones are deleted when a new file is
    // opened, so an unattended machine does not fill its disk over weeks of
    // sessions at kai_log 2.
    public static int KeepFiles { get; set; } = 20;

    // Rate limiter so a Verbose line inside a per-tick hook does not print
    // 64 times a second per bot. Key is supplied by the caller. Shared
    // between console and file so the two never disagree about what fired.
    private static readonly Dictionary<string, float> _lastPrint = new();

    private static StreamWriter? _writer;
    private static string? _currentPath;

    // Buffered rather than flushed per line, because a busy round at
    // verbosity 2 produces a lot of lines and an fsync on each would be
    // pointless. Errors bypass this and flush immediately, since an error is
    // exactly the thing you want on disk if the process then dies.
    // Flush timing uses a managed monotonic clock, NOT Server.CurrentTime.
    //
    // Two reasons. Every Server property is a call straight through to native
    // code, and log lines are emitted during plugin load while the engine is
    // still starting and that code is not ready. And the game clock restarts
    // from zero on every map change, which used to need a special case here
    // to stop the buffer sitting unflushed until the new map caught up.
    // Environment.TickCount64 has neither problem.
    private static long _lastFlushMs;
    private const long FlushIntervalMs = 1000;

    // UTF-8 with no byte order mark. A BOM at the top of a log file confuses
    // tail, grep and anything else that expects plain text.
    private static readonly Encoding LogEncoding = new UTF8Encoding(false);

    // Open a log file. Safe to call repeatedly; any previous writer is closed
    // and flushed first.
    //
    // Called once at plugin load and again on every map change. The load-time
    // call matters: Listeners.OnMapStart does not fire on a css_plugins
    // reload, so opening only there would mean a hot reload produced no file
    // at all until the next map, which is exactly when a log is most wanted.
    public static void OpenForMap(string logDir, string mapName)
    {
        CloseCurrent();

        if (!FileEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(mapName))
        {
            mapName = "unknown_map";
        }

        try
        {
            Directory.CreateDirectory(logDir);

            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(logDir, $"{mapName}_{stamp}.log");

            var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                LogEncoding);

            writer.AutoFlush = false;
            _writer = writer;
            _currentPath = path;

            // Deliberately NOT seeded from Server.CurrentTime. This method
            // runs during plugin load, which happens while the engine is
            // still starting and before any map exists, and every Server
            // property is a call straight through to native code that is not
            // ready yet. Zero is a fine starting value: the first log line
            // then flushes immediately, which is what you want at startup
            // anyway.
            _lastFlushMs = 0;

            // Server.CurrentTime restarts from zero on a map change, which
            // would leave every throttle key holding a timestamp from the
            // future and suppress its line until something else cleared the
            // table. Clearing here removes that whole class of confusion.
            _lastPrint.Clear();

            WriteLineRaw($"# KaiBotTactics log opened {KaiTime.NowUtc()}");
            WriteLineRaw($"# map={mapName} verbosity={Level} keepFiles={KeepFiles}");

            writer.Flush();

            PruneOldLogs(logDir);
        }
        catch (Exception ex)
        {
            DisableSink();

            // Console only. The file sink itself just failed, so routing this
            // through the normal path would drop the one message explaining
            // why there is no file.
            Console.WriteLine($"[KaiTactics][ERROR] OpenForMap: could not open log file: {ex.Message}");
        }
    }

    // Write an unstamped comment line to the file only. Used at startup to
    // record the plugin version and which optional capabilities resolved,
    // which is the context needed to interpret everything below it.
    public static void Note(string line)
    {
        if (_writer == null)
        {
            return;
        }

        WriteLineRaw($"# {line}");
        FlushNow();
    }

    public static string? CurrentLogPath
    {
        get { return _currentPath; }
    }

    public static void CloseCurrent()
    {
        if (_writer == null)
        {
            return;
        }

        try
        {
            WriteLineRaw($"# KaiBotTactics log closed {KaiTime.NowUtc()}");
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // Best effort. A failure here must not stop a map change or a
            // plugin unload from completing.
        }

        _writer = null;
        _currentPath = null;
    }

    // Drop the writer without trying to use it again. Used on any write
    // failure, so a disk that has filled or a file that has been locked does
    // not produce one error line per log call for the rest of the session.
    private static void DisableSink()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Nothing useful to do; the sink is being abandoned regardless.
        }

        _writer = null;
        _currentPath = null;
    }

    // Keep only the newest KeepFiles logs in the directory.
    private static void PruneOldLogs(string logDir)
    {
        if (KeepFiles <= 0)
        {
            return;
        }

        try
        {
            var files = new DirectoryInfo(logDir)
                .GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepFiles)
                .ToList();

            foreach (var file in files)
            {
                file.Delete();
            }

            if (files.Count > 0)
            {
                WriteLineRaw($"# pruned {files.Count} old log file(s), keeping newest {KeepFiles}");
            }
        }
        catch (Exception ex)
        {
            // Not worth failing the whole sink over; the current file is open
            // and usable either way.
            Console.WriteLine($"[KaiTactics][ERROR] PruneOldLogs: {ex.Message}");
        }
    }

    private static void WriteLineRaw(string line)
    {
        try
        {
            _writer?.WriteLine(line);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KaiTactics][ERROR] log write failed, file sink disabled: {ex.Message}");
            DisableSink();
        }
    }

    // Log a single event. Every function in these files should call this.
    //
    //   func     the calling function name, so a line can be traced back to
    //            source without guessing
    //   message  what happened, including the values involved
    public static void Event(string func, string message, KaiLogLevel level = KaiLogLevel.Info)
    {
        if (level > Level)
        {
            return;
        }

        string tag;

        if (level == KaiLogLevel.Error)
        {
            tag = "ERROR";
        }
        else if (level == KaiLogLevel.Verbose)
        {
            tag = "VERB";
        }
        else
        {
            tag = "INFO";
        }

        Console.WriteLine($"[KaiTactics][{tag}] {func}: {message}");

        if (_writer == null)
        {
            return;
        }

        WriteLineRaw($"{KaiTime.NowUtc()} [{tag}] {func}: {message}");

        if (level == KaiLogLevel.Error)
        {
            // An error is the thing you go looking for after a crash, so it
            // goes to disk now rather than waiting for the next flush.
            FlushNow();
        }
        else
        {
            MaybeFlush();
        }
    }

    // As Event, but will not repeat the same key more often than
    // intervalSeconds. Use inside per-tick code so the log stays readable.
    public static void Throttled(
        string key,
        string func,
        string message,
        float intervalSeconds = 1.0f,
        KaiLogLevel level = KaiLogLevel.Verbose)
    {
        if (level > Level)
        {
            return;
        }

        float now = Server.CurrentTime;

        if (_lastPrint.TryGetValue(key, out float last))
        {
            // now < last means the server clock restarted, on a map change or
            // a game restart. Treat the stored time as stale rather than
            // suppressing this key until the clock catches up, which for a
            // long previous session could be hours.
            if (now >= last && now - last < intervalSeconds)
            {
                return;
            }
        }

        _lastPrint[key] = now;
        Event(func, message, level);
    }

    private static void FlushNow()
    {
        _lastFlushMs = Environment.TickCount64;

        try
        {
            _writer?.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KaiTactics][ERROR] log flush failed, file sink disabled: {ex.Message}");
            DisableSink();
        }
    }

    private static void MaybeFlush()
    {
        if (Environment.TickCount64 - _lastFlushMs < FlushIntervalMs)
        {
            return;
        }

        FlushNow();
    }

    // Clear the throttle table. Called on round start so the first tick of a
    // new round always logs regardless of what the previous round did.
    public static void ResetThrottles()
    {
        int cleared = _lastPrint.Count;
        _lastPrint.Clear();
        Event(nameof(ResetThrottles), $"cleared {cleared} throttle entries");
    }

    // Echo to every connected human console as well as the server console and
    // the log file. Used by the authoring commands, where the result needs to
    // be visible immediately without alt-tabbing.
    public static void ToHumans(string func, string message)
    {
        Event(func, message);

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV)
            {
                continue;
            }

            p.PrintToConsole($"[KaiTactics] {message}");
        }
    }
}
