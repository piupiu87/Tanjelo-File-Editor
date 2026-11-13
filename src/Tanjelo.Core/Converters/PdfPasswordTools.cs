using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Protect… saves a copy encrypted with AES-256 (so it's PDF 2.0) behind a password to open, limits on printing,
/// copying and changes, or both. Remove password… saves a copy without encryption, given the password to open, or the
/// permissions password when the PDF has limits. Which of the two a PDF gets is read from its trailer,
/// without parsing it. Passwords go nowhere but into PDFsharp: they're never remembered, logged or shown back.
/// </summary>
public sealed class PdfPasswordTools : IConverter, IValidator
{
    static readonly Option[] ProtectOptions =
    [
        new("password", Strings.PasswordToOpen, OptionKind.Password, "") { Optional = true, Repeat = true, Remember = false, Hint = Strings.PasswordToOpenHint },
        new("limit", Strings.LimitPrintingCopyingAndChanges, OptionKind.Toggle, "false") { Hint = Strings.LimitsAreHonouredByReaders },
        new("printing", Strings.AllowPrinting, OptionKind.Choice, "full")
        {
            Choices = [new("full", Strings.AtFullQuality), new("low", Strings.AtLowQuality), new("none", Strings.NotAtAll)],
            ShownWhen = new("limit", "true"),
        },
        new("copying", Strings.AllowCopyingTextAndPictures, OptionKind.Toggle, "false") { ShownWhen = new("limit", "true") },
        new("changes", Strings.AllowChangesCommentsAndFormFilling, OptionKind.Toggle, "false") { ShownWhen = new("limit", "true") },
        new("permissions-password", Strings.PermissionsPassword, OptionKind.Password, "")
        {
            Repeat = true, Remember = false, ShownWhen = new("limit", "true"), Hint = Strings.PermissionsPasswordHint,
        },
    ];

    static readonly Target Protect = new Target("protect-pdf", Strings.Protect, Strings.APasswordOrLimitsOnPrinting, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Protecting,
        Done = Strings.Protected,
        Confirm = Strings.ProtectButton,
    }.WithOptions(ProtectOptions);

    /// <summary>The field asks for the password that will work: the permissions password when there are limits.</summary>
    static Target Unlock(bool? limits) => new Target("unlock-pdf", Strings.RemovePassword, Strings.ACopyWithoutEncryption, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.RemovingThePassword,
        Done = Strings.PasswordRemoved,
        Confirm = Strings.RemovePasswordButton,
    }.WithOptions(new Option("password", limits switch
    {
        true => Strings.PermissionsPassword,
        false => Strings.PasswordToOpen,
        null => Strings.Password,
    }, OptionKind.Password, "")
    {
        Remember = false,
        Hint = limits switch
        {
            true => Strings.ThisPDFLimitsPrintingCopyingOrChanges,
            false => Strings.ThePasswordAskedForWhenItOpens,
            null => Strings.ItsPasswordToOpenOrItsPermissionsPassword,
        },
    });

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (Formats.Key(input) != "pdf") return [];
        return PdfFiles.EncryptionOf(input) is { } encryption ? [Unlock(encryption.Limits)] : [Protect];
    }

    public Task<SettingsProblem?> ValidateAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run(() =>
    {
        if (target.Key == "protect-pdf") return ProtectProblem(target);
        try
        {
            using var _ = PdfFiles.OpenForChanges(inputs[0], target.Password("password"));
            return null;
        }
        catch (ConversionException ex) when (ex.InnerException is PdfSharp.Pdf.IO.PdfReaderException)
        {
            return new SettingsProblem("password", ex.Message);
        }
    }, ctx.Cancellation);

    /// <summary>What's missing from Protect's settings, which only make sense together.</summary>
    static SettingsProblem? ProtectProblem(Target target)
    {
        var open = target.Password("password");
        if (!target.Flag("limit"))
            return open.Length == 0 ? new SettingsProblem("password", Strings.GiveAPasswordToOpenOrLimit) : null;
        return target.Password("permissions-password") == open ? new SettingsProblem("permissions-password", Strings.ThePermissionsPasswordMustDiffer) : null;
    }

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
        target.Key == "protect-pdf" ? [ProtectCopy(input, target, ctx)] : [UnlockCopy(input, target, ctx)], ctx.Cancellation);

    static string ProtectCopy(string input, Target target, JobContext ctx)
    {
        if (ProtectProblem(target) is { } problem) throw new ConversionException(problem.Message);
        if (PdfFiles.EncryptionOf(input) is not null) throw new ConversionException(Strings.ThisPDFIsAlreadyProtected);
        using var document = PdfFiles.OpenForChanges(input);
        PdfFiles.NoteIfSigned(document, ctx);

        var security = document.SecuritySettings;
        var open = target.Password("password");
        if (open.Length > 0) security.UserPassword = open;
        // Without limits there's no permissions password: PDFsharp then uses the password to open for both, so that
        // password alone removes the protection again.
        if (target.Flag("limit"))
        {
            security.OwnerPassword = target.Password("permissions-password");
            var printing = target.Choice("printing");
            security.PermitPrint = printing != "none";
            security.PermitFullQualityPrint = printing == "full";
            security.PermitExtractContent = target.Flag("copying");
            var changes = target.Flag("changes");
            security.PermitModifyDocument = changes;
            security.PermitAnnotations = changes;
            security.PermitFormsFill = changes;
            security.PermitAssembleDocument = changes;
        }
        document.SecurityHandler.SetEncryptionToV5(encryptMetadata: true);

        var output = OutputPaths.Suffixed(input, "protected", "pdf");
        PdfFiles.Save(document, output);
        ctx.Progress.Report(1);
        return output;
    }

    static string UnlockCopy(string input, Target target, JobContext ctx)
    {
        if (PdfFiles.EncryptionOf(input) is null) throw new ConversionException(Strings.ThisPDFIsnTPasswordProtected);
        using var document = PdfFiles.OpenForChanges(input, target.Password("password"));
        PdfFiles.NoteIfSigned(document, ctx);
        document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();

        var output = OutputPaths.Suffixed(input, "unlocked", "pdf");
        PdfFiles.Save(document, output);
        ctx.Progress.Report(1);
        return output;
    }
}
