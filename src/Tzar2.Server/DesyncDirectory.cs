using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Tzar2.Net.Relay;

namespace Tzar2.Server;

/// <summary>
/// Writes desync reports (REQ-NET-04) as <c>.t2desync</c> files on a background thread (the relay thread must not wait
/// on the disk) and keeps at most <c>max</c> of them, deleting the oldest. File names carry only the lobby code, the
/// match id and the tick: nothing a peer controls reaches the path.
/// </summary>
internal sealed class DesyncDirectory : IDesyncSink, IDisposable
{
    private readonly string _directory;
    private readonly int _max;
    private readonly Action<string> _log;
    private readonly BlockingCollection<DesyncBundle> _queue = new(boundedCapacity: 16);
    private readonly Thread _writer;

    public DesyncDirectory(string directory, int max, Action<string> log)
    {
        _directory = directory;
        _max = max;
        _log = log;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "desync writer" };
        _writer.Start();
    }

    /// <summary>Reports written so far.</summary>
    public int Written => Volatile.Read(ref _written);

    private int _written;
    private int _disposed;

    /// <inheritdoc />
    public void Store(DesyncBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!_queue.TryAdd(bundle))
        {
            _log("desync report dropped: the writer is behind");
        }
    }

    /// <summary>The file name of a bundle (relay-generated fields only).</summary>
    public static string FileName(DesyncBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        string code = RelayServer.NormalizeCode(bundle.Lobby);
        return string.Create(CultureInfo.InvariantCulture, $"desync-{code}-{bundle.MatchId:X16}-t{bundle.Tick}.t2desync");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(10));
        _queue.Dispose();
    }

    private void WriteLoop()
    {
        foreach (DesyncBundle bundle in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(_directory, FileName(bundle));
                File.WriteAllBytes(path, bundle.ToBytes());
                Interlocked.Increment(ref _written);
                _log("desync report written: " + path);
                Prune();
            }
            catch (IOException e)
            {
                _log("desync report not written: " + e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                _log("desync report not written: " + e.Message);
            }
        }
    }

    private void Prune()
    {
        FileInfo[] files = new DirectoryInfo(_directory).GetFiles("*.t2desync").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
        foreach (FileInfo old in files.Skip(_max))
        {
            old.Delete();
        }
    }
}
