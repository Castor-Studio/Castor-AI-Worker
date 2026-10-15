using Castor.AI.Worker.Strategies.Football;

namespace Castor.AI.Worker.Tests.Football;

/// <summary>Decoding of a hand-made YOLO output ([84, anchors]: cx, cy, w, h, 80 class scores).</summary>
public class BallDecodeTests
{
    private const int Anchors = 3;
    private const int Classes = 80;
    private const int Person = 0;

    private static readonly BallDetectorOptions Options = new();

    [Fact]
    public void Ball_above_threshold_is_found_with_its_box_in_frame_pixels()
    {
        var output = Output();
        SetBox(output, anchor: 1, cx: 100, cy: 50, w: 20, h: 10);
        output[Score(BallDetector.BallClassId, 1)] = 0.8f;

        var detection = BallDetector.Decode(output, Anchors, Options, scaleX: 2, scaleY: 1);

        Assert.True(detection.Found);
        Assert.Equal(0.8f, detection.Confidence);
        Assert.Equal(new BallBox(180, 45, 220, 55), detection.Box);
    }

    [Fact]
    public void Ball_kept_by_ultralytics_but_below_ball_confidence_is_not_found()
    {
        var output = Output();
        output[Score(BallDetector.BallClassId, 0)] = 0.5f; // > PREDICTION_CONFIDENCE, < BALL_CONFIDENCE

        var detection = BallDetector.Decode(output, Anchors, Options, 1, 1);

        Assert.False(detection.Found);
        Assert.Equal(0.5f, detection.Confidence);
    }

    [Fact]
    public void Box_labelled_with_another_class_is_not_a_ball()
    {
        var output = Output();
        output[Score(BallDetector.BallClassId, 2)] = 0.7f;
        output[Score(Person, 2)] = 0.9f; // ultralytics labels this box "person"

        var detection = BallDetector.Decode(output, Anchors, Options, 1, 1);

        Assert.False(detection.Found);
        Assert.Null(detection.Box);
    }

    [Fact]
    public void Best_ball_wins()
    {
        var output = Output();
        output[Score(BallDetector.BallClassId, 0)] = 0.65f;
        SetBox(output, anchor: 2, cx: 10, cy: 10, w: 2, h: 2);
        output[Score(BallDetector.BallClassId, 2)] = 0.9f;

        var detection = BallDetector.Decode(output, Anchors, Options, 1, 1);

        Assert.Equal(0.9f, detection.Confidence);
        Assert.Equal(new BallBox(9, 9, 11, 11), detection.Box);
    }

    private static float[] Output() => new float[(4 + Classes) * Anchors];

    private static int Score(int cls, int anchor) => (4 + cls) * Anchors + anchor;

    private static void SetBox(float[] output, int anchor, float cx, float cy, float w, float h)
    {
        output[anchor] = cx;
        output[Anchors + anchor] = cy;
        output[2 * Anchors + anchor] = w;
        output[3 * Anchors + anchor] = h;
    }
}
