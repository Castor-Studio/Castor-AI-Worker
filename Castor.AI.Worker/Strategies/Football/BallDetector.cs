using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Castor.AI.Worker.Models;

namespace Castor.AI.Worker.Strategies.Football;

/// <summary>Result of one frame. <see cref="Confidence"/> is the best ball score, even below the threshold.</summary>
public readonly record struct BallDetection(bool Found, float Confidence, BallBox? Box);

/// <summary>Ball box in input frame pixels.</summary>
public readonly record struct BallBox(float X1, float Y1, float X2, float Y2);

/// <summary>Raw model output for one frame: [4 + classes, anchors], rows cx, cy, w, h then class scores.</summary>
public interface IBallModel
{
    int InputSize { get; }

    /// <param name="input">CHW float in [0, 1], <see cref="InputSize"/> x <see cref="InputSize"/>.</param>
    float[] Run(float[] input, out int anchors);
}

/// <summary>
/// Port of <c>castostudio_ai_football.detector.BallDetector</c>: YOLO (COCO, Castor-AI
/// <c>checkpoint_5.pt</c> exported to ONNX) on the frame stretched to 416x416, "sports ball" class.
/// Thread-safe: the ONNX session is shared, buffers are per call.
/// </summary>
public sealed class BallDetector(IBallModel model, BallDetectorOptions options)
{
    /// <summary>COCO "sports ball" (<c>FOOT_BALL_CLASS_ID</c>).</summary>
    public const int BallClassId = 32;

    public BallDetectorOptions Options { get; } = options;

    /// <summary>
    /// Same decision as Python: the ball is found when one kept detection is a ball scoring at least
    /// <see cref="BallDetectorOptions.BallConfidence"/>. Ultralytics keeps a box when its best class scores at least
    /// <see cref="BallDetectorOptions.PredictionConfidence"/> and labels it with that class; NMS never removes the
    /// best box of a class, so "found" does not depend on NMS.
    /// </summary>
    public BallDetection Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var size = model.InputSize;
        var input = new float[3 * size * size];
        Preprocess(bgra, width, height, stride, size, Options.SwapRedBlue, input);
        var output = model.Run(input, out var anchors);
        return Decode(output, anchors, Options, (float)width / size, (float)height / size);
    }

    /// <summary>
    /// Resize to size x size like OpenCV <c>cv2.resize(INTER_LINEAR)</c> on 8-bit images (fixed-point weights,
    /// rounded to uint8 as Python does), then scaled to [0, 1], CHW.
    /// <paramref name="swapRedBlue"/> false keeps B, G, R plane order, true gives R, G, B.
    /// </summary>
    public static void Preprocess(ReadOnlySpan<byte> bgra, int width, int height, int stride, int size, bool swapRedBlue, float[] chw)
    {
        var plane = size * size;
        var first = swapRedBlue ? 2 : 0; // byte offset of the first plane in a BGRA pixel
        var last = 2 - first;

        Span<int> x0 = stackalloc int[size];
        Span<int> x1 = stackalloc int[size];
        Span<Weights> wx = stackalloc Weights[size];
        for (var x = 0; x < size; x++)
        {
            Sample(x, (double)width / size, width, out x0[x], out x1[x], out wx[x]);
        }

        for (var y = 0; y < size; y++)
        {
            Sample(y, (double)height / size, height, out var y0, out var y1, out var wy);
            var row0 = bgra.Slice(y0 * stride);
            var row1 = bgra.Slice(y1 * stride);
            for (var x = 0; x < size; x++)
            {
                var a = x0[x] * 4;
                var b = x1[x] * 4;
                var index = y * size + x;
                chw[index] = Lerp(row0, row1, a, b, first, wx[x], wy);
                chw[plane + index] = Lerp(row0, row1, a, b, 1, wx[x], wy);
                chw[2 * plane + index] = Lerp(row0, row1, a, b, last, wx[x], wy);
            }
        }
    }

    public static BallDetection Decode(float[] output, int anchors, BallDetectorOptions options, float scaleX, float scaleY)
    {
        var classes = output.Length / anchors - 4;
        if (classes <= BallClassId)
        {
            throw new InvalidDataException($"Model has {classes} classes, the ball class is {BallClassId}");
        }

        var ballRow = (4 + BallClassId) * anchors;
        var best = -1;
        var bestScore = 0f;
        var bestAny = 0f;
        for (var anchor = 0; anchor < anchors; anchor++)
        {
            var ball = output[ballRow + anchor];
            bestAny = Math.Max(bestAny, ball);
            if (ball <= options.PredictionConfidence || ball <= bestScore)
            {
                continue;
            }

            // Ultralytics labels a box with its best class: a ball must beat every other class.
            var isBest = true;
            for (var c = 0; c < classes; c++)
            {
                if (c != BallClassId && output[(4 + c) * anchors + anchor] > ball)
                {
                    isBest = false;
                    break;
                }
            }

            if (isBest)
            {
                best = anchor;
                bestScore = ball;
            }
        }

        if (best < 0)
        {
            return new BallDetection(false, bestAny, null);
        }

        var cx = output[best];
        var cy = output[anchors + best];
        var w = output[2 * anchors + best];
        var h = output[3 * anchors + best];
        var box = new BallBox((cx - w / 2) * scaleX, (cy - h / 2) * scaleY, (cx + w / 2) * scaleX, (cy + h / 2) * scaleY);
        return new BallDetection(bestScore >= options.BallConfidence, bestScore, box);
    }

    // OpenCV INTER_LINEAR on 8-bit images: 11-bit fixed-point weights (INTER_RESIZE_COEF_BITS).
    private const int WeightOne = 2048;

    /// <summary>
    /// Vertical pass of OpenCV <c>VResizeLinearVec_32s8u</c> (the SIMD path cv2 takes on x86):
    /// <c>((b0 * (r0 &gt;&gt; 4)) &gt;&gt; 16 + (b1 * (r1 &gt;&gt; 4)) &gt;&gt; 16 + 2) &gt;&gt; 2</c>.
    /// Differs from the scalar formula by 1 on some pixels; this one matches cv2 on 99.95 % of them.
    /// </summary>
    private static float Lerp(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, int a, int b, int channel, Weights wx, Weights wy)
    {
        var top = row0[a + channel] * wx.Low + row0[b + channel] * wx.High;
        var bottom = row1[a + channel] * wx.Low + row1[b + channel] * wx.High;
        var value = (((top >> 4) * wy.Low >> 16) + ((bottom >> 4) * wy.High >> 16) + 2) >> 2;
        return value / 255f;
    }

    /// <summary>Source pixels and weights for destination pixel <paramref name="index"/> (OpenCV <c>resize</c> setup).</summary>
    private static void Sample(int index, double scale, int size, out int low, out int high, out Weights weights)
    {
        var position = (float)((index + 0.5) * scale - 0.5);
        low = (int)MathF.Floor(position);
        var fraction = position - low;
        if (low < 0)
        {
            low = 0;
            fraction = 0;
        }

        if (low >= size - 1)
        {
            low = size - 1;
            fraction = 0;
        }

        high = Math.Min(low + 1, size - 1);
        weights = new Weights(
            (int)MathF.Round((1 - fraction) * WeightOne, MidpointRounding.ToEven),
            (int)MathF.Round(fraction * WeightOne, MidpointRounding.ToEven));
    }

    private readonly record struct Weights(int Low, int High);
}

/// <summary>The exported YOLO model, loaded once per worker (<see cref="Models.ModelStore"/>).</summary>
public sealed class YoloBallModel : IBallModel, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int[] _inputShape;
    private readonly Lock _runGate = new();

    public YoloBallModel(string modelPath, OnnxSessionFactory? sessions = null)
    {
        (_session, ExecutionProvider) = (sessions ?? new OnnxSessionFactory()).Create(modelPath, session =>
        {
            var metadata = session.InputMetadata.Single();
            var shape = metadata.Value.Dimensions;
            if (shape is not [1, 3, var h, var w] || h != w || h <= 0)
                throw new InvalidDataException($"Expected a static [1, 3, N, N] input in {modelPath}");
            var input = NamedOnnxValue.CreateFromTensor(metadata.Key, new DenseTensor<float>(new float[3 * h * w], shape));
            using var results = session.Run([input]);
        });
        ModelPath = modelPath;

        var input = _session.InputMetadata.Single();
        _inputName = input.Key;
        // The warmup above validated the static square input before retaining the session.
        _inputShape = input.Value.Dimensions;
        InputSize = _inputShape[2];
    }

    public string ModelPath { get; }

    public string ExecutionProvider { get; }

    public int InputSize { get; }

    public float[] Run(float[] input, out int anchors)
    {
        // DirectML forbids concurrent Run calls on a shared session.
        lock (_runGate)
        {
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(input, _inputShape)) };
            using var results = _session.Run(inputs);
            var tensor = results[0].AsTensor<float>();
            anchors = tensor.Dimensions[2];
            return tensor.ToArray();
        }
    }

    public void Dispose() => _session.Dispose();
}
