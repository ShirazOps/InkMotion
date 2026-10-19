namespace InkMotion;

public static class AudioMixControls
{
    public static float[,] CreateMatrix(int sourceChannels, AudioChannelLayout layout, double balance)
    {
        if (!double.IsFinite(balance) || balance < -1 || balance > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(balance));
        }

        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(sourceChannels, layout);
        var speakers = AudioChannelLayouts.Get(layout).Speakers;
        for (int target = 0; target < speakers.Count; target++)
        {
            double gain = speakers[target] switch
            {
                AudioSpeaker.FrontLeft or AudioSpeaker.BackLeft or AudioSpeaker.SideLeft or
                    AudioSpeaker.TopFrontLeft or AudioSpeaker.TopBackLeft => 1 - Math.Max(0, balance),
                AudioSpeaker.FrontRight or AudioSpeaker.BackRight or AudioSpeaker.SideRight or
                    AudioSpeaker.TopFrontRight or AudioSpeaker.TopBackRight => 1 + Math.Min(0, balance),
                _ => 1,
            };
            for (int source = 0; source < sourceChannels; source++)
            {
                matrix[target, source] *= (float)gain;
            }
        }

        return matrix;
    }
}
