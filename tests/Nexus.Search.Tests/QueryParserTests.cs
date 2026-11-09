using Nexus.Search;
using Xunit;

namespace Nexus.Search.Tests;

public class QueryParserTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 15, 0, 0, DateTimeKind.Local); // a Sunday

    [Fact]
    public void Plain_words_become_terms_and_quotes_group()
    {
        var q = QueryParser.Parse("rapport \"mon fichier\" -brouillon", Now);
        Assert.Equal(["rapport", "mon fichier"], q.Terms);
        Assert.Equal(["brouillon"], q.ExcludeTerms);
    }

    [Fact]
    public void Star_dot_ext_and_ext_filter()
    {
        var q = QueryParser.Parse("*.png ext:.JPG,gif -ext:tmp", Now);
        Assert.Equal(["png", "jpg", "gif"], q.Extensions);
        Assert.Equal(["tmp"], q.ExcludeExtensions);
        Assert.Empty(q.Terms);
    }

    [Theory]
    [InlineData("size:>1gb", 1073741825L, null)]
    [InlineData("size:<10mb", null, 10485759L)]
    [InlineData("size:1mb..5mb", 1048576L, 5242880L)]
    [InlineData("size:>=2kb", 2048L, null)]
    public void Size_filters(string text, long? min, long? max)
    {
        var q = QueryParser.Parse(text, Now);
        Assert.Null(q.Error);
        Assert.Equal(min, q.MinSize);
        Assert.Equal(max, q.MaxSize);
    }

    [Fact]
    public void Type_filter_expands_to_extensions_and_unknown_type_is_an_error()
    {
        Assert.Contains("png", QueryParser.Parse("type:image", Now).Extensions);
        Assert.NotNull(QueryParser.Parse("type:bogus", Now).Error);
    }

    [Fact]
    public void Week_starts_on_monday()
    {
        var q = QueryParser.Parse("dm:week", Now);
        var expectedFrom = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks; // Monday 2 Nov
        Assert.Equal(expectedFrom, q.Modified!.Value.FromTicks);
        Assert.Null(q.Modified.Value.ToTicks);
    }

    [Fact]
    public void Absolute_day_covers_24_hours()
    {
        var r = QueryParser.Parse("dc:2026-01-31", Now).Created!.Value;
        Assert.Equal(TimeSpan.FromHours(24), TimeSpan.FromTicks(r.ToTicks!.Value - r.FromTicks!.Value));
    }

    [Fact]
    public void Paths_regex_flags_and_sort()
    {
        var q = QueryParser.Parse(@"path:Documents\Projets /^img_\d+/ is:folder ~documnt sort:-size", Now);
        Assert.Equal([@"Documents\Projets"], q.PathContains);
        Assert.Equal(@"^img_\d+", q.Regex);
        Assert.True(q.IsDirectory);
        Assert.True(q.Fuzzy);
        Assert.Equal(["documnt"], q.Terms);
        Assert.Equal(Nexus.Core.Search.SortField.Size, q.Sort);
        Assert.True(q.Descending);
    }

    [Fact]
    public void Invalid_regex_and_drive_paths()
    {
        Assert.NotNull(QueryParser.Parse("regex:(", Now).Error);
        var q = QueryParser.Parse(@"C:\Users\foo", Now);
        Assert.Equal([@"C:\Users\foo"], q.PathContains);
    }

    [Fact]
    public void Empty_and_half_typed_filters_are_empty_queries()
    {
        Assert.True(QueryParser.Parse("", Now).IsEmpty);
        Assert.True(QueryParser.Parse("ext:", Now).IsEmpty);
    }
}
