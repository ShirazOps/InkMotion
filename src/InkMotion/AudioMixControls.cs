namespace InkMotion;

public static class AudioMixControls
{
    public static float[,] CreateMatrix(
        int sourceChannels, AudioChannelLayout layout, double balance, bool spatialPanEnabled = false, double spatialPanDegrees = 0)
    {
        if (!double.IsFinite(balance) || balance < -1 || balance > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(balance));
        }

        if (spatialPanEnabled)
        {
            if (sourceChannels != 1)
            {
                throw new ArgumentException("Spatial azimuth panning is supported for mono sources only.", nameof(sourceChannels));
            }

            return AudioChannelLayouts.CreateSpatialPanMatrix(layout, spatialPanDegrees);
        }

        float[,] matrix = AudioChannelLayouts.CreateMixMatrix(sourceChannels, layout);
        var speakers = AudioChannelLayouts.Get(layout).Speakers;
        for (int target = 0; target < speakers.Count; target++)
        {
            double gain = GetBalanceGain(speakers[target], balance);
            for (int source = 0; source < sourceChannels; source++)
            {
                matrix[target, source] *= (float)gain;
            }
        }

        return matrix;
    }

    public static double GetBalanceGain(AudioSpeaker speaker, double balance) =>
        speaker switch
        {
            AudioSpeaker.FrontLeft or AudioSpeaker.BackLeft or AudioSpeaker.SideLeft or
                AudioSpeaker.TopFrontLeft or AudioSpeaker.TopBackLeft => 1 - Math.Max(0, balance),
            AudioSpeaker.FrontRight or AudioSpeaker.BackRight or AudioSpeaker.SideRight or
                AudioSpeaker.TopFrontRight or AudioSpeaker.TopBackRight => 1 + Math.Min(0, balance),
            _ => 1,
        };
}
