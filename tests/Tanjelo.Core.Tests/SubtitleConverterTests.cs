using System.Text;

namespace Tanjelo.Core.Tests;

public class SubtitleConverterTests
{
    const string Srt = """
        1
        00:00:01,000 --> 00:00:03,500
        Hello <i>there</i> & welcome.
        {\an8}Second line

        2
        00:01:02,250 --> 00:01:04,000
        <font color="#ffff00">Fish < chips</font>

        """;

    const string Vtt = """
        WEBVTT - Example
        Kind: captions

        NOTE This is a comment
        that spans two lines.

        STYLE
        ::cue { color: yellow }

        intro
        00:01.000 --> 00:03.500 align:start position:10%
        <v Ann>Hello <b.loud>there</b> &amp; welcome.</v>

        01:00:00.000 --> 01:00:02.000
        Fish &lt; chips<00:00:01.500> again

        """;

    [Fact]
    public async Task Srt_becomes_vtt()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("vtt", scratch.Text("movie.srt", Srt));

        Assert.Equal(scratch.PathOf("movie.vtt"), output);
        Assert.Equal("""
            WEBVTT

            00:00:01.000 --> 00:00:03.500
            Hello <i>there</i> &amp; welcome.
            Second line

            00:01:02.250 --> 00:01:04.000
            Fish &lt; chips


            """.ReplaceLineEndings("\r\n"), File.ReadAllText(output));
        Assert.False(HasBom(output));
    }

    [Fact]
    public async Task Vtt_becomes_srt()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("srt", scratch.Text("movie.vtt", Vtt));

        Assert.Equal("""
            1
            00:00:01,000 --> 00:00:03,500
            Hello <b>there</b> & welcome.

            2
            01:00:00,000 --> 01:00:02,000
            Fish < chips again


            """.ReplaceLineEndings("\r\n"), File.ReadAllText(output));
        Assert.True(HasBom(output));
    }

    [Theory]
    [InlineData("movie.srt", Srt, "Hello there & welcome.\r\nSecond line\r\nFish < chips\r\n")]
    [InlineData("movie.vtt", Vtt, "Hello there & welcome.\r\nFish < chips again\r\n")]
    public async Task Subtitles_become_plain_text(string name, string content, string expected)
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("txt", scratch.Text(name, content));
        Assert.Equal(expected, File.ReadAllText(output));
    }

    [Fact]
    public async Task Rolling_captions_dont_repeat_lines_in_text()
    {
        using var scratch = new Scratch();
        var srt = scratch.Text("auto.srt", """
            1
            00:00:01,000 --> 00:00:02,000
            so today we're

            2
            00:00:02,000 --> 00:00:03,000
            so today we're
            looking at subtitles

            """);

        var output = await Env.RunOne("txt", srt);

        Assert.Equal("so today we're\r\nlooking at subtitles\r\n", File.ReadAllText(output));
    }

    [Fact]
    public async Task Legacy_ansi_subtitles_are_read_correctly()
    {
        using var scratch = new Scratch();
        var srt = scratch.PathOf("café.srt");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé crème\r\n", Encoding.GetEncoding(1252));

        var output = await Env.RunOne("vtt", srt);

        Assert.Contains("Café crème", File.ReadAllText(output, Encoding.UTF8));
    }

    [Fact]
    public async Task A_file_without_cues_fails_clearly()
    {
        using var scratch = new Scratch();
        var empty = scratch.Text("empty.vtt", "WEBVTT\r\n\r\nNOTE nothing here\r\n");
        var engine = Env.Engine();

        var result = await engine.RunAsync([empty], Env.Offered(engine, "srt", [empty]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("No subtitles were found in this file.", Assert.Single(result.Failures).Message);
        Assert.Equal(["empty.vtt"], scratch.Files());
    }

    [Fact]
    public void Subtitle_files_offer_the_other_format_and_text()
    {
        using var scratch = new Scratch();
        Assert.Equal(["vtt", "txt"], Env.Keys(Section.Document, scratch.Text("a.srt", Srt)));
        Assert.Equal(["srt", "txt"], Env.Keys(Section.Document, scratch.Text("a.vtt", Vtt)));
    }

    static bool HasBom(string path) => File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..];
}
