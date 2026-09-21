using System.Text.Json;
using NAudio.Wave;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Stages all MP3s and lossless WAVs in a hidden-name sibling folder, verifies them, and exposes
/// the session folder only after every clip succeeds.
/// </summary>
public sealed class MediaFoundationExportService : IExportService, IExportPreflightService
{
    private const string CommitReceiptFileName = ".voicebooth-export.json";
    private const string RawFolderName = "Raw";
    private const int CopyBufferSize = 128 * 1024;
    private readonly MediaFoundationMp3Exporter exporter;
    private readonly string localExportRoot;

    public MediaFoundationExportService()
        : this(new MediaFoundationMp3Exporter(), null)
    {
    }

    public MediaFoundationExportService(MediaFoundationMp3Exporter exporter, string? localExportRoot = null)
    {
        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        this.localExportRoot = Path.GetFullPath(localExportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceBoothKiosk",
            "export-cache"));
    }

    public Task<ExportEncoderValidationResult> ValidateEncoderAsync(
        AudioFormat format,
        int mp3BitrateKbps,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (format != AudioFormat.CanonicalMono)
                {
                    return new ExportEncoderValidationResult(
                        false,
                        "MP3 export requires the supported 48 kHz, 16-bit mono recording format.",
                        []);
                }

                var supported = exporter.GetSupportedBitrates()
                    .Select(static bitrate => bitrate / 1000)
                    .Distinct()
                    .OrderBy(static bitrate => bitrate)
                    .ToArray();
                var ready = supported.Contains(mp3BitrateKbps);
                return new ExportEncoderValidationResult(
                    ready,
                    ready
                        ? $"MP3 encoder ready at {mp3BitrateKbps} kbps."
                        : $"The configured {mp3BitrateKbps} kbps MP3 profile is unavailable. Supported: {string.Join(", ", supported)} kbps.",
                    supported);
            },
            cancellationToken);

    public Task<DestinationValidationResult> ValidateDestinationAsync(
        DestinationPreset destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(() => ValidateDestination(destination, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyCollection<string>> GetExistingSessionFolderNamesAsync(
        DestinationPreset destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run<IReadOnlyCollection<string>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!destination.IsEnabled || !Directory.Exists(destination.RootPath))
                {
                    return [];
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in Directory.EnumerateDirectories(Path.GetFullPath(destination.RootPath)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    names.Add(TryGetReservedNameFromStagingDirectory(name, out var reservedName)
                        ? reservedName
                        : name);
                }

                foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(destination.RootPath)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryGetReservedNameFromMarker(Path.GetFileName(file), out var reservedName))
                    {
                        names.Add(reservedName);
                    }
                }

                return names.ToArray();
            },
            cancellationToken);
    }

    public Task<ExportResult?> FindCommittedExportAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> finalizedSegmentIds,
        IReadOnlyList<DestinationPreset> destinations,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(finalizedSegmentIds);
        ArgumentNullException.ThrowIfNull(destinations);
        var expectedSegments = finalizedSegmentIds.ToHashSet();
        return Task.Run(
            () => FindCommittedExport(sessionId, expectedSegments, destinations, cancellationToken),
            cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        Report(progress, ExportState.Validating, 0, null, "Checking the destination and MP3 encoder.");
        var validation = await ValidateDestinationAsync(request.Destination, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new IOException(validation.Message);
        }

        // Fail before creating a visible or staging folder when the required encoder profile is absent.
        var bitrate = checked(request.Mp3BitrateKbps * 1000);
        if (!exporter.GetSupportedBitrates().Contains(bitrate))
        {
            throw new PlatformNotSupportedException(
                $"Windows Media Foundation does not provide the configured {request.Mp3BitrateKbps} kbps MP3 profile.");
        }

        var root = Path.GetFullPath(request.Destination.RootPath);
        var finalFolder = CombineSingleChild(root, request.SessionFolderName, "session folder");
        if (Directory.Exists(finalFolder) || File.Exists(finalFolder))
        {
            throw new IOException($"The destination session folder already exists: {finalFolder}");
        }

        var stagingFolder = CombineSingleChild(
            root,
            $".{request.SessionFolderName}.{Guid.NewGuid():N}.partial",
            "staging folder");
        var outputNames = ValidateAndReserveOutputNames(request.Clips);
        var rawOutputNames = outputNames
            .Select(static name => Path.ChangeExtension(name, ".wav"))
            .ToArray();
        var reservationPath = CombineSingleChild(
            root,
            $".{request.SessionFolderName}.voicebooth-reservation",
            "reservation marker");
        var localEncodingFolder = Path.Combine(
            localExportRoot,
            $"VoiceBoothKiosk-Export-{Guid.NewGuid():N}");
        FileStream reservation;
        try
        {
            reservation = new FileStream(
                reservationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            reservation.Flush(flushToDisk: true);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "Another export has already reserved this session folder name. Choose a new session-folder suffix and retry.",
                exception);
        }

        try
        {
            // Recheck while holding the marker. This interoperates with Infrastructure's
            // OutputNameReservation marker convention and closes the check/create race.
            if (Directory.Exists(finalFolder) || File.Exists(finalFolder))
            {
                throw new IOException($"The destination session folder already exists: {finalFolder}");
            }

            Directory.CreateDirectory(localEncodingFolder);
            Directory.CreateDirectory(stagingFolder);
            var rawStagingFolder = CombineSingleChild(stagingFolder, RawFolderName, "raw audio folder");
            Directory.CreateDirectory(rawStagingFolder);
            var stagedMp3Paths = new List<string>(request.Clips.Count);
            var stagedRawPaths = new List<string>(request.Clips.Count);
            for (var index = 0; index < request.Clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = request.Clips[index];
                var fraction = (double)index / request.Clips.Count;
                Report(
                    progress,
                    ExportState.Encoding,
                    fraction,
                    clip.ClipId,
                    $"Encoding MP3 {index + 1} of {request.Clips.Count}.");

                var stagedPath = CombineSingleChild(stagingFolder, outputNames[index], "MP3 file");
                var localEncodedPath = CombineSingleChild(localEncodingFolder, outputNames[index], "local MP3 file");
                var result = await exporter.ExportAsync(
                    clip.Segments.Select(static segment => segment.WavePath).ToArray(),
                    localEncodedPath,
                    bitrate,
                    cancellationToken).ConfigureAwait(false);
                var tolerance = TimeSpan.FromSeconds(Math.Max(1.5, clip.Duration.TotalSeconds * 0.03));
                if ((result.SourceDuration - clip.Duration).Duration() > tolerance)
                {
                    throw new InvalidDataException(
                        $"Clip {clip.ClipId} metadata does not match the duration of its WAV segments.");
                }

                await Task.Run(
                    () => CopyCompletedFile(localEncodedPath, stagedPath, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                stagedMp3Paths.Add(stagedPath);
                Report(
                    progress,
                    ExportState.Encoding,
                    fraction + (0.5 / request.Clips.Count),
                    clip.ClipId,
                    $"Writing lossless WAV {index + 1} of {request.Clips.Count}.");
                var stagedRawPath = CombineSingleChild(rawStagingFolder, rawOutputNames[index], "raw WAV file");
                await Task.Run(
                    () => WriteAndVerifyRawWave(clip, stagedRawPath, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                stagedRawPaths.Add(stagedRawPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteCommitReceipt(stagingFolder, request, outputNames, rawOutputNames);
            Report(progress, ExportState.Committing, 0.98, null, "Finalizing the session folder.");
            TransientFileOperation.Execute(
                () => Directory.Move(stagingFolder, finalFolder),
                cancellationToken);

            var finalPaths = stagedMp3Paths
                .Select(path => Path.Combine(finalFolder, Path.GetFileName(path)))
                .ToArray();
            var finalRawPaths = stagedRawPaths
                .Select(path => Path.Combine(finalFolder, RawFolderName, Path.GetFileName(path)))
                .ToArray();
            Report(progress, ExportState.Completed, 1, null, "Session export completed.");
            return new ExportResult
            {
                SessionFolderPath = finalFolder,
                ExportedFilePaths = finalPaths,
                RawFilePaths = finalRawPaths,
            };
        }
        catch (OperationCanceledException)
        {
            Report(progress, ExportState.Canceled, 0, null, "Session export canceled.");
            TryDeleteStagingFolder(stagingFolder, root);
            throw;
        }
        catch (Exception exception)
        {
            Report(progress, ExportState.Failed, 0, null, exception.Message);
            TryDeleteStagingFolder(stagingFolder, root);
            throw;
        }
        finally
        {
            reservation.Dispose();
            TryDeleteReservation(reservationPath);
            TryDeleteLocalEncodingFolder(localEncodingFolder, localExportRoot);
        }
    }

    private static void CopyCompletedFile(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullSource = Path.GetFullPath(sourcePath);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (File.Exists(fullDestination))
        {
            throw new IOException($"The staged MP3 already exists: {fullDestination}");
        }

        var directory = Path.GetDirectoryName(fullDestination) ??
                        throw new ArgumentException("The staged MP3 path has no parent directory.", nameof(destinationPath));
        var transferPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.transferring");
        try
        {
            long sourceLength;
            using (var source = new FileStream(
                       fullSource,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       CopyBufferSize,
                       FileOptions.SequentialScan))
            {
                sourceLength = source.Length;
                using var destination = new FileStream(
                    transferPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.SequentialScan | FileOptions.WriteThrough);
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                }
                destination.Flush(flushToDisk: true);
            }

            if (new FileInfo(transferPath).Length != sourceLength)
            {
                throw new IOException("The staged MP3 length does not match its verified local source.");
            }

            TransientFileOperation.Execute(
                () => File.Move(transferPath, fullDestination, overwrite: false),
                cancellationToken);
        }
        finally
        {
            TryDeleteFile(transferPath);
        }
    }

    private static ExportResult? FindCommittedExport(
        Guid sessionId,
        IReadOnlySet<Guid> expectedSegments,
        IReadOnlyList<DestinationPreset> destinations,
        CancellationToken cancellationToken)
    {
        foreach (var destination in destinations.Where(destination => destination.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(destination.RootPath) || !Directory.Exists(destination.RootPath))
            {
                continue;
            }

            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(Path.GetFullPath(destination.RootPath)).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var receiptPath = Path.Combine(folder, CommitReceiptFileName);
                if (!File.Exists(receiptPath))
                {
                    continue;
                }

                try
                {
                    using var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var receipt = JsonSerializer.Deserialize<CommittedExportReceipt>(stream);
                    if (receipt is null || receipt.SchemaVersion is not (1 or 2) || receipt.SessionId != sessionId ||
                        receipt.FinalizedSegmentIds is null || receipt.FinalizedSegmentIds.Count == 0 ||
                        receipt.FileNames is null || receipt.FileNames.Count == 0 ||
                        receipt.SchemaVersion == 2 &&
                        (receipt.RawFileNames is null ||
                         receipt.RawFileNames.Count != receipt.FileNames.Count ||
                         !receipt.RawFileNames.SequenceEqual(
                             receipt.FileNames.Select(static name => Path.ChangeExtension(name, ".wav")),
                             StringComparer.OrdinalIgnoreCase)) ||
                        !string.Equals(receipt.SessionFolderName, Path.GetFileName(folder), StringComparison.Ordinal) ||
                        !expectedSegments.SetEquals(receipt.FinalizedSegmentIds))
                    {
                        continue;
                    }

                    var files = new List<string>(receipt.FileNames.Count);
                    foreach (var fileName in receipt.FileNames)
                    {
                        if (string.IsNullOrWhiteSpace(fileName) ||
                            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                            !string.Equals(Path.GetExtension(fileName), ".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("The export receipt contains an invalid MP3 filename.");
                        }

                        var path = Path.Combine(folder, fileName);
                        using var reader = new Mp3FileReader(path);
                        if (reader.Length <= 0 || reader.TotalTime <= TimeSpan.Zero)
                        {
                            throw new InvalidDataException("A committed MP3 is empty or unreadable.");
                        }
                        files.Add(path);
                    }

                    var rawFiles = new List<string>();
                    if (receipt.SchemaVersion == 2)
                    {
                        foreach (var fileName in receipt.RawFileNames)
                        {
                            if (string.IsNullOrWhiteSpace(fileName) ||
                                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                                !string.Equals(Path.GetExtension(fileName), ".wav", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("The export receipt contains an invalid raw WAV filename.");
                            }

                            var path = Path.Combine(folder, RawFolderName, fileName);
                            VerifyRawWave(path);
                            rawFiles.Add(path);
                        }
                    }

                    return new ExportResult
                    {
                        SessionFolderPath = folder,
                        ExportedFilePaths = files,
                        RawFilePaths = rawFiles,
                    };
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    // Invalid receipts are ignored; they never authorize completion or deletion.
                }
            }
        }

        return null;
    }

    private static void WriteCommitReceipt(
        string stagingFolder,
        ExportRequest request,
        IReadOnlyList<string> outputNames,
        IReadOnlyList<string> rawOutputNames)
    {
        var receipt = new CommittedExportReceipt
        {
            SchemaVersion = 2,
            SessionId = request.SessionId,
            SessionFolderName = request.SessionFolderName,
            CommittedAtUtc = DateTimeOffset.UtcNow,
            FinalizedSegmentIds = request.Clips
                .SelectMany(static clip => clip.Segments)
                .Select(static segment => segment.SegmentId)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray(),
            FileNames = outputNames.ToArray(),
            RawFileNames = rawOutputNames.ToArray(),
        };
        var path = Path.Combine(stagingFolder, CommitReceiptFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The dot-prefixed receipt remains non-user content even when Hidden cannot be set.
        }
    }

    private static void WriteAndVerifyRawWave(
        ClipExportSource clip,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new LogicalClipWaveStream(
            clip.Segments.Select(static segment => segment.WavePath));
        var expectedBytes = source.Length;
        if (expectedBytes <= 0)
        {
            throw new InvalidDataException($"Clip {clip.ClipId} contains no PCM audio for its raw WAV export.");
        }

        using (var writer = new WaveFileWriter(outputPath, source.WaveFormat))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
            }
            writer.Flush();
        }

        VerifyRawWave(outputPath, expectedBytes);
    }

    private static void VerifyRawWave(string path, long? expectedBytes = null)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.SampleRate != WasapiAudioCaptureService.CanonicalSampleRate ||
            reader.WaveFormat.BitsPerSample != WasapiAudioCaptureService.CanonicalBitsPerSample ||
            reader.WaveFormat.Channels != WasapiAudioCaptureService.CanonicalChannels ||
            reader.Length <= 0 ||
            reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException("A raw WAV export is empty, unreadable, or not in the lossless recording format.");
        }

        if (expectedBytes is not null && reader.Length != expectedBytes.Value)
        {
            throw new InvalidDataException(
                $"The raw WAV export contains {reader.Length} PCM bytes; {expectedBytes.Value} were expected.");
        }
    }

    private static DestinationValidationResult ValidateDestination(
        DestinationPreset destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.IsEnabled)
        {
            return Invalid("This export destination is disabled.");
        }

        if (string.IsNullOrWhiteSpace(destination.RootPath) || !Path.IsPathFullyQualified(destination.RootPath))
        {
            return Invalid("The export destination is not an absolute path.");
        }

        var root = Path.GetFullPath(destination.RootPath);
        if (!Directory.Exists(root))
        {
            return Invalid("The export destination is unavailable.");
        }

        long? availableBytes = null;
        try
        {
            var pathRoot = Path.GetPathRoot(root);
            if (!string.IsNullOrWhiteSpace(pathRoot))
            {
                availableBytes = new DriveInfo(pathRoot).AvailableFreeSpace;
            }
        }
        catch
        {
            // Some UNC providers do not report capacity. The write probe below is authoritative.
        }

        if (availableBytes is not null && availableBytes < destination.MinimumFreeBytes)
        {
            return new DestinationValidationResult
            {
                IsValid = false,
                Message = "The export destination does not have enough free space.",
                AvailableBytes = availableBytes,
            };
        }

        var probe = Path.Combine(root, $".voicebooth-write-test-{Guid.NewGuid():N}.tmp");
        var probeCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            probeCreated = true;
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DestinationValidationResult
            {
                IsValid = false,
                Message = "The export destination is not writable.",
                AvailableBytes = availableBytes,
            };
        }
        finally
        {
            try
            {
                if (probeCreated)
                {
                    File.Delete(probe);
                }
            }
            catch
            {
            }
        }

        return new DestinationValidationResult
        {
            IsValid = true,
            Message = "The export destination is ready.",
            AvailableBytes = availableBytes,
        };
    }

    private static DestinationValidationResult Invalid(string message) => new()
    {
        IsValid = false,
        Message = message,
    };

    private static void ValidateRequest(ExportRequest request)
    {
        if (request.Format != AudioFormat.CanonicalMono)
        {
            throw new NotSupportedException("MP3 export requires 48 kHz, 16-bit, mono PCM source segments.");
        }

        if (request.Mp3BitrateKbps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MP3 bitrate must be positive.");
        }

        if (request.Clips.Count == 0 || request.Clips.Any(static clip => clip.Segments.Count == 0))
        {
            throw new ArgumentException("Export requires at least one non-empty clip.", nameof(request));
        }
    }

    private static string[] ValidateAndReserveOutputNames(IReadOnlyList<ClipExportSource> clips)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new string[clips.Count];
        for (var index = 0; index < clips.Count; index++)
        {
            var name = clips[index].OutputFileName;
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(name), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                !reserved.Add(name))
            {
                throw new ArgumentException($"The reserved MP3 output name is invalid or duplicated: {name}", nameof(clips));
            }

            result[index] = name;
        }

        return result;
    }

    private static string CombineSingleChild(string parent, string childName, string description)
    {
        if (string.IsNullOrWhiteSpace(childName) ||
            Path.IsPathRooted(childName) ||
            !string.Equals(Path.GetFileName(childName), childName, StringComparison.Ordinal) ||
            childName is "." or "..")
        {
            throw new ArgumentException($"The {description} name is invalid.", nameof(childName));
        }

        var combined = Path.GetFullPath(Path.Combine(parent, childName));
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), combined);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"The {description} escapes its approved parent folder.", nameof(childName));
        }

        return combined;
    }

    private static bool TryGetReservedNameFromMarker(string fileName, out string reservedName)
    {
        const string suffix = ".voicebooth-reservation";
        reservedName = string.Empty;
        if (fileName.Length <= suffix.Length + 1 || fileName[0] != '.'
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        reservedName = fileName[1..^suffix.Length];
        return IsSafeReservedName(reservedName);
    }

    private static bool TryGetReservedNameFromStagingDirectory(string directoryName, out string reservedName)
    {
        const string suffix = ".partial";
        reservedName = string.Empty;
        if (directoryName.Length <= suffix.Length + 1 || directoryName[0] != '.'
            || !directoryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var withoutWrapper = directoryName[1..^suffix.Length];
        var separator = withoutWrapper.LastIndexOf('.');
        if (separator <= 0 || separator == withoutWrapper.Length - 1
            || !Guid.TryParseExact(withoutWrapper[(separator + 1)..], "N", out _))
        {
            return false;
        }

        reservedName = withoutWrapper[..separator];
        return IsSafeReservedName(reservedName);
    }

    private static bool IsSafeReservedName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
        && value is not ("." or "..");

    private static void Report(
        IProgress<ExportProgress>? progress,
        ExportState state,
        double fraction,
        Guid? clipId,
        string message) =>
        SafeReport(progress, new ExportProgress
        {
            State = state,
            FractionComplete = Math.Clamp(fraction, 0, 1),
            ClipId = clipId,
            Message = message,
        });

    private static void SafeReport(IProgress<ExportProgress>? progress, ExportProgress update)
    {
        try
        {
            progress?.Report(update);
        }
        catch
        {
            // Progress observers are advisory and cannot be allowed to invalidate a committed export.
        }
    }

    private static void TryDeleteStagingFolder(string stagingFolder, string root)
    {
        try
        {
            var fullStaging = Path.GetFullPath(stagingFolder);
            var relative = Path.GetRelativePath(Path.GetFullPath(root), fullStaging);
            if (relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative) &&
                Path.GetFileName(fullStaging).EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullStaging))
            {
                TransientFileOperation.Execute(() => Directory.Delete(fullStaging, recursive: true));
            }
        }
        catch
        {
            // A leftover folder remains explicitly marked partial and can be cleaned by diagnostics.
        }
    }

    private static void TryDeleteReservation(string reservationPath)
    {
        try
        {
            TransientFileOperation.Execute(() => File.Delete(reservationPath));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            TransientFileOperation.Execute(() => File.Delete(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteLocalEncodingFolder(string localEncodingFolder, string approvedRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(approvedRoot);
            var fullFolder = Path.GetFullPath(localEncodingFolder);
            var relative = Path.GetRelativePath(fullRoot, fullFolder);
            if (relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative) &&
                Path.GetFileName(fullFolder).StartsWith("VoiceBoothKiosk-Export-", StringComparison.Ordinal) &&
                Directory.Exists(fullFolder))
            {
                TransientFileOperation.Execute(() => Directory.Delete(fullFolder, recursive: true));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The uniquely named local cache remains recoverable and can be removed during maintenance.
        }
    }

    private sealed record CommittedExportReceipt
    {
        public int SchemaVersion { get; init; }
        public Guid SessionId { get; init; }
        public string SessionFolderName { get; init; } = string.Empty;
        public DateTimeOffset CommittedAtUtc { get; init; }
        public IReadOnlyList<Guid> FinalizedSegmentIds { get; init; } = [];
        public IReadOnlyList<string> FileNames { get; init; } = [];
        public IReadOnlyList<string> RawFileNames { get; init; } = [];
    }
}
