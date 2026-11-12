using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PdfSharp.Pdf.IO;
using Tanjelo.Core.Converters;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Protect… and Remove password…, and the rule that passwords go nowhere but into the PDF.</summary>
public class PdfPasswordTests
{
    // Made up for these tests.
    const string Open = "Open-Sesame-2026";
    const string Permissions = "Perm-Lift-5678";

    static async Task<BatchResult> Run(string key, string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, key, [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static string Plain(Scratch scratch) => scratch.BookmarkedPdf("report.pdf", ("Introduction", 2));

    [Fact]
    public void The_trailer_decides_between_Protect_and_Remove_password()
    {
        using var scratch = new Scratch();
        var plain = Plain(scratch);
        var locked = scratch.EncryptedPdf("locked.pdf", "aes256", Open);
        var limited = scratch.EncryptedPdf("limited.pdf", "aes128", null, Permissions, limits: true);
        var both = scratch.EncryptedPdf("both.pdf", "rc4", Open, Permissions, limits: true);
        var engine = Env.Engine();

        Assert.Contains("protect-pdf", Env.Keys(plain));
        Assert.DoesNotContain("unlock-pdf", Env.Keys(plain));
        Assert.Null(PdfFiles.EncryptionOf(plain));
        foreach (var (pdf, label) in new[] { (locked, "Password to open"), (limited, "Permissions password"), (both, "Permissions password") })
        {
            Assert.DoesNotContain("protect-pdf", Env.Keys(pdf));
            var unlock = Env.Offered(engine, "unlock-pdf", [pdf]);
            Assert.Equal(label, Assert.Single(unlock.Options).Label);
        }
    }

    [Theory]
    [InlineData("full", false, false)]
    [InlineData("low", true, false)]
    [InlineData("none", false, true)]
    [InlineData("full", true, true)]
    [InlineData("none", false, false)]
    public async Task Each_combination_of_limits_is_written(string printing, bool copying, bool changes)
    {
        using var scratch = new Scratch();
        var result = await Run("protect-pdf", Plain(scratch),
            ("limit", "true"), ("printing", printing), ("copying", copying.ToString()), ("changes", changes.ToString()), ("permissions-password", Permissions));

        var copy = Assert.Single(result.Outputs);
        Assert.Equal(scratch.PathOf("report-protected.pdf"), copy);
        var bits = PdfFiles.EncryptionOf(copy)!.Permissions!.Value;
        Assert.Equal(printing != "none", (bits & 1 << 2) != 0);
        Assert.Equal(printing == "full", (bits & 1 << 11) != 0);
        Assert.Equal(copying, (bits & 1 << 4) != 0);
        foreach (var bit in new[] { 3, 5, 8, 10 }) // change, comment, fill in forms, assemble
            Assert.Equal(changes, (bits & 1 << bit) != 0);
        Assert.Equal(!(printing == "full" && copying && changes), PdfFiles.EncryptionOf(copy)!.Limits);

        // Without a password to open it opens for anyone, and only the permissions password lifts the limits.
        using (var pdf = PigDocument.Open(copy)) Assert.Contains("Introduction page 1", pdf.GetPage(1).Text);
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(copy, PdfDocumentOpenMode.Modify));
        using (PdfReader.Open(copy, Permissions, PdfDocumentOpenMode.Modify)) { }
    }

    [Fact]
    public async Task The_protected_copy_is_AES_256_and_opens_only_with_its_password()
    {
        using var scratch = new Scratch();
        var copy = Assert.Single((await Run("protect-pdf", Plain(scratch), ("password", Open))).Outputs);

        Assert.StartsWith("%PDF-2.0", File.ReadAllText(copy)[..8]);
        Assert.Contains("/V 5", File.ReadAllText(copy).Replace("\r", " ").Replace("\n", " "));
        Assert.Throws<UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException>(() => PigDocument.Open(copy));
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(copy, PdfDocumentOpenMode.Import));
        using (var pdf = PigDocument.Open(copy, new UglyToad.PdfPig.ParsingOptions { Password = Open }))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Contains("Introduction page 2", pdf.GetPage(2).Text);
            Assert.True(pdf.TryGetBookmarks(out var bookmarks));
            Assert.Equal("Introduction", Assert.Single(bookmarks.Roots).Title);
        }
        Assert.False(PdfFiles.EncryptionOf(copy)!.Limits);
        // With no limits, the password to open is all Remove password needs.
        var unlocked = Assert.Single((await Run("unlock-pdf", copy, ("password", Open))).Outputs);
        Assert.Equal(scratch.PathOf("report-protected-unlocked.pdf"), unlocked);
        Assert.Null(PdfFiles.EncryptionOf(unlocked));
    }

    [Fact]
    public async Task Limits_need_the_permissions_password()
    {
        using var scratch = new Scratch();
        var protectedCopy = Assert.Single((await Run("protect-pdf", Plain(scratch),
            ("password", Open), ("limit", "true"), ("permissions-password", Permissions))).Outputs);

        var opened = await Run("unlock-pdf", protectedCopy, ("password", Open));
        Assert.Equal("That password opens the PDF but doesn't lift its limits on printing, copying or changes. Give its permissions password.",
            Assert.Single(opened.Failures).Message);
        var unlocked = Assert.Single((await Run("unlock-pdf", protectedCopy, ("password", Permissions))).Outputs);

        Assert.Null(PdfFiles.EncryptionOf(unlocked));
        using var pdf = PigDocument.Open(unlocked);
        Assert.Contains("Introduction page 1", pdf.GetPage(1).Text);
        Assert.True(pdf.TryGetBookmarks(out var bookmarks));
        Assert.Single(bookmarks.Roots);
    }

    [Theory]
    [InlineData("aes256")]
    [InlineData("aes128")]
    [InlineData("rc4")]
    public async Task Remove_password_unlocks_each_kind_of_encryption(string encryption)
    {
        using var scratch = new Scratch();
        var locked = scratch.EncryptedPdf("locked.pdf", encryption, Open);

        var unlocked = Assert.Single((await Run("unlock-pdf", locked, ("password", Open))).Outputs);

        Assert.Equal(scratch.PathOf("locked-unlocked.pdf"), unlocked);
        using var pdf = PigDocument.Open(unlocked);
        Assert.Equal("Locked text", pdf.GetPage(1).Text);
        Assert.Equal(1, Inspect.PdfPages(unlocked)); // Windows' own renderer opens it, AES-256 or not
    }

    [Fact]
    public async Task Wrong_settings_are_caught_before_the_dialog_closes()
    {
        using var scratch = new Scratch();
        var plain = Plain(scratch);
        var locked = scratch.EncryptedPdf("locked.pdf", "aes256", Open);
        var engine = Env.Engine();
        var ct = TestContext.Current.CancellationToken;
        Task<SettingsProblem?> Check(string key, string pdf, params (string, string)[] values) =>
            engine.ValidateAsync([pdf], Env.Offered(engine, key, [pdf]).WithParameters(values.ToDictionary(v => v.Item1, v => v.Item2)), ct);

        Assert.Equal(new SettingsProblem("password", "That password isn't right."), await Check("unlock-pdf", locked, ("password", "not-it")));
        Assert.Null(await Check("unlock-pdf", locked, ("password", Open)));
        Assert.Equal("password", (await Check("protect-pdf", plain))!.Option);
        Assert.Equal("permissions-password", (await Check("protect-pdf", plain, ("password", Open), ("limit", "true"), ("permissions-password", Open)))!.Option);
        Assert.Null(await Check("protect-pdf", plain, ("limit", "true"), ("permissions-password", Permissions)));
        Assert.Null(await engine.ValidateAsync([plain], Env.Offered(engine, "split-pdf", [plain]), ct)); // tools that don't check say nothing
        Assert.Equal(["locked.pdf", "report.pdf"], scratch.Files()); // checking writes nothing
    }

    [Fact]
    public async Task Passwords_never_reach_the_memory_file_the_log_the_card_or_tanj()
    {
        using var scratch = new Scratch();
        var plain = Plain(scratch);
        var locked = scratch.EncryptedPdf("locked.pdf", "aes256", Open);
        var engine = Env.Engine();
        var protect = Env.Offered(engine, "protect-pdf", [plain])
            .WithParameters(new Dictionary<string, string> { ["password"] = Open, ["limit"] = "true", ["permissions-password"] = Permissions });

        // tools.json: even an option that says to remember keeps no password.
        var memory = new ToolMemory(scratch.PathOf("tools.json"));
        memory.Store(protect);
        memory.Store(protect with { Options = protect.Options.Select(o => o with { Remember = true }).ToList() });
        var saved = File.ReadAllText(scratch.PathOf("tools.json"));
        Assert.DoesNotContain(Open, saved);
        Assert.DoesNotContain(Permissions, saved);
        Assert.Contains("\"limit\": \"true\"", saved);
        Assert.DoesNotContain(new ToolMemory(scratch.PathOf("tools.json")).Recall(protect).Keys, k => k.Contains("password", StringComparison.Ordinal));

        // The card and the log show the result's heading, failures and notes; the app logs failures as they are.
        var wrong = "Wrong-Guess-1111";
        var failed = await engine.RunAsync([locked], Env.Offered(engine, "unlock-pdf", [locked])
            .WithParameters(new Dictionary<string, string> { ["password"] = wrong }), ct: TestContext.Current.CancellationToken);
        var shown = string.Join("\n", failed.Failures.Select(f => f.Message).Concat(failed.Notes).Append(protect.DoneText).Append(protect.RunningText));
        Assert.Equal("That password isn't right.", Assert.Single(failed.Failures).Message);
        Assert.DoesNotContain(wrong, shown);
        var empty = Env.Offered(engine, "unlock-pdf", [locked]);
        Assert.Equal("Password to open is needed.", Assert.Throws<ConversionException>(() => empty.Password("password")).Message);

        // tanj options and targets print no password, not even one given.
        var printed = new StringWriter();
        Tanj.OptionList.Write(protect, printed);
        Assert.DoesNotContain(Open, printed.ToString());
        Assert.DoesNotContain(Permissions, printed.ToString());
        Assert.Contains("--permissions-password=", printed.ToString());
        Assert.Contains("- reads it from standard input", printed.ToString());
    }

    [Fact]
    public void Tanj_reads_passwords_from_standard_input_or_asks_with_the_typing_hidden()
    {
        using var scratch = new Scratch();
        var plain = Plain(scratch);
        var engine = Env.Engine();
        var protect = Env.Offered(engine, "protect-pdf", [plain]);
        var unlock = Env.Offered(engine, "unlock-pdf", [scratch.EncryptedPdf("locked.pdf", "aes256", Open)]);

        // "-" reads a line each, in the order the tool lists them.
        var values = new Dictionary<string, string> { ["password"] = "-", ["limit"] = "true", ["permissions-password"] = "-" };
        var errors = new StringWriter();
        Assert.True(Tanj.Passwords.Fill(protect, values, new StringReader($"{Open}\n{Permissions}\n"), errors, ask: null));
        Assert.Equal((Open, Permissions), (values["password"], values["permissions-password"]));
        Assert.Empty(errors.ToString());

        // Left out, it's asked for, twice for a new password; two different answers stop it.
        var asked = new List<string>();
        var answers = new Queue<string>([Open, Open]);
        values = [];
        Assert.True(Tanj.Passwords.Fill(protect, values, TextReader.Null, errors, prompt => { asked.Add(prompt); return answers.Dequeue(); }));
        Assert.Equal(["Password to open (Enter for none): ", "Repeat it: "], asked);
        Assert.Equal(Open, values["password"]);
        answers = new Queue<string>([Open, "typo"]);
        Assert.False(Tanj.Passwords.Fill(protect, [], TextReader.Null, errors, _ => answers.Dequeue()));
        Assert.Contains("The two passwords differ", errors.ToString());

        // Nobody to ask: an optional password is left empty, a needed one is an error.
        Assert.True(Tanj.Passwords.Fill(protect, values = [], TextReader.Null, errors = new StringWriter(), ask: null));
        Assert.False(values.ContainsKey("password"));
        Assert.False(Tanj.Passwords.Fill(unlock, [], TextReader.Null, errors, ask: null));
        Assert.Equal("--password (Password to open) is needed; --password=- reads it from standard input.", errors.ToString().Trim());

        // Typed on the command line it works, with a warning that names the setting, never the password.
        values = new Dictionary<string, string> { ["password"] = Open };
        Assert.True(Tanj.Passwords.Fill(unlock, values, TextReader.Null, errors = new StringWriter(), ask: null));
        Assert.Equal(Open, values["password"]);
        Assert.Contains("stays in the shell's history", errors.ToString());
        Assert.DoesNotContain(Open, errors.ToString());
    }

    [Fact]
    public async Task Pdfs_Windows_cant_render_because_of_AES_256_get_the_password_message()
    {
        using var scratch = new Scratch();
        var aes256 = scratch.EncryptedPdf("aes256.pdf", "aes256", null, Permissions, limits: true);
        var aes128 = scratch.EncryptedPdf("aes128.pdf", "aes128", null, Permissions, limits: true);
        var engine = Env.Engine();

        var refused = await engine.RunAsync([aes256], Env.Offered(engine, "png", [aes256]), ct: TestContext.Current.CancellationToken);
        Assert.Equal("This PDF is password-protected. Remove its password first with Remove password…", Assert.Single(refused.Failures).Message);
        Assert.Single((await Env.Run("png", aes128)).Outputs);
    }

    [Fact]
    public async Task A_signed_pdf_says_its_signature_wont_hold_in_the_copy()
    {
        using var scratch = new Scratch();
        var result = await Run("protect-pdf", scratch.SignedPdf("signed.pdf"), ("password", Open));

        Assert.Single(result.Outputs);
        Assert.Equal("This PDF was digitally signed, and the signature isn't valid in the changed copy.", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task The_dialog_hides_passwords_asks_twice_and_keeps_a_wrong_one_in_place()
    {
        using var scratch = new Scratch();
        var plain = Plain(scratch);
        var locked = scratch.EncryptedPdf("locked.pdf", "aes256", Open);
        await Ui.Run(async () =>
        {
            var engine = Env.Engine();
            var protect = new OptionsWindow(engine, [plain], Env.Offered(engine, "protect-pdf", [plain]), new Dictionary<string, string>());
            var host = Ui.Host(protect, 700, 900);
            using (A11y.Attach(host))
            {
                await Ui.Settle();
                var boxes = Ui.Find<PasswordBox>(host).ToList();
                Assert.Equal(2, boxes.Count(Ui.Showing)); // the password and its repeat; the limits are off
                Assert.Equal("Password to open", AutomationProperties.GetName(boxes[0]));
                Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.Edit, System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(boxes[0]).GetAutomationControlType());
                Assert.True(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(boxes[0]).IsPassword());
                boxes[0].Password = Open;
                boxes[1].Password = "typo";
                Assert.False(protect.RunButton.IsEnabled);
                Assert.Contains("Password to open isn't the same in both boxes.", Ui.Texts(host));
                boxes[1].Password = Open;
                Assert.True(protect.RunButton.IsEnabled);
                var show = Ui.Find<Button>(host).Single(b => AutomationProperties.GetName(b) == "Show the password to open");
                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Contains(Ui.Find<TextBox>(host), b => Ui.Showing(b) && b.Text == Open);
                A11y.Check(host, "protect");
                Ui.Render(host, "options-protect-pdf");
            }
            protect.Close();

            var unlock = new OptionsWindow(engine, [locked], Env.Offered(engine, "unlock-pdf", [locked]), new Dictionary<string, string>());
            var asked = unlock.AskAsync();
            host = Ui.Host(unlock, 700, 600);
            await Ui.Settle();
            var box = Ui.Find<PasswordBox>(host).Single();
            box.Password = "not-it";
            unlock.RunButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Ui.Until(() => Ui.Texts(host).Contains("That password isn't right."));
            Assert.False(asked.IsCompleted);
            Ui.Render(host, "options-unlock-pdf-wrong");
            box.Password = Open;
            Assert.DoesNotContain("That password isn't right.", Ui.Texts(host));
            unlock.RunButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var chosen = await asked;
            Assert.Equal(Open, chosen!.Password("password"));
        });
    }
}
