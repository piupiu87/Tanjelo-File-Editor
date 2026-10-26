using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Writes a PowerPoint file of picture slides with the Open XML SDK, so no PowerPoint is needed. The deck has one
/// master, one blank layout and a plain theme; each picture fills its slide, centred if its shape differs, and a
/// slide can carry speaker notes.
/// </summary>
internal sealed class SlideDeck : IDisposable
{
    /// <summary>English Metric Units: PowerPoint's unit of length.</summary>
    public const long EmuPerInch = 914400;

    // PowerPoint's limits for a slide's sides: 1 to 56 inches.
    const long MinSide = EmuPerInch, MaxSide = 56 * EmuPerInch;

    const string Namespaces = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;

    const string EmptyTree = """
        <p:spTree>
          <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
          <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
        """;

    const string ColorMap = """<p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>""";

    const string Theme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Tanjelo">
          <a:themeElements>
            <a:clrScheme name="Office">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
              <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="44546A"/></a:dk2>
              <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
              <a:accent1><a:srgbClr val="4472C4"/></a:accent1>
              <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
              <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
              <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
              <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
              <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
              <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
              <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Office">
              <a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Office">
              <a:fillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:fillStyleLst>
              <a:lnStyleLst>
                <a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
              </a:lnStyleLst>
              <a:effectStyleLst>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
              </a:effectStyleLst>
              <a:bgFillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
          <a:objectDefaults/>
          <a:extraClrSchemeLst/>
        </a:theme>
        """;

    const string Master = $"""
        <p:sldMaster {Namespaces}>
          <p:cSld>
            <p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg>
            {EmptyTree}</p:spTree>
          </p:cSld>
          {ColorMap}
          <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
          <p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>
        </p:sldMaster>
        """;

    const string Layout = $"""
        <p:sldLayout {Namespaces} type="blank" preserve="1">
          <p:cSld name="Blank">{EmptyTree}</p:spTree></p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sldLayout>
        """;

    // The notes page: the slide's picture at the top, the notes under it, on a 7.5 x 10 inch page.
    const long NotesWidth = 6858000, NotesHeight = 9144000;

    const string NotesMaster = $"""
        <p:notesMaster {Namespaces}>
          <p:cSld>
            {EmptyTree}
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Slide Image"/><p:cNvSpPr><a:spLocks noGrp="1" noRot="1" noChangeAspect="1"/></p:cNvSpPr><p:nvPr><p:ph type="sldImg" idx="2"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="685800"/><a:ext cx="5486400" cy="3429000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/><a:ln w="12700"><a:solidFill><a:prstClr val="black"/></a:solidFill></a:ln></p:spPr>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Notes"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" sz="quarter" idx="3"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="4400550"/><a:ext cx="5486400" cy="3600450"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
          {ColorMap}
          <p:notesStyle><a:lvl1pPr><a:defRPr sz="1200"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr></p:notesStyle>
        </p:notesMaster>
        """;

    readonly PresentationDocument _document;
    readonly PresentationPart _presentation;
    readonly SlideLayoutPart _layout;
    readonly ThemePart _theme;
    readonly Presentation _root;
    NotesMasterPart? _notesMaster;
    uint _nextSlideId = 256;
    int _count;

    public long Width { get; }
    public long Height { get; }

    public int Count => _count;

    /// <summary>Starts a deck at <paramref name="path"/> with slides of about this size, within PowerPoint's limits.</summary>
    public SlideDeck(string path, long width, long height)
    {
        (Width, Height) = Fit(width, height);
        _document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        _presentation = _document.AddPresentationPart();

        var master = _presentation.AddNewPart<SlideMasterPart>("rId1");
        _layout = master.AddNewPart<SlideLayoutPart>("rId1");
        _theme = master.AddNewPart<ThemePart>("rId2");
        Feed(_theme, Theme);
        Feed(_layout, Layout);
        _layout.AddPart(master, "rId1");
        Feed(master, Master);
        _presentation.AddPart(_theme);

        _presentation.Presentation = _root = new Presentation(
            new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = _presentation.GetIdOfPart(master) }),
            new SlideIdList(),
            new SlideSize { Cx = (int)Width, Cy = (int)Height, Type = SlideSizeValues.Custom },
            new NotesSize { Cx = NotesWidth, Cy = NotesHeight },
            new DefaultTextStyle());
    }

    /// <summary>Within PowerPoint's 1 to 56 inch limits, keeping the shape where that's possible.</summary>
    internal static (long Width, long Height) Fit(long width, long height)
    {
        double w = Math.Max(1, width), h = Math.Max(1, height);
        var shrink = Math.Min(1, MaxSide / Math.Max(w, h));
        (w, h) = (w * shrink, h * shrink);
        var grow = Math.Max(1, MinSide / Math.Min(w, h));
        (w, h) = (Math.Min(MaxSide, w * grow), Math.Min(MaxSide, h * grow));
        return ((long)Math.Round(w), (long)Math.Round(h));
    }

    /// <summary>Adds a slide filled by a PNG or JPEG of <paramref name="pixelWidth"/> by <paramref name="pixelHeight"/>.</summary>
    /// <param name="name">What the picture shows, for screen readers, such as "Page 3".</param>
    public void Add(byte[] picture, bool jpeg, int pixelWidth, int pixelHeight, string name, string? notes = null)
    {
        _count++;
        var slide = _presentation.AddNewPart<SlidePart>();
        slide.AddPart(_layout);
        var image = slide.AddImagePart(jpeg ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var bytes = new MemoryStream(picture, writable: false))
            image.FeedData(bytes);

        // Centred at its own shape: as wide as the slide, or as tall, whichever fits. A page rendered to whole
        // pixels is a hair off its slide's shape, and fills it all the same.
        var ratio = (double)pixelWidth / pixelHeight;
        long cx = Width, cy = (long)Math.Round(Width / ratio);
        if (cy > Height) (cx, cy) = ((long)Math.Round(Height * ratio), Height);
        if (Math.Abs(ratio / ((double)Width / Height) - 1) < 0.01) (cx, cy) = (Width, Height);

        slide.Slide = new Slide(
            new CommonSlideData(new ShapeTree(
                GroupProperties(),
                new GroupShapeProperties(new A.TransformGroup(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 },
                    new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 0, Cy = 0 })),
                new Picture(
                    new NonVisualPictureProperties(
                        new NonVisualDrawingProperties { Id = 2U, Name = name, Description = name },
                        new NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    new BlipFill(new A.Blip { Embed = slide.GetIdOfPart(image) }, new A.Stretch(new A.FillRectangle())),
                    new ShapeProperties(
                        new A.Transform2D(new A.Offset { X = (Width - cx) / 2, Y = (Height - cy) / 2 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))),
            new ColorMapOverride(new A.MasterColorMapping()));

        if (!string.IsNullOrWhiteSpace(notes))
            AddNotes(slide, notes);

        _root.SlideIdList!.Append(new SlideId { Id = _nextSlideId++, RelationshipId = _presentation.GetIdOfPart(slide) });
    }

    void AddNotes(SlidePart slide, string notes)
    {
        if (_notesMaster is null)
        {
            _notesMaster = _presentation.AddNewPart<NotesMasterPart>();
            var theme = _notesMaster.AddNewPart<ThemePart>("rId1");
            Feed(theme, Theme);
            Feed(_notesMaster, NotesMaster);
            // The list of notes masters comes right after the slide masters.
            _root.InsertAfter(new NotesMasterIdList(new NotesMasterId { Id = _presentation.GetIdOfPart(_notesMaster) }), _root.SlideMasterIdList);
        }

        var part = slide.AddNewPart<NotesSlidePart>();
        part.AddPart(_notesMaster);
        part.AddPart(slide);
        var paragraphs = notes.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Length == 0
                ? new A.Paragraph(new A.EndParagraphRunProperties())
                : new A.Paragraph(new A.Run(new A.RunProperties(), new A.Text(XmlSafe(line)))));
        part.NotesSlide = new NotesSlide(
            new CommonSlideData(new ShapeTree(
                GroupProperties(),
                new GroupShapeProperties(new A.TransformGroup()),
                new Shape(
                    new NonVisualShapeProperties(
                        new NonVisualDrawingProperties { Id = 2U, Name = "Slide Image" },
                        new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true, NoRotation = true, NoChangeAspect = true }),
                        new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.SlideImage })),
                    new ShapeProperties()),
                new Shape(
                    new NonVisualShapeProperties(
                        new NonVisualDrawingProperties { Id = 3U, Name = "Notes" },
                        new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                        new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Body, Index = 1U })),
                    new ShapeProperties(),
                    new TextBody([new A.BodyProperties(), new A.ListStyle(), .. paragraphs])))),
            new ColorMapOverride(new A.MasterColorMapping()));
    }

    static NonVisualGroupShapeProperties GroupProperties() => new(
        new NonVisualDrawingProperties { Id = 1U, Name = "" },
        new NonVisualGroupShapeDrawingProperties(),
        new ApplicationNonVisualDrawingProperties());

    /// <summary>XML can't hold most control characters, which some PDFs' text has.</summary>
    static string XmlSafe(string text) => string.Concat(text.Where(c => c is '\t' or '\n' or '\r' || !char.IsControl(c)));

    static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(xml));
        part.FeedData(stream);
    }

    /// <summary>Writes the deck and closes the file.</summary>
    public void Save()
    {
        _root.Save();
        _document.Dispose();
    }

    public void Dispose() => _document.Dispose();
}
