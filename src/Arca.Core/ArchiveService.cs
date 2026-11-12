using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Arca.Core;

/// <summary>
/// Lists, extracts and tests archives. Nothing the archive says is trusted: names go through <see cref="EntryPathGuard"/>,
/// sizes are counted from the bytes actually read (not the sizes in the headers), and the limits in
/// <see cref="SafetyLimits"/> stop a run that is turning into a zip bomb.
/// </summary>
public static class ArchiveService
{
    /// <summary>Reads the table of contents without extracting anything, and collects safety warnings.</summary>
    /// <exception cref="NotSupportedException">The file isn't an archive Arca can read (the message says why).</exception>
    public static ArchiveListing List(string archivePath, string? password = null, SafetyLimits? limits = null)
    {
        limits ??= SafetyLimits.Default;
        var (set, format) = RequireReadable(archivePath);
        var archiveLength = set.TotalLength;

        var entries = new List<ArchiveEntryInfo>();
        var warnings = new List<ArchiveWarning>();
        bool tooMany = false;

        // Entries are only looked at here, never opened; for a .tar.gz the reader steps over each file's data.
        var isSolid = WalkAskingForPassword(set, format, password, (e, open) =>
        {
            if (entries.Count >= limits.MaxEntries) { tooMany = true; return false; }
            var name = EntryName(e, set);
            var unsafeReason = EntryPathGuard.CheckName(name, out _);
            entries.Add(new ArchiveEntryInfo(name, e.IsDirectory, e.Size, e.CompressedSize, e.IsEncrypted, e.LastModifiedTime, unsafeReason, e.Crc));
            return true;
        });

        if (tooMany)
            warnings.Add(new ArchiveWarning("too-many-entries", $"This archive holds more than {limits.MaxEntries:N0} entries. Only the first {limits.MaxEntries:N0} are shown, and extracting it will stop at that limit."));

        var unsafeEntries = entries.Where(e => e.UnsafeReason is not null).ToList();
        if (unsafeEntries.Count > 0)
            warnings.Add(new ArchiveWarning("unsafe-path",
                $"{unsafeEntries.Count:N0} {(unsafeEntries.Count == 1 ? "entry has" : "entries have")} an unsafe name and will not be extracted. For example \"{unsafeEntries[0].Path}\": {unsafeEntries[0].UnsafeReason}."));

        int encrypted = entries.Count(e => e.IsEncrypted);
        if (encrypted > 0)
            warnings.Add(new ArchiveWarning("encrypted", $"{encrypted:N0} {(encrypted == 1 ? "entry is" : "entries are")} password-protected."));

        long declaredTotal = entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.Size));
        if (declaredTotal > limits.MaxTotalBytes)
            warnings.Add(new ArchiveWarning("too-large", $"This archive says it unpacks to {FormatBytes(declaredTotal)}, more than the {FormatBytes(limits.MaxTotalBytes)} limit."));
        else if (declaredTotal >= limits.MinBytesForRatioCheck && archiveLength > 0 && (double)declaredTotal / archiveLength > limits.MaxRatio)
            warnings.Add(new ArchiveWarning("high-ratio", $"This {FormatBytes(archiveLength)} archive says it unpacks to {FormatBytes(declaredTotal)}. That is a typical zip bomb pattern."));

        return new ArchiveListing
        {
            ArchivePath = archivePath,
            Format = format,
            Entries = entries,
            Warnings = warnings,
            IsSolid = isSolid,
            ArchiveSize = archiveLength,
            Parts = set.Parts,
        };
    }

    /// <summary>
    /// Extracts the archive (or the entries in <see cref="ExtractOptions.Only"/>) into <paramref name="destinationFolder"/>,
    /// or a new folder inside it (see <see cref="ExtractOptions.SmartFolder"/>).
    /// Unsafe or password-protected entries are skipped and reported in the result, not thrown.
    /// A safety limit stops the run and sets <see cref="ExtractResult.AbortReason"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The file isn't an archive Arca can read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancellationToken"/>.</exception>
    public static ExtractResult Extract(
        string archivePath,
        string destinationFolder,
        ExtractOptions? options = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractOptions();
        var limits = options.Limits;
        var (set, format) = RequireReadable(archivePath);

        var archiveLength = set.TotalLength;
        var mark = options.PropagateMarkOfTheWeb ? MarkOfTheWeb.Read(archivePath) : null;
        var only = options.Only?.Select(ArchivePaths.Normalize).ToList();
        var strip = ArchivePaths.Normalize(options.StripPrefix);

        var root = Path.GetFullPath(destinationFolder);
        if (only is null && options.SmartFolder && NeedsWrapperFolder(set, format, options.Password))
            root = UniquePath(Path.Combine(root, WrapperFolderName(set)));
        Directory.CreateDirectory(root);

        var result = new ExtractResult { Destination = root };
        var buffer = new byte[81920];
        var meter = new Meter(limits, archiveLength);
        int seen = 0;

        // Returns false to stop the whole run.
        bool Handle(IEntry entry, Func<Stream> open)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++seen > limits.MaxEntries)
            {
                result.AbortReason = $"Stopped: the archive holds more than {limits.MaxEntries:N0} entries.";
                return false;
            }

            var name = EntryName(entry, set);
            var normalized = ArchivePaths.Normalize(name);
            if (only is not null && !only.Any(p => ArchivePaths.IsUnder(normalized, p))) return true;

            // Writing a selection from inside a folder: drop that folder's path from the front.
            var target = name;
            if (strip.Length > 0 && ArchivePaths.IsUnder(normalized, strip))
            {
                if (normalized.Length == strip.Length) return true; // the folder itself
                target = normalized[(strip.Length + 1)..];
            }

            if (!EntryPathGuard.TryResolve(root, target, out var fullPath, out var problem))
            {
                result.Skipped.Add(new SkippedEntry(name, problem));
                return true;
            }

            if (entry.IsDirectory)
            {
                try { Directory.CreateDirectory(fullPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Skipped.Add(new SkippedEntry(name, ex.Message));
                }
                return true;
            }

            if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
            {
                result.Skipped.Add(new SkippedEntry(name, "it is password-protected and no password was given"));
                return true;
            }

            if (File.Exists(fullPath))
            {
                if (options.Overwrite == OverwriteMode.Skip)
                {
                    result.Skipped.Add(new SkippedEntry(name, "a file with that name already exists"));
                    return true;
                }
                if (options.Overwrite == OverwriteMode.Rename) fullPath = UniquePath(fullPath);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                string? abort = null;
                using (var input = open())
                using (var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        abort = meter.Add(n);
                        if (abort is not null) break;
                        output.Write(buffer, 0, n);
                    }
                }
                result.BytesWritten = meter.Total;

                if (abort is not null)
                {
                    result.AbortReason = abort;
                    TryDelete(fullPath); // don't leave the half-written file behind
                    return false;
                }

                if (entry.LastModifiedTime is DateTime modified)
                {
                    try { File.SetLastWriteTime(fullPath, modified); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
                }
                if (mark is not null) MarkOfTheWeb.Apply(fullPath, mark);

                result.FilesExtracted++;
                progress?.Report(new ExtractProgress(name, result.FilesExtracted, result.BytesWritten));
            }
            catch (OperationCanceledException)
            {
                TryDelete(fullPath);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A damaged or wrong-password entry: skip it, say why, keep going.
                TryDelete(fullPath);
                result.Skipped.Add(new SkippedEntry(name, ex.Message));
            }
            return true;
        }

        WalkAskingForPassword(set, format, options.Password, Handle, passwordIsSuspect: false);
        return result;
    }

    /// <summary>
    /// Reads every file through without writing anything, and compares each one's CRC-32 with the one stored in the
    /// archive (when the format stores one). The same size and zip-bomb limits apply as for extracting.
    /// </summary>
    /// <exception cref="NotSupportedException">The file isn't an archive Arca can read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancellationToken"/>.</exception>
    public static TestResult Test(
        string archivePath,
        string? password = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default,
        SafetyLimits? limits = null)
    {
        limits ??= SafetyLimits.Default;
        var (set, format) = RequireReadable(archivePath);

        var meter = new Meter(limits, set.TotalLength);
        var result = new TestResult();
        var buffer = new byte[81920];
        int seen = 0;

        bool Handle(IEntry entry, Func<Stream> open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++seen > limits.MaxEntries)
            {
                result.AbortReason = $"Stopped: the archive holds more than {limits.MaxEntries:N0} entries.";
                return false;
            }
            if (entry.IsDirectory) return true;

            var name = EntryName(entry, set);
            if (entry.IsEncrypted && string.IsNullOrEmpty(password))
            {
                result.Skipped.Add(new SkippedEntry(name, "it is password-protected and no password was given"));
                return true;
            }

            try
            {
                uint crc = Crc32.Seed;
                using (var input = open())
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var abort = meter.Add(n);
                        if (abort is not null)
                        {
                            result.BytesRead = meter.Total;
                            result.AbortReason = abort;
                            return false;
                        }
                        crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                    }
                }
                result.BytesRead = meter.Total;
                crc = Crc32.Finish(crc);

                if (entry.Crc != 0 && (uint)entry.Crc != crc)
                    result.Failed.Add(new SkippedEntry(name, $"its checksum doesn't match (stored {(uint)entry.Crc:X8}, read {crc:X8}): the file is damaged"));
                else
                    result.FilesOk++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Failed.Add(new SkippedEntry(name, ex.Message));
            }

            progress?.Report(new ExtractProgress(name, result.FilesOk + result.Failed.Count, result.BytesRead));
            return true;
        }

        WalkAskingForPassword(set, format, password, Handle, passwordIsSuspect: false);
        return result;
    }

    /// <summary>
    /// Calls <paramref name="handle"/> for each entry in archive order until it returns false. Returns whether the
    /// archive is solid. GZip, BZip2 and XZ are read as a stream, which is also what finds the TAR inside a .tar.gz
    /// (opened as an archive, it would look like one big .tar file).
    /// </summary>
    private static bool Walk(VolumeSet set, ArchiveFormat format, ReaderOptions readerOptions, Func<IEntry, Func<Stream>, bool> handle)
    {
        if (format is ArchiveFormat.Gzip or ArchiveFormat.Bzip2 or ArchiveFormat.Xz)
        {
            using var stream = OpenWhole(set);
            using var streamReader = ReaderFactory.Open(stream, readerOptions);
            while (streamReader.MoveToNextEntry())
            {
                if (!handle(streamReader.Entry, () => streamReader.OpenEntryStream())) break;
            }
            return false;
        }

        if (set.Kind == VolumeKind.Split)
        {
            using var stream = OpenWhole(set);
            using var joined = ArchiveFactory.Open(stream, readerOptions);
            Walk(joined, handle);
            return joined.IsSolid;
        }

        // RAR volumes and spanned ZIPs: SharpCompress finds the other parts itself from the one it is given.
        using var archive = ArchiveFactory.Open(set.OpenPath, readerOptions);
        Walk(archive, handle);
        return archive.IsSolid;
    }

    /// <summary>The archive as one stream: the file itself, or all the pieces of a split archive end to end.</summary>
    private static Stream OpenWhole(VolumeSet set) => set.Kind == VolumeKind.Split
        ? new ConcatenatedStream(set.Parts)
        : new FileStream(set.OpenPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// <see cref="Walk(VolumeSet, ArchiveFormat, ReaderOptions, Func{IEntry, Func{Stream}, bool})"/> for an archive whose
    /// file names may themselves be encrypted (7z made with "encrypt file names", RAR with -hp): nothing can be listed
    /// without the password, so that becomes a <see cref="PasswordRequiredException"/> the caller can ask about.
    /// </summary>
    /// <param name="passwordIsSuspect">
    /// Treat any failure with a password given as a wrong password. Only right while listing, where a wrong key garbles
    /// the names; while extracting, a full disk would otherwise be reported as a wrong password.
    /// </param>
    private static bool WalkAskingForPassword(
        VolumeSet set, ArchiveFormat format, string? password, Func<IEntry, Func<Stream>, bool> handle, bool passwordIsSuspect = true)
    {
        try
        {
            return Walk(set, format, new ReaderOptions { Password = password }, handle);
        }
        catch (CryptographicException) when (string.IsNullOrEmpty(password))
        {
            throw new PasswordRequiredException(wrongPassword: false);
        }
        catch (Exception ex) when (passwordIsSuspect && !string.IsNullOrEmpty(password) && ex is not (OutOfMemoryException or OperationCanceledException or MissingPartException or FileNotFoundException or UnauthorizedAccessException))
        {
            // Names decrypted with the wrong key come out as nonsense headers, which fail in assorted ways
            // (some of SharpCompress's exception types aren't even public). A password is only ever passed here
            // after the archive asked for one, so the password is the likely culprit.
            throw new PasswordRequiredException(wrongPassword: true, ex);
        }
    }

    private static void Walk(IArchive archive, Func<IEntry, Func<Stream>, bool> handle)
    {
        if (archive.IsSolid || archive.Type == ArchiveType.Rar)
        {
            // Solid archives must be read front to back; jumping to single entries would re-unpack everything before them.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (!handle(reader.Entry, () => reader.OpenEntryStream())) break;
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                var current = entry;
                if (!handle(current, () => current.OpenEntryStream())) break;
            }
        }
    }

    /// <summary>Counts bytes coming out and says when a size or ratio limit is crossed.</summary>
    private sealed class Meter
    {
        private readonly SafetyLimits _limits;
        private readonly long _archiveLength;

        public Meter(SafetyLimits limits, long archiveLength)
        {
            _limits = limits;
            _archiveLength = archiveLength;
        }

        public long Total { get; private set; }

        /// <summary>Adds <paramref name="n"/> bytes; returns why to stop, or null to carry on (the bytes are counted only when it returns null).</summary>
        public string? Add(int n)
        {
            long next = Total + n;
            if (next > _limits.MaxTotalBytes)
                return $"Stopped: the archive unpacks to more than {FormatBytes(_limits.MaxTotalBytes)}.";
            if (next >= _limits.MinBytesForRatioCheck && _archiveLength > 0 && (double)next / _archiveLength > _limits.MaxRatio)
                return $"Stopped: the output is more than {_limits.MaxRatio:N0} times the size of the archive, which looks like a zip bomb.";
            Total = next;
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is an archive (or a part of one) that Arca can open.</summary>
    public static bool CanOpen(string path)
    {
        try
        {
            RequireReadable(path);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (VolumeSet Set, ArchiveFormat Format) RequireReadable(string archivePath)
    {
        var set = Volumes.Find(archivePath);
        var format = Detect(set);
        if (format == ArchiveFormat.Unknown)
            throw new NotSupportedException("Arca doesn't recognise this file as an archive.");
        if (!ArchiveFormats.CanRead(format))
            throw new NotSupportedException($"{ArchiveFormats.DisplayName(format)} archives aren't supported yet.");
        return (set, format);
    }

    private static ArchiveFormat Detect(VolumeSet set)
    {
        switch (set.Kind)
        {
            case VolumeKind.ZipSpanned:
                // The .zip of a spanned set is its last part and usually starts mid-file, without the ZIP signature.
                return ArchiveFormat.Zip;
            case VolumeKind.Split:
                using (var stream = new ConcatenatedStream(set.Parts))
                    return FormatDetector.Detect(stream, set.BaseName);
            default:
                return FormatDetector.DetectFile(set.OpenPath);
        }
    }

    /// <summary>Single-file formats like .gz can come without a stored name; use the archive's own name minus its extension.</summary>
    private static string EntryName(IEntry entry, VolumeSet set)
    {
        var key = entry.Key;
        return string.IsNullOrEmpty(key) ? Path.GetFileNameWithoutExtension(set.BaseName) : key;
    }

    private static bool NeedsWrapperFolder(VolumeSet set, ArchiveFormat format, string? password)
    {
        var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkAskingForPassword(set, format, password, (entry, _) =>
        {
            var key = entry.Key;
            if (string.IsNullOrEmpty(key)) return true;
            var first = key.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is null || first == ".") return true;
            topLevel.Add(first);
            return topLevel.Count <= 1;   // two different top-level names is enough to know
        }, passwordIsSuspect: false);
        return topLevel.Count > 1;
    }

    /// <summary>
    /// A new folder beside the archive, named after it ("photos.zip" → "photos", "photos (1)" if that exists), for
    /// "Extract to a folder". The folder is not created.
    /// </summary>
    public static string OwnFolderFor(string archivePath)
    {
        var set = Volumes.Find(archivePath);
        return UniquePath(Path.Combine(Path.GetDirectoryName(set.OpenPath)!, WrapperFolderName(set)));
    }

    private static string WrapperFolderName(VolumeSet set)
    {
        var name = Path.GetFileNameWithoutExtension(set.BaseName);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return name.Length == 0 ? "Extracted" : name;
    }

    /// <summary>"name.txt" becomes "name (1).txt", "name (2).txt", ... until nothing has that name.</summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Couldn't find a free file name.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
