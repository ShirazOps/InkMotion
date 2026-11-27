using Xunit;

namespace InkMotion.Tests;

public sealed class TimelineScrubTests
{
    [Fact]
    public void PointerBursts_SeekOnlyLatestFrameAndFlushFinalPosition()
    {
        var requests = new TimelineScrubRequests();
        var delivered = new List<int>();
        for (int frame = 0; frame < 240; frame++)
        {
            requests.Request(frame);
            if (frame % 8 == 7 && requests.TryTake(out int latest))
                delivered.Add(latest);
        }
        Assert.Equal(30, delivered.Count);
        Assert.Equal(Enumerable.Range(1, 30).Select(index => index * 8 - 1), delivered);
        requests.Request(400);
        requests.Request(17);
        Assert.True(requests.TryTake(out int final));
        Assert.Equal(17, final);
        Assert.False(requests.TryTake(out _));
    }

    [Fact]
    public void SameFrame_IsNotResoughtAndResetDiscardsOldGesture()
    {
        var requests = new TimelineScrubRequests();
        requests.Request(24);
        Assert.True(requests.TryTake(out _));
        requests.Request(24);
        Assert.False(requests.TryTake(out _));
        requests.Request(90);
        requests.Reset();
        Assert.False(requests.TryTake(out _));
        requests.Request(24);
        Assert.True(requests.TryTake(out int first));
        Assert.Equal(24, first);
    }
}
