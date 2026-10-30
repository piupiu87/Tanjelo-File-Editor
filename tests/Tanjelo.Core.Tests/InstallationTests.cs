namespace Tanjelo.Core.Tests;

/// <summary>
/// What the installer's hooks do to the user's PATH, worked out as text: the hooks themselves change Windows, so they
/// run on CI, which installs and uninstalls for real.
/// </summary>
public class InstallationTests
{
    const string Folder = @"C:\Users\someone\AppData\Local\Tanjelo\current";

    [Fact]
    public void Installing_adds_the_folder_once_at_the_end()
    {
        Assert.Equal(Folder, Installation.WithFolder("", Folder));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;" + Folder, Installation.WithFolder(@"%USERPROFILE%\bin;C:\Tools", Folder));
        Assert.Equal(@"C:\Tools;" + Folder, Installation.WithFolder(@"C:\Tools;", Folder));
        // Already there, however it's written: left as it was.
        foreach (var path in new[] { Folder, Folder + @"\", Folder.ToUpperInvariant(), $"\"{Folder}\"", @"C:\Tools;" + Folder + ";" })
            Assert.Equal(path, Installation.WithFolder(path, Folder));
    }

    [Fact]
    public void Uninstalling_takes_only_the_folder_away()
    {
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools", Installation.WithoutFolder($@"%USERPROFILE%\bin;{Folder};C:\Tools", Folder));
        Assert.Equal(@"C:\Tools", Installation.WithoutFolder($@"C:\Tools;{Folder}\;{Folder.ToUpperInvariant()}", Folder));
        Assert.Equal("", Installation.WithoutFolder(Folder, Folder));
        Assert.Equal(@"C:\Tools;C:\Tanjelo\current-old", Installation.WithoutFolder(@"C:\Tools;C:\Tanjelo\current-old", Folder));
    }
}
