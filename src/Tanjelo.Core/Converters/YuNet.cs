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

    /// <summary>A face found, in the pixels of the picture looked at, and how sure YuNet is of it.</summary>
    internal readonly record struct Found(float X1, float Y1, float X2, float Y2, float Score)
    {
        public float Area => (X2 - X1) * (Y2 - Y1);

        public float Overlap(Found o)
        {
            var w = Math.Min(X2, o.X2) - Math.Max(X1, o.X1);
            var h = Math.Min(Y2, o.Y2) - Math.Max(Y1, o.Y1);
            return w <= 0 || h <= 0 ? 0 : w * h / (Area + o.Area - w * h);
        }

        public Found Scaled(float sx, float sy) => new(X1 * sx, Y1 * sy, X2 * sx, Y2 * sy, Score);

        /// <summary>As a box to hide, a little wider to take in the hair.</summary>
        public RedactBox Box() =>
            new RedactBox((int)Math.Floor(X1), (int)Math.Floor(Y1), (int)Math.Ceiling(X2 - X1), (int)Math.Ceiling(Y2 - Y1)).Grown(0.1, 0.05, 0.05);
    }

    /// <summary>The faces in an upright picture, in its own pixels, each box a little wider to take in the hair.</summary>
    public static IReadOnlyList<RedactBox> Find(IMagickImage<byte> picture)
    {
        using var finder = new Finder();
        var longest = (int)Math.Max(picture.Width, picture.Height);
        var found = new List<Found>();
        // Each copy a third the size of the last takes in faces three times as big: close-ups and crowds alike.
        for (var size = Math.Min(longest, Largest); ; size /= 3)
        {
            var (bgr, width, height) = Pixels(picture, (double)size / longest);
            found.AddRange(finder.Look(bgr, width, height).Select(f => f.Scaled((float)picture.Width / width, (float)picture.Height / height)));
            if (size / 3 < Smallest) break;
        }
        return Kept(found).Select(f => f.Box().Within((int)picture.Width, (int)picture.Height)).OfType<RedactBox>().ToList();
    }

    /// <summary>Each face once, as the box YuNet was surest of: it finds most faces from neighbouring cells, and at more than one size.</summary>
    static List<Found> Kept(IEnumerable<Found> found)
    {
        var kept = new List<Found>();
        foreach (var f in found.OrderByDescending(f => f.Score))
            if (kept.All(k => k.Overlap(f) < 0.3f)) kept.Add(f);
        return kept;
    }

    /// <summary>A copy of the picture at this scale, as blue, green and red bytes.</summary>
    static (byte[] Bgr, int Width, int Height) Pixels(IMagickImage<byte> picture, double scale)
    {
        using var copy = picture.Clone();
        if (scale != 1)
            copy.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(picture.Width * scale)), (uint)Math.Max(1, Math.Round(picture.Height * scale))) { IgnoreAspectRatio = true });
        if (copy.ColorSpace == ColorSpace.CMYK) copy.TransformColorSpace(ColorProfiles.SRGB);
        if (copy.HasAlpha) Redaction.Flatten(copy);
        return (copy.ToByteArray(MagickFormat.Bgr), (int)copy.Width, (int)copy.Height);
    }

    /// <summary>YuNet set up once, to look at one picture after another, such as the frames of a video.</summary>
    internal sealed class Finder : IDisposable
    {
        readonly LearningModelSession _session = new(Model.Value ?? throw new ConversionException(Strings.WindowsCanTRunTheFaceFinder),
            new LearningModelDevice(LearningModelDeviceKind.Cpu));

        /// <summary>The faces in a picture of blue, green and red bytes, each once, in its pixels.</summary>
        public List<Found> Look(byte[] bgr, int width, int height) => Look(bgr, width, height, new RedactBox(0, 0, width, height));

        /// <summary>The faces in an area of a picture of blue, green and red bytes, each once, in the picture's pixels.</summary>
        public List<Found> Look(byte[] bgr, int width, int height, RedactBox area)
        {
            // The model takes blue, green and red planes of 0 to 255, its sides a multiple of 32: the rest is left black.
            int paddedWidth = (area.Width + 31) / 32 * 32, paddedHeight = (area.Height + 31) / 32 * 32, plane = paddedWidth * paddedHeight;
            var input = new float[3 * plane];
            for (var y = 0; y < area.Height; y++)
            {
                var from = ((area.Y + y) * width + area.X) * 3;
                for (int x = 0, to = y * paddedWidth; x < area.Width; x++, to++, from += 3)
                {
                    input[to] = bgr[from];
                    input[plane + to] = bgr[from + 1];
                    input[2 * plane + to] = bgr[from + 2];
                }
            }
            var binding = new LearningModelBinding(_session);
            binding.Bind("input", TensorFloat.CreateFromArray([1, 3, paddedHeight, paddedWidth], input));
            var result = _session.Evaluate(binding, "faces");
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
                        var cx = (column + box[i * 4]) * stride + area.X;
                        var cy = (row + box[i * 4 + 1]) * stride + area.Y;
                        var w = MathF.Exp(box[i * 4 + 2]) * stride;
                        var h = MathF.Exp(box[i * 4 + 3]) * stride;
                        found.Add(new Found(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, score));
                    }
            }
            return Kept(found);
        }

        public void Dispose() => _session.Dispose();
    }
}
