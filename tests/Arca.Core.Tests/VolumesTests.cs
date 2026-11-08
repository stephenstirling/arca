using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class VolumesTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-volumes-" + Guid.NewGuid().ToString("N"));

    public VolumesTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private IEnumerable<string> Names(VolumeSet set) => set.Parts.Select(Path.GetFileName)!;

    [Fact]
    public void An_ordinary_archive_is_a_set_of_one()
    {
        var path = Touch("photos.zip");
        var set = Volumes.Find(path);
        Assert.Equal(VolumeKind.Single, set.Kind);
        Assert.Equal(new[] { "photos.zip" }, Names(set));
        Assert.Equal("photos.zip", set.BaseName);
    }

    [Fact]
    public void Split_pieces_are_found_from_any_of_them()
    {
        Touch("big.7z.001"); Touch("big.7z.002"); var third = Touch("big.7z.003");
        var set = Volumes.Find(third);
        Assert.Equal(VolumeKind.Split, set.Kind);
        Assert.Equal(new[] { "big.7z.001", "big.7z.002", "big.7z.003" }, Names(set));
        Assert.Equal("big.7z.001", Path.GetFileName(set.OpenPath));
        Assert.Equal("big.7z", set.BaseName);
    }

    [Fact]
    public void A_name_ending_in_digits_is_not_a_split_archive()
    {
        var path = Touch("report.2024");
        Assert.Equal(VolumeKind.Single, Volumes.Find(path).Kind);
    }

    [Fact]
    public void New_style_rar_volumes_start_at_part_one_with_the_same_padding()
    {
        Touch("film.part01.rar"); Touch("film.part02.rar"); var second = Path.Combine(_work, "film.part02.rar");
        var set = Volumes.Find(second);
        Assert.Equal(VolumeKind.RarVolumes, set.Kind);
        Assert.Equal(new[] { "film.part01.rar", "film.part02.rar" }, Names(set));
        Assert.Equal("film.part01.rar", Path.GetFileName(set.OpenPath));
        Assert.Equal("film.rar", set.BaseName);
    }

    [Fact]
    public void Old_style_rar_volumes_start_at_the_rar()
    {
        Touch("old.rar"); Touch("old.r00"); var r01 = Touch("old.r01");
        var set = Volumes.Find(r01);
        Assert.Equal(VolumeKind.RarVolumes, set.Kind);
        Assert.Equal(new[] { "old.rar", "old.r00", "old.r01" }, Names(set));
        Assert.Equal("old.rar", Path.GetFileName(set.OpenPath));
    }

    [Fact]
    public void A_rar_without_r00_is_a_single_archive()
    {
        Assert.Equal(VolumeKind.Single, Volumes.Find(Touch("one.rar")).Kind);
    }

    [Fact]
    public void Spanned_zip_ends_with_the_zip_which_is_where_reading_starts()
    {
        var z01 = Touch("disk.z01"); Touch("disk.z02"); Touch("disk.zip");
        var set = Volumes.Find(z01);
        Assert.Equal(VolumeKind.ZipSpanned, set.Kind);
        Assert.Equal(new[] { "disk.z01", "disk.z02", "disk.zip" }, Names(set));
        Assert.Equal("disk.zip", Path.GetFileName(set.OpenPath));
    }

    [Fact]
    public void A_missing_middle_part_is_named()
    {
        Touch("film.part1.rar"); var third = Touch("film.part3.rar");
        var ex = Assert.Throws<MissingPartException>(() => Volumes.Find(third));
        Assert.Equal("film.part2.rar", ex.PartName);
    }

    [Fact]
    public void A_missing_first_piece_is_named()
    {
        Touch("big.7z.002"); var third = Touch("big.7z.003");
        var ex = Assert.Throws<MissingPartException>(() => Volumes.Find(third));
        Assert.Equal("big.7z.001", ex.PartName);
    }

    [Fact]
    public void A_spanned_zip_without_its_zip_is_reported()
    {
        var z01 = Touch("disk.z01");
        var ex = Assert.Throws<MissingPartException>(() => Volumes.Find(z01));
        Assert.Equal("disk.zip", ex.PartName);
    }
}
