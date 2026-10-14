using System.Text;
using System.Text.RegularExpressions;

namespace EdgeGateway.Services;

// Keeps every console line, so nothing is lost when the gateway runs in the
// background with nobody watching the window:
//   * writes each line, time-stamped, to logs\gateway-yyyy-MM-dd.log next to
//     the exe; one file per day, files older than KeepDays are deleted
//   * remembers the last lines in memory for the config page (Log view)
// It sits behind Console.Out / Console.Error, so the existing
// Console.WriteLine calls need no change.
public sealed class GatewayLog
{
    private const int MaxLines = 500;

    // Words the gateway already uses when something went wrong.
    private static readonly Regex ProblemWords = new(
        @"\b(fail(ed|s)?|cannot|can't|dropped|error|stopped|refused|exception|warning)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _logDir;
    private readonly int _keepDays;
    private readonly object _sync = new();
    private readonly LinkedList<LogLine> _recent = new();
    private long _nextId = 1;
    private long _errorCount;
    private DateTime _fileDate;
    private StreamWriter? _file;

    private GatewayLog(string logDir, int keepDays)
    {
        _logDir = logDir;
        _keepDays = keepDays;
    }

    // Call once, first thing in Program.cs.
    public static GatewayLog Install(string logDir, int keepDays)
    {
        var log = new GatewayLog(logDir, keepDays);
        Console.SetOut(new TeeWriter(Console.Out, log, isError: false));
        Console.SetError(new TeeWriter(Console.Error, log, isError: true));
        return log;
    }

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    // Lines newer than afterId, oldest first.
    public IReadOnlyList<LogLine> Since(long afterId)
    {
        lock (_sync)
            return _recent.Where(l => l.Id > afterId).ToList();
    }

    private void Add(string text, bool fromErrorStream)
    {
        if (text.Length == 0)
            return;

        var now = DateTime.Now;
        var isError = fromErrorStream || ProblemWords.IsMatch(text);

        lock (_sync)
        {
            var line = new LogLine(_nextId++, now, text, isError);
            _recent.AddLast(line);
            if (_recent.Count > MaxLines)
                _recent.RemoveFirst();
            if (isError)
                _errorCount++;

            try
            {
                FileFor(now).WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} {(isError ? "ERR " : "    ")}{text}");
            }
            catch
            {
                // Disk full / no access: keep running, the page still has the lines.
            }
        }
    }

    // Opens today's file; on a new day closes yesterday's and deletes old ones.
    private StreamWriter FileFor(DateTime now)
    {
        if (_file is not null && _fileDate == now.Date)
            return _file;

        _file?.Dispose();
        Directory.CreateDirectory(_logDir);
        _fileDate = now.Date;
        _file = new StreamWriter(
            new FileStream(Path.Combine(_logDir, $"gateway-{now:yyyy-MM-dd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };

        DeleteOldFiles(now);
        return _file;
    }

    private void DeleteOldFiles(DateTime now)
    {
        foreach (var path in Directory.GetFiles(_logDir, "gateway-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(path) < now.Date.AddDays(-_keepDays))
                    File.Delete(path);
            }
            catch
            {
                // in use or no access: try again tomorrow
            }
        }
    }

    // Passes everything to the real console and hands complete lines to the log.
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly GatewayLog _log;
        private readonly bool _isError;
        private readonly StringBuilder _line = new();

        public TeeWriter(TextWriter console, GatewayLog log, bool isError)
        {
            _console = console;
            _log = log;
            _isError = isError;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            Collect(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            _console.Write(value);
            Collect(value);
        }

        public override void WriteLine(string? value)
        {
            _console.WriteLine(value);
            Collect((value ?? "") + "\n");
        }

        public override void WriteLine()
        {
            _console.WriteLine();
            Collect("\n");
        }

        // Buffers text until a newline, then logs the finished line.
        private void Collect(string text)
        {
            lock (_line)
            {
                foreach (var c in text)
                {
                    if (c == '\n')
                    {
                        _log.Add(_line.ToString().TrimEnd('\r'), _isError);
                        _line.Clear();
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }
        }

        public override void Flush() => _console.Flush();
    }
}

public sealed record LogLine(long Id, DateTime Time, string Text, bool IsError);
