using Nexus.Search;
using Xunit;

namespace Nexus.Search.Tests;

public sealed class InstalledProgramsTests : IDisposable
{
    private const ushort Window = 2, Console = 3;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-programs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Exe(string relative, ushort subsystem = Window)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[1024];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3C] = 0x80;
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        bytes[0x80 + 92] = (byte)subsystem;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private List<LaunchableApp> Find(params LaunchableApp[] known) => InstalledPrograms.Find([_root], known);

    [Fact]
    public void Only_the_program_named_after_its_folder_is_kept()
    {
        string main = Exe(@"Oracle\VirtualBox\VirtualBox.exe");
        Exe(@"Oracle\VirtualBox\VirtualBoxVM.exe");
        Exe(@"Oracle\VirtualBox\VBoxManage.exe");
        Assert.Equal([main], Find().Select(a => a.Target));
    }

    [Fact]
    public void A_folder_named_with_several_words_matches_one_of_them() =>
        Assert.Equal([Exe(@"Mozilla Firefox\firefox.exe")], Find().Select(a => a.Target));

    [Fact]
    public void A_program_in_a_subfolder_matches_the_folder_above() =>
        Assert.Equal([Exe(@"Tool\bin\tool.exe")], Find().Select(a => a.Target));

    [Fact]
    public void A_helper_that_only_starts_like_its_folder_is_ignored()
    {
        Exe(@"Python\pythonw.exe");
        Exe(@"Steam\bin\steam_tool.exe");
        Assert.Empty(Find());
    }

    [Fact]
    public void A_command_line_tool_is_not_an_application()
    {
        Exe(@"Node\node.exe", Console);
        Assert.Empty(Find());
    }

    [Theory]
    [InlineData(@"Foo\FooUpdater.exe")]
    [InlineData(@"Foo\FooSetup.exe")]
    [InlineData(@"Foo\unins000.exe")]
    [InlineData(@"NvContainer\nvcontainer.exe")]
    [InlineData(@"Common Files\Foo\Foo.exe")]
    public void Auxiliary_programs_are_ignored(string path)
    {
        Exe(path);
        Assert.Empty(Find());
    }

    [Fact]
    public void A_program_the_Start_menu_already_lists_is_not_repeated()
    {
        Exe(@"Foo\Foo.exe");
        Exe(@"Bar\Bar.exe");
        Assert.Empty(Find(new LaunchableApp("Foo", "Foo.App!Main"), new LaunchableApp("Autre nom", @"{6D809377}\Bar\bar.exe")));
    }

    [Fact]
    public void A_program_named_in_the_installed_list_is_kept_whatever_its_folder()
    {
        string main = Exe(@"CEDAR Logic\cedarls.exe");
        Assert.Equal([main], InstalledPrograms.Find([_root], [], ["CEDARLS 1.5 beta"]).Select(a => a.Target));
    }

    [Fact]
    public void A_program_listed_under_a_longer_name_is_not_repeated()
    {
        Exe(@"Opera\opera.exe");
        Assert.Empty(Find(new LaunchableApp("Navigateur Opera", "OperaSoftware.Opera.123")));
    }

    [Fact]
    public void The_same_program_in_two_places_appears_once()
    {
        Exe(@"Foo\Foo.exe");
        Exe(@"Foo\bin\Foo.exe");
        Assert.Single(Find());
    }

    [Fact]
    public void A_missing_root_is_ignored() => Assert.Empty(InstalledPrograms.Find([Path.Combine(_root, "absent")], []));
}
