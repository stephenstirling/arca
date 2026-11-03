using Arca.Core;

namespace Arca;

/// <summary>One line in the archive list.</summary>
public sealed class EntryRow
{
    public EntryRow(ArchiveEntryInfo entry)
    {
        Name = entry.Path;
        SizeText = entry.IsDirectory ? "" : ArchiveService.FormatBytes(entry.Size);
        Note = entry.UnsafeReason is not null ? "unsafe name, will be skipped"
             : entry.IsEncrypted ? "password-protected"
             : "";
    }

    public string Name { get; }
    public string SizeText { get; }
    public string Note { get; }
}
