using Castor.AI.Worker.Strategies.Football;

namespace Castor.AI.Worker.Tests.Football;

/// <summary>Port of the Castor-AI FootballAnalyzer / FootballModule focus rule.</summary>
public class FootballFocusTests
{
    [Fact]
    public void Nothing_is_decided_until_every_camera_was_analysed()
    {
        var focus = new FootballFocus(["left", "right"]);

        focus.Report("left", true);

        Assert.Null(focus.Decide(0));
        Assert.Null(focus.Focus);
    }

    [Fact]
    public void Ball_on_a_single_camera_takes_the_focus()
    {
        var focus = new FootballFocus(["left", "right"]);
        focus.OnAir("left");

        focus.Report("left", false);
        focus.Report("right", true);

        Assert.Equal("right", focus.Decide(0));
        Assert.Equal("right", focus.Focus);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Ball_on_both_or_neither_keeps_the_previous_focus(bool left, bool right)
    {
        var focus = new FootballFocus(["left", "right"]);
        focus.OnAir("left");
        focus.Report("left", false);
        focus.Report("right", true);
        focus.Decide(0);
        focus.OnAir("right");

        focus.Report("left", left);
        focus.Report("right", right);

        Assert.Null(focus.Decide(0.1));
        Assert.Equal("right", focus.Focus);
    }

    [Fact]
    public void Ball_on_neither_before_any_focus_decides_nothing()
    {
        var focus = new FootballFocus(["left", "right"]);
        focus.Report("left", false);
        focus.Report("right", false);

        Assert.Null(focus.Decide(0));
        Assert.Null(focus.Focus);
    }

    [Fact]
    public void Focus_already_on_air_is_not_suggested()
    {
        var focus = new FootballFocus(["left", "right"]);
        focus.OnAir("right");
        focus.Report("left", false);
        focus.Report("right", true);

        Assert.Null(focus.Decide(0));
    }

    [Fact]
    public void Refused_focus_is_suggested_again_after_one_second()
    {
        var focus = new FootballFocus(["left", "right"]);
        focus.OnAir("left");
        focus.Report("left", false);
        focus.Report("right", true);

        Assert.Equal("right", focus.Decide(0));
        Assert.Null(focus.Decide(0.5)); // the app did not take it yet
        Assert.Equal("right", focus.Decide(FootballFocus.ResuggestSeconds + 0.01));
    }

    [Fact]
    public void Same_rule_with_three_cameras()
    {
        var focus = new FootballFocus(["a", "b", "c"]);
        focus.OnAir("a");
        focus.Report("a", false);
        focus.Report("b", false);
        focus.Report("c", true);
        Assert.Equal("c", focus.Decide(0));
        focus.OnAir("c");

        focus.Report("b", true); // b and c see it: keep c
        Assert.Null(focus.Decide(0.1));

        focus.Report("c", false); // only b
        Assert.Equal("b", focus.Decide(0.2));
    }
}
