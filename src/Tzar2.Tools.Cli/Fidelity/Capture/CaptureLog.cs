using System;
using System.Globalization;
using System.IO;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>Progress output for the operator (console) mirrored into <c>log.txt</c> with timestamps.</summary>
internal sealed class CaptureLog : IDisposable
{
    private readonly TextWriter _console;
    private readonly StreamWriter? _file;
    private readonly object _gate = new();

    public CaptureLog(TextWriter console, string? filePath)
    {
        _console = console;
        if (filePath is not null)
        {
            _file = new StreamWriter(filePath, append: true) { AutoFlush = true };
        }
    }

    public int Warnings { get; private set; }

    public int Errors { get; private set; }

    public void Info(string message) => Write("INFO ", message, toConsole: true);

    /// <summary>Detail that only goes to the log file.</summary>
    public void Detail(string message) => Write("DEBUG", message, toConsole: false);

    public void Warn(string message)
    {
        Warnings++;
        Write("WARN ", message, toConsole: true);
    }

    public void Error(string message)
    {
        Errors++;
        Write("ERROR", message, toConsole: true);
    }

    private void Write(string level, string message, bool toConsole)
    {
        lock (_gate)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (toConsole)
            {
                _console.WriteLine(level == "INFO " ? $"[{stamp}] {message}" : $"[{stamp}] {level.Trim()}: {message}");
            }

            _file?.WriteLine($"{DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {level} {message}");
        }
    }

    public void Dispose() => _file?.Dispose();
}
