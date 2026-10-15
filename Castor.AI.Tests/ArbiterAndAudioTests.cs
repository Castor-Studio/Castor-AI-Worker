using Castor.AI.Arbitration;
using Castor.AI.Audio;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Tests;

public class DecisionArbiterTests
{
    [Fact]
    public void Min_hold_of_the_accepted_shot_blocks_the_next_cut()
    {
        var arbiter = new DecisionArbiter(new ArbiterOptions());
        arbiter.Reset("wide", 0);

        Assert.True(arbiter.Evaluate(Decision("host", 1_000_000, minHoldMs: 2000), 1_000_000).Accepted);
        var early = arbiter.Evaluate(Decision("guest", 2_000_000), 2_000_000);
        var late = arbiter.Evaluate(Decision("guest", 3_100_000), 3_100_000);

        Assert.False(early.Accepted);
        Assert.StartsWith("Current shot held", early.Reason);
        Assert.True(late.Accepted);
    }

    [Fact]
    public void Default_min_hold_applies_when_decision_has_none()
    {
        var arbiter = new DecisionArbiter(new ArbiterOptions { DefaultMinHoldMs = 1500 });
        arbiter.Reset("wide", 0);

        arbiter.Evaluate(Decision("host", 0), 0);

        Assert.False(arbiter.Evaluate(Decision("guest", 1_000_000), 1_000_000).Accepted);
        Assert.True(arbiter.Evaluate(Decision("guest", 1_600_000), 1_600_000).Accepted);
    }

    [Fact]
    public void Same_camera_is_not_a_cut()
    {
        var arbiter = new DecisionArbiter(new ArbiterOptions());
        arbiter.Reset("wide", 0);

        Assert.Equal("Already on air", arbiter.Evaluate(Decision("wide", 0), 0).Reason);
    }

    private static SceneDecision Decision(string camera, long ptsUs, uint minHoldMs = 0) =>
        new() { CameraId = camera, SourcePtsUs = ptsUs, Confidence = 0.9f, MinHoldMs = minHoldMs };
}

public class AudioFeedTests
{
    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(16_000)]
    [InlineData(8_000)]
    public void Resampler_outputs_16k(int inputRate)
    {
        var resampler = new LinearResampler(inputRate, 16_000);
        var output = new Queue<float>();

        for (var i = 0; i < 10; i++)
        {
            resampler.Process(new float[inputRate / 10], output); // 10 x 100 ms
        }

        Assert.InRange(output.Count, 16_000 - 2, 16_000 + 2);
    }

    [Fact]
    public void Resampler_preserves_a_ramp()
    {
        var resampler = new LinearResampler(48_000, 16_000);
        var output = new Queue<float>();
        var ramp = Enumerable.Range(0, 300).Select(i => (float)i).ToArray();

        resampler.Process(ramp, output);

        var values = output.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(i * 3f, values[i], 3);
        }
    }

    [Fact]
    public void Block_interleaves_channels_and_fills_silence()
    {
        var feed = new AudioFeed(3, _ => { });
        feed.Write(0, Enumerable.Repeat(0.5f, AudioFeed.BlockFrames).ToArray(), 16_000);
        feed.Write(2, Enumerable.Repeat(-0.25f, 100).ToArray(), 16_000); // underrun after 100 frames

        var block = new float[AudioFeed.BlockFrames * 3];
        feed.FillBlock(block, new float[AudioFeed.BlockFrames]);

        Assert.Equal(0.5f, block[0]);
        Assert.Equal(0f, block[1]);
        Assert.Equal(-0.25f, block[2]);
        Assert.Equal(0f, block[100 * 3 + 2]);
        Assert.Equal(0.5f, block[511 * 3]);
    }

    [Fact]
    public void Writes_to_unknown_channels_are_ignored()
    {
        var feed = new AudioFeed(1, _ => { });

        feed.Write(5, new float[10], 16_000);
        feed.Write(-1, new float[10], 16_000);
    }
}
