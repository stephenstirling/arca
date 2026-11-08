using System.Globalization;
using System.Text.RegularExpressions;

namespace Arca.Core;

public enum VolumeKind
{
    Single,

    /// <summary>One archive cut into pieces byte by byte (name.7z.001, name.7z.002 …, as 7-Zip's "split" makes): read as one file.</summary>
    Split,

    /// <summary>RAR volumes: name.part1.rar, name.part2.rar … or the older name.rar, name.r00, name.r01 ….</summary>
    RarVolumes,

    /// <summary>Spanned ZIP: name.z01, name.z02 … with name.zip as the last part.</summary>
    ZipSpanned,
}

/// <summary>
/// Every part of a multi-part archive, found from any one of them.
/// <see cref="OpenPath"/> is the part the reader starts from; <see cref="Parts"/> is every part in order.
/// </summary>
public sealed record VolumeSet(VolumeKind Kind, string OpenPath, IReadOnlyList<string> Parts, string BaseName)
{
    public long TotalLength => Parts.Sum(p => new FileInfo(p).Length);
}

/// <summary>Thrown when a part of a multi-part archive isn't next to the others.</summary>
public sealed class MissingPartException(string partName)
    : IOException($"Part of this archive is missing: {partName} needs to be in the same folder as the other parts.")
{
    public string PartName { get; } = partName;
}

public static class Volumes
{
    private static readonly Regex SplitName = new(@"^(?<base>.+)\.(?<n>\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RarPartName = new(@"^(?<base>.+)\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RarOldName = new(@"^(?<base>.+)\.(?<ext>rar|r\d\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ZipSpanName = new(@"^(?<base>.+)\.(?<ext>zip|z\d\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The whole set that <paramref name="path"/> belongs to; a set of one for an ordinary archive.</summary>
    /// <exception cref="MissingPartException">A part before the last one found is missing.</exception>
    public static VolumeSet Find(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);

        var m = RarPartName.Match(name);
        if (m.Success)
        {
            var width = m.Groups["n"].Length;
            string Part(int i) => Path.Combine(dir, $"{m.Groups["base"].Value}.part{i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}.rar");
            var parts = Consecutive(Part, 1);
            return Checked(new VolumeSet(VolumeKind.RarVolumes, Part(1), parts, m.Groups["base"].Value + ".rar"), full, Part(parts.Count + 1));
        }

        m = SplitName.Match(name);
        if (m.Success)
        {
            var width = m.Groups["n"].Length;
            string Part(int i) => Path.Combine(dir, $"{m.Groups["base"].Value}.{i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}");
            var parts = Consecutive(Part, 1);
            // "report.2024" is a name ending in digits, not a piece of a split archive.
            if (parts.Count == 0 && !File.Exists(Part(2))) return Single(full);
            return Checked(new VolumeSet(VolumeKind.Split, Part(1), parts, m.Groups["base"].Value), full, Part(parts.Count + 1));
        }

        m = RarOldName.Match(name);
        if (m.Success)
        {
            var stem = Path.Combine(dir, m.Groups["base"].Value);
            var first = stem + ".rar";
            if (m.Groups["ext"].Value.Equals("rar", StringComparison.OrdinalIgnoreCase) && !File.Exists(stem + ".r00"))
                return Single(full);
            // .rar is part 1, then .r00 is part 2, .r01 part 3 …
            string Part(int i) => i == 1 ? first : $"{stem}.r{(i - 2).ToString("00", CultureInfo.InvariantCulture)}";
            var parts = Consecutive(Part, 1, max: 101);
            return Checked(new VolumeSet(VolumeKind.RarVolumes, first, parts, m.Groups["base"].Value + ".rar"), full, Part(parts.Count + 1));
        }

        m = ZipSpanName.Match(name);
        if (m.Success)
        {
            var stem = Path.Combine(dir, m.Groups["base"].Value);
            var last = stem + ".zip";
            if (m.Groups["ext"].Value.Equals("zip", StringComparison.OrdinalIgnoreCase) && !File.Exists(stem + ".z01"))
                return Single(full);
            string Part(int i) => $"{stem}.z{i.ToString("00", CultureInfo.InvariantCulture)}";
            var parts = Consecutive(Part, 1, max: 99);
            var missing = Part(parts.Count + 1);
            if (!File.Exists(last)) throw new MissingPartException(Path.GetFileName(last));
            parts.Add(last);
            return Checked(new VolumeSet(VolumeKind.ZipSpanned, last, parts, m.Groups["base"].Value + ".zip"), full, missing);
        }

        return Single(full);
    }

    private static VolumeSet Single(string full) => new(VolumeKind.Single, full, [full], Path.GetFileName(full));

    /// <summary>part(first), part(first + 1) … for as long as the files exist.</summary>
    private static List<string> Consecutive(Func<int, string> part, int first, int max = 9_999)
    {
        var parts = new List<string>();
        for (int i = first; i < first + max && File.Exists(part(i)); i++) parts.Add(part(i));
        return parts;
    }

    /// <summary>The opened file must be in the set; when it isn't, a part between the last one found and it is missing.</summary>
    private static VolumeSet Checked(VolumeSet set, string opened, string nextAfterLastFound)
    {
        if (set.Parts.Count == 0 || !File.Exists(set.OpenPath)) throw new MissingPartException(Path.GetFileName(set.OpenPath));
        if (!set.Parts.Contains(opened, StringComparer.OrdinalIgnoreCase))
            throw new MissingPartException(Path.GetFileName(nextAfterLastFound));
        return set;
    }
}
