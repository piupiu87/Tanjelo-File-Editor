using PdfSharp.Pdf;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class PdfFilesTests
{
    [Fact]
    public void Pages_can_be_copied_into_a_new_document()
    {
        using var scratch = new Scratch();
        using var source = PdfFiles.OpenForImport(scratch.Pdf("three.pdf", pages: 3));
        using var copy = PdfFiles.NewLike(source);

        copy.AddPage(source.Pages[2]);
        PdfFiles.Save(copy, scratch.PathOf("third.pdf"));

        Assert.Equal(1, Inspect.PdfPages(scratch.PathOf("third.pdf")));
    }

    [Fact]
    public void A_password_protected_pdf_is_reported_as_such()
    {
        using var scratch = new Scratch();
        var locked = scratch.PathOf("locked.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "secret";
            document.Save(locked);
        }

        var error = Assert.Throws<ConversionException>(() => PdfFiles.OpenForImport(locked));
        Assert.Equal("This PDF is password-protected.", error.Message);
    }

    [Fact]
    public void A_damaged_pdf_is_reported_as_invalid()
    {
        using var scratch = new Scratch();
        var error = Assert.Throws<ConversionException>(() => PdfFiles.OpenForImport(scratch.Text("broken.pdf", "%PDF-1.4 nothing else")));
        Assert.Equal("This doesn't look like a valid PDF.", error.Message);
    }
}
