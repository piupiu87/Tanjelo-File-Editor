using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Windows.AI.MachineLearning;
using Windows.Storage.Streams;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Finds faces with YuNet (MIT, from OpenCV Zoo), a small face finder that Windows' own machine learning runs, on this PC.
/// It finds faces from about 10 to 300 pixels across, turned to the side, tilted, dim or partly covered, where Windows'
/// face detection needs them upright and facing the camera. Windows 10's machine learning can't read it (it takes
/// models up to ONNX opset 9, and YuNet's is 11), so there it isn't available.
/// </summary>
static class YuNet
{
    /// <summary>A face counts from this score: low, as a missed face would show, and a box too many is easy to delete.</summary>
    const float Threshold = 0.5f;

    /// <summary>The widest picture looked at; faces are found at 10 to 300 pixels, so smaller copies take in bigger ones.</summary>
    const int Largest = 2560;

    const int Smallest = 120;

    static readonly int[] Strides = [8, 16, 32];

    static readonly Lazy<LearningModel?> Model = new(Load);

    public static bool IsAvailable => Model.Value is not null;

    static LearningModel? Load()
    {
        try
        {
            using var resource = typeof(YuNet).Assembly.GetManifestResourceStream("yunet.onnx")!;
            var memory = new MemoryStream();
            resource.CopyTo(memory);
            // In Windows' own kind of stream: given a .NET stream, Windows' machine learning throws "not implemented".
            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(memory.ToArray().AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            return LearningModel.LoadFromStream(RandomAccessStreamReference.CreateFromStream(stream));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    readonly record struct Found(float X1, float Y1, float X2, float Y2, float Score)
    {
        public float Area => (X2 - X1) * (Y2 - Y1);

        public float Overlap(Found o)
        {
            var w = Math.Min(X2, o.X2) - Math.Max(X1, o.X1);
            var h = Math.Min(Y2, o.Y2) - Math.Max(Y1, o.Y1);
            return w <= 0 || h <= 0 ? 0 : w * h / (Area + o.Area - w * h);
        }
    }

    /// <summary>The faces in an upright picture, in its own pixels, each box a little wider to take in the hair.</summary>
    public static IReadOnlyList<RedactBox> Find(IMagickImage<byte> picture)
    {
        var model = Model.Value ?? throw new ConversionException("Windows can't run the face finder on this PC.");
        using var session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
        var longest = (int)Math.Max(picture.Width, picture.Height);
        var found = new List<Found>();
        // Each copy a third the size of the last takes in faces three times as big: close-ups and crowds alike.
        for (var size = Math.Min(longest, Largest); ; size /= 3)
        {
            found.AddRange(Look(session, picture, (double)size / longest));
            if (size / 3 < Smallest) break;
        }
        // The same face found at more than one size is kept once, as the box it was surest of.
        var kept = new List<Found>();
        foreach (var f in found.OrderByDescending(f => f.Score))
            if (kept.All(k => k.Overlap(f) < 0.3f)) kept.Add(f);
        return kept
            .Select(f => new RedactBox((int)Math.Floor(f.X1), (int)Math.Floor(f.Y1), (int)Math.Ceiling(f.X2 - f.X1), (int)Math.Ceiling(f.Y2 - f.Y1)).Grown(0.1, 0.05, 0.05))
            .Select(b => b.Within((int)picture.Width, (int)picture.Height))
            .OfType<RedactBox>()
            .ToList();
    }

    /// <summary>The faces in a copy of the picture at this scale, in the picture's own pixels.</summary>
    static List<Found> Look(LearningModelSession session, IMagickImage<byte> picture, double scale)
    {
        using var copy = picture.Clone();
        if (scale != 1)
            copy.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(picture.Width * scale)), (uint)Math.Max(1, Math.Round(picture.Height * scale))) { IgnoreAspectRatio = true });
        if (copy.ColorSpace == ColorSpace.CMYK) copy.TransformColorSpace(ColorProfiles.SRGB);
        if (copy.HasAlpha) Redaction.Flatten(copy);
        var (width, height) = ((int)copy.Width, (int)copy.Height);
        var (sx, sy) = ((float)picture.Width / width, (float)picture.Height / height);
        // The model takes blue, green and red planes of 0 to 255, its sides a multiple of 32: the rest is left black.
        int paddedWidth = (width + 31) / 32 * 32, paddedHeight = (height + 31) / 32 * 32, plane = paddedWidth * paddedHeight;
        var bgr = copy.ToByteArray(MagickFormat.Bgr);
        var input = new float[3 * plane];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 3; c++)
                    input[c * plane + y * paddedWidth + x] = bgr[(y * width + x) * 3 + c];
        var binding = new LearningModelBinding(session);
        binding.Bind("input", TensorFloat.CreateFromArray([1, 3, paddedHeight, paddedWidth], input));
        var result = session.Evaluate(binding, "faces");
        float[] Output(string name) => [.. ((TensorFloat)result.Outputs[name]).GetAsVectorView()];

        // For each cell of a grid at three strides: how likely a face is there, and its box, as in OpenCV's FaceDetectorYN.
        var found = new List<Found>();
        foreach (var stride in Strides)
        {
            var (cls, obj, box) = (Output($"cls_{stride}"), Output($"obj_{stride}"), Output($"bbox_{stride}"));
            int columns = paddedWidth / stride, rows = paddedHeight / stride;
            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                {
                    var i = row * columns + column;
                    var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                    if (score < Threshold) continue;
                    var cx = (column + box[i * 4]) * stride;
                    var cy = (row + box[i * 4 + 1]) * stride;
                    var w = MathF.Exp(box[i * 4 + 2]) * stride;
                    var h = MathF.Exp(box[i * 4 + 3]) * stride;
                    found.Add(new Found((cx - w / 2) * sx, (cy - h / 2) * sy, (cx + w / 2) * sx, (cy + h / 2) * sy, score));
                }
        }
        return found;
    }
}
