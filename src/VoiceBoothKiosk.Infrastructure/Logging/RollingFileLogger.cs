using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceBoothKiosk.Infrastructure.Logging;

public enum AppLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

/// <summary>A dependency-free, bounded JSON-lines logger suitable for support bundles.</summary>
public sealed class RollingFileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private int _retentionDays;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private int _sequence;
    private long _approximateBytes;
    private bool _disposed;

    public RollingFileLogger(
        string directory,
        AppLogLevel minimumLevel = AppLogLevel.Information,
        long maxFileBytes = 5 * 1024 * 1024,
        int retentionDays = 14)
    {
        if (maxFileBytes < 64 * 1024) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        _directory = Path.GetFullPath(directory);
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(_directory);
        RemoveExpiredLogs();
    }

    public AppLogLevel MinimumLevel { get; set; }

    public void UpdatePolicy(AppLogLevel minimumLevel, int retentionDays)
    {
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MinimumLevel = minimumLevel;
            _retentionDays = retentionDays;
            RemoveExpiredLogs();
        }
    }

    public void Write(
        AppLogLevel level,
        int eventId,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (level < MinimumLevel || MinimumLevel == AppLogLevel.None)
        {
            return;
        }

        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = level.ToString(),
            eventId,
            message,
            properties,
            exception = exception is null ? null : new
            {
                type = exception.GetType().FullName,
                exception.Message,
                exception.StackTrace
            }
        };
        var line = JsonSerializer.Serialize(entry);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWriter(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
            _writer!.WriteLine(line);
            _writer.Flush();
            _approximateBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        }
    }

    public static string RedactName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "empty";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureWriter(int pendingBytes)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today && _approximateBytes + pendingBytes <= _maxFileBytes)
        {
            return;
        }

        _writer?.Dispose();
        if (_writerDate != today)
        {
            _sequence = 0;
        }
        _writerDate = today;

        string path;
        do
        {
            _sequence++;
            path = Path.Combine(_directory, $"voicebooth-{today:yyyyMMdd}-{_sequence:000}.jsonl");
        }
        while (File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
        _approximateBytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private void RemoveExpiredLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var path in Directory.EnumerateFiles(_directory, "voicebooth-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
