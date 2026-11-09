using Nexus.Core;
using Nexus.Core.Formatting;
using Nexus.Core.Models;
using Xunit;

namespace Nexus.Core.Tests;

public class CoreTests
{
    [Theory]
    [InlineData(0, "0 o")]
    [InlineData(1023, "1023 o")]
    public void ByteSize_small(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void ByteSize_scales_units()
    {
        Assert.Contains("Ko", ByteSize.Format(2048));
        Assert.Contains("Go", ByteSize.Format(5L << 30));
        Assert.StartsWith("0", ByteSize.Format(-5));
    }

    [Theory]
    [InlineData("a.PNG", "png")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData(".gitignore", "")]
    [InlineData("noext", "")]
    [InlineData("trailing.", "")]
    public void Extension_of(string name, string ext) => Assert.Equal(ext, FileEntry.ExtensionOf(name));

    [Theory]
    [InlineData("NEXUS", "1.0.1", true)]
    [InlineData("NEXUS                    ", "1.0.1   ", true)]   // padded, as Inno Setup writes it
    [InlineData("NEXUS", "1.0.0", false)]                         // same version
    [InlineData("NEXUS", "0.9.9", false)]                         // older
    [InlineData("Autre", "9.0.0", false)]
    [InlineData(null, "9.0.0", false)]
    [InlineData("NEXUS", "pas une version", false)]
    public void Update_check(string? name, string? version, bool accepted)
    {
        var (v, error) = UpdateCheck.Evaluate(name, version, new Version(1, 0, 0, 0));
        Assert.Equal(accepted, v is not null);
        Assert.Equal(accepted, error is null);
    }

    [Fact]
    public void File_types()
    {
        Assert.Equal(FileCategory.Image, FileTypes.Of("png"));
        Assert.Equal("Dossier", FileTypes.Label("", true));
        Assert.Equal("Image PNG", FileTypes.Label("png", false));
    }
}
