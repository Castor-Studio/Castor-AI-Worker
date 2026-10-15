using Castor.AI.Worker.Models;
using Castor.AI.Worker.Tests.Models;
using Castor.AI.Worker.Strategies.Football;

namespace Castor.AI.Worker.Tests.Football;

/// <summary>
/// Runs the exported model and compares it with the Python detector on the same frames.
/// The model is downloaded on first run, like the worker does.
/// </summary>
public sealed class BallDetectorParityTests : IDisposable
{
    // Same pixels on 99.95 % of the input; PyTorch and ONNX Runtime float32 convolutions round differently.
    private const float Tolerance = 0.005f;

    // Python runs the model in half precision on CUDA.
    private const float HalfPrecisionTolerance = 0.01f;

    private readonly YoloBallModel _model = new(TestModels.Path(ModelCatalog.FootballBall));

    [Theory]
    [InlineData("pitch_ball", false, "python_fp32")]
    [InlineData("pitch_empty", false, "python_fp32")]
    [InlineData("zidane", false, "python_fp32")]
    [InlineData("pitch_ball", true, "rgb_fp32")]
    [InlineData("zidane", true, "rgb_fp32")]
    public void Kept_classes_and_scores_match_python(string frame, bool swapRedBlue, string variant)
    {
        var expected = Fixtures.BestPerClass(frame, variant);

        var actual = KeptClasses(frame, swapRedBlue);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (cls, confidence) in expected)
        {
            Assert.True(Math.Abs(confidence - actual[cls]) <= Tolerance,
                $"class {cls}: python {confidence:0.0000}, C# {actual[cls]:0.0000}");
        }
    }

    [Fact]
    public void Ball_is_found_like_python_with_default_thresholds()
    {
        var detector = new BallDetector(_model, new BallDetectorOptions());

        var ball = Detect(detector, "pitch_ball");
        var empty = Detect(detector, "pitch_empty");
        var people = Detect(detector, "zidane");

        // Python (CUDA, half): ball 0.6758 >= BALL_CONFIDENCE 0.60.
        Assert.True(ball.Found);
        Assert.Equal(Fixtures.BestPerClass("pitch_ball", "python")[BallDetector.BallClassId], ball.Confidence, HalfPrecisionTolerance);
        var box = ball.Box!.Value;
        Assert.InRange((box.X1 + box.X2) / 2, 150, 170); // ball drawn at (160, 220), radius 13
        Assert.InRange((box.Y1 + box.Y2) / 2, 210, 230);
        Assert.False(empty.Found);
        Assert.False(people.Found);
    }

    public void Dispose() => _model.Dispose();

    private BallDetection Detect(BallDetector detector, string frame) =>
        detector.Detect(Fixtures.Bgra(frame), Fixtures.Width, Fixtures.Height, Fixtures.Stride);

    /// <summary>Ultralytics kept boxes, best score per class: best class of the anchor above <c>conf</c> 0.25.</summary>
    private Dictionary<int, float> KeptClasses(string frame, bool swapRedBlue)
    {
        var size = _model.InputSize;
        var input = new float[3 * size * size];
        BallDetector.Preprocess(Fixtures.Bgra(frame), Fixtures.Width, Fixtures.Height, Fixtures.Stride, size, swapRedBlue, input);
        var output = _model.Run(input, out var anchors);
        var classes = output.Length / anchors - 4;

        var kept = new Dictionary<int, float>();
        for (var anchor = 0; anchor < anchors; anchor++)
        {
            var best = 0;
            for (var c = 1; c < classes; c++)
            {
                if (output[(4 + c) * anchors + anchor] > output[(4 + best) * anchors + anchor])
                {
                    best = c;
                }
            }

            var score = output[(4 + best) * anchors + anchor];
            if (score > new BallDetectorOptions().PredictionConfidence)
            {
                kept[best] = Math.Max(kept.GetValueOrDefault(best), score);
            }
        }

        return kept;
    }
}
