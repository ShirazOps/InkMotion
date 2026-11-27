using InkMotion;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioInputMonitoringTests
{
    [Fact]
    public void MonitorQueuePreservesPcmAndPadsUnderrunsWithoutReplayingOldInput()
    {
        var queue = new AudioMonitorBuffer(new WaveFormat(48000, 16, 2));
        byte[] pcm = [1, 2, 3, 4, 5, 6, 7, 8];
        queue.Add(pcm, pcm.Length);
        byte[] output = new byte[16];
        Assert.Equal(16, queue.Read(output, 0, output.Length));
        Assert.Equal(pcm, output[..8]);
        Assert.All(output[8..], sample => Assert.Equal(0, sample));
        queue.Read(output, 0, output.Length);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void MonitorQueueRejectsOverrunAtomicallyAndRejectsPartialFrames()
    {
        var queue = new AudioMonitorBuffer(new WaveFormat(48000, 16, 1));
        byte[] pcm = new byte[24000];
        pcm[0] = 123;
        queue.Add(pcm, pcm.Length);
        Assert.Throws<InvalidOperationException>(() => queue.Add(new byte[2], 2));
        byte[] output = new byte[pcm.Length];
        queue.Read(output, 0, output.Length);
        Assert.Equal(pcm, output);
        Assert.Throws<InvalidDataException>(() => queue.Add(new byte[1], 1));
    }
}
