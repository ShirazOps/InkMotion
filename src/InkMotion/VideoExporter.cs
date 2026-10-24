using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace InkMotion;

/// <param name="InkReferenceHeight">Height in DIPs of the ink canvas the strokes were drawn on, used to scale brushes.</param>
public sealed record VideoExportRequest(
    EditorProject Project,
    VideoExportProfile Profile,
    VideoExportCodec Codec,
    string OutputPath,
    double InkReferenceHeight,
    bool PreferHardware = true,
    double Quality = 0.5,
    int AudioSampleRate = ExportPlanning.AudioSampleRate,
    int AudioBitRate = 192000);

public sealed record VideoExportProgress(int Frame, int FrameCount, string Stage)
{
    public double Fraction => FrameCount <= 0 ? 0 : Math.Clamp((double)Frame / FrameCount, 0, 1);
}

public sealed record VideoExportResult(
    string OutputPath,
    string? SurroundStemPath,
    int FrameCount,
    double DurationSeconds,
    bool HardwareTransformsEnabled,
    IReadOnlyList<string> Warnings,
    int AudioChannels = 0);

/// <summary>
/// Renders the project frame by frame on the GPU with Win2D and encodes it to MP4 with Media Foundation.
/// Video, image and title tracks composite bottom track first, then visible ink layers on top.
/// </summary>
public static class VideoExporter
{
    private const int AudioChunkFrames = 4096;

    public static double GetDurationSeconds(EditorProject project, double audioDurationSeconds) => Math.Max(
        Math.Max(VideoTimelineEditing.GetTimelineEndSeconds(project.VideoTracks, project.FrameRate), audioDurationSeconds),
        ExportPlanning.GetInkDurationSeconds(project));

    public static VideoExportResult Export(
        VideoExportRequest request,
        IProgress<VideoExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EditorProject project = request.Project;
        ExportPlanning.ValidateEmbeddedAudio(request.AudioSampleRate, request.AudioBitRate);
        ExportPlanning.ValidateOutputPath(project, request.OutputPath);
        VideoExportProfile profile = request.Profile;
        var warnings = new List<string>();
        string? stemPath = null;
        bool completed = false;

        progress?.Report(new VideoExportProgress(0, 0, "Preparing audio"));
        using var mixer = new ExportAudioMixer(project, request.AudioSampleRate);
        warnings.AddRange(mixer.Warnings);
        double duration = GetDurationSeconds(project, mixer.DurationSeconds);
        int frameCount = ExportPlanning.GetFrameCount(duration, profile.FrameRate);
        if (frameCount == 0)
        {
            throw new InvalidOperationException("Nothing to export: add video, images, titles, audio or ink first.");
        }

        AudioChannelLayout encodedLayout = ExportPlanning.GetEncodedAudioLayout(mixer.Layout);
        AudioChannelLayoutInfo encodedInfo = AudioChannelLayouts.Get(encodedLayout);
        bool hasAudio = mixer.SourceCount > 0;
        float[,] downmix = AudioChannelLayouts.CreateMixMatrix(mixer.Layout, encodedLayout);

        MediaFoundationVideoWriter? writer = null;
        MultichannelWaveWriter? stem = null;
        bool hardwareTransformsEnabled = false;
        if (request.PreferHardware)
        {
            warnings.Add("Media Foundation hardware transforms were enabled by preference; this API does not confirm whether the video encoder used hardware.");
        }
        try
        {
            try
            {
                writer = CreateWriter(request, frameCount, hasAudio ? encodedInfo : null, warnings, out hardwareTransformsEnabled);
            }
            catch (AudioEncoderUnavailableException) when (encodedInfo.ChannelCount > 2)
            {
                warnings.Add($"Windows could not encode {encodedInfo.DisplayName} AAC on this PC, so the MP4 carries a stereo downmix.");
                encodedLayout = AudioChannelLayout.Stereo;
                encodedInfo = AudioChannelLayouts.Get(encodedLayout);
                downmix = AudioChannelLayouts.CreateMixMatrix(mixer.Layout, encodedLayout);
                writer = CreateWriter(request, frameCount, encodedInfo, warnings, out hardwareTransformsEnabled);
            }

            AudioChannelLayoutInfo mixInfo = AudioChannelLayouts.Get(mixer.Layout);
            if (hasAudio && mixInfo.ChannelCount > 2 && mixInfo.ChannelCount > encodedInfo.ChannelCount)
            {
                stemPath = ExportPlanning.GetSurroundStemPath(request.OutputPath);
                stem = new MultichannelWaveWriter(stemPath, request.AudioSampleRate, mixInfo.ChannelCount, mixInfo.ChannelMask);
                warnings.Add($"MP4 audio carries a {encodedInfo.DisplayName} downmix; the full {mixInfo.DisplayName} mix is in {Path.GetFileName(stemPath)}.");
            }

            using var device = new CanvasDevice();
            using var renderer = new FrameRenderer(device, request, warnings);
            var audio = new AudioPump(mixer, writer, stem, downmix, encodedInfo.ChannelCount);
            for (int frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] pixels = renderer.Render(frame);
                long time = ExportPlanning.GetFrameTime(frame, profile.FrameRate);
                writer.WriteVideoFrame(pixels, time, ExportPlanning.GetFrameTime(frame + 1, profile.FrameRate) - time);
                if (hasAudio)
                {
                    audio.WriteThrough(ExportPlanning.GetAudioFramesThrough(frame, profile.FrameRate, request.AudioSampleRate));
                }

                if (frame % 5 == 0 || frame == frameCount - 1)
                {
                    progress?.Report(new VideoExportProgress(frame + 1, frameCount, "Encoding"));
                }
            }

            progress?.Report(new VideoExportProgress(frameCount, frameCount, "Finalising"));
            writer.Finish();
            if (stem?.Truncated == true)
            {
                warnings.Add("The surround mix reached the 4 GB WAV limit and was cut short.");
            }

            completed = true;
            return new VideoExportResult(
                request.OutputPath,
                stemPath,
                frameCount,
                duration,
                hardwareTransformsEnabled,
                warnings,
                hasAudio ? encodedInfo.ChannelCount : 0);
        }
        finally
        {
            writer?.Dispose();
            stem?.Dispose();
            if (!completed)
            {
                TryDelete(request.OutputPath);
                if (stemPath is not null)
                {
                    TryDelete(stemPath);
                }
            }
        }
    }

    private static MediaFoundationVideoWriter CreateWriter(
        VideoExportRequest request,
        int frameCount,
        AudioChannelLayoutInfo? audio,
        List<string> warnings,
        out bool hardwareTransformsEnabled)
    {
        VideoExportProfile profile = request.Profile;
        int bitRate = ExportPlanning.GetVideoBitRate(profile, request.Codec, request.Quality);
        MediaFoundationVideoWriter Create(bool enableHardwareTransforms) => new(
            request.OutputPath,
            profile.Width,
            profile.Height,
            profile.FrameRate,
            request.Codec,
            bitRate,
            audio?.ChannelCount ?? 0,
            audio?.ChannelMask ?? 0,
            enableHardwareTransforms,
            request.AudioSampleRate,
            request.AudioBitRate);

        if (request.PreferHardware)
        {
            try
            {
                hardwareTransformsEnabled = true;
                return Create(true);
            }
            catch (COMException)
            {
                TryDelete(request.OutputPath);
                warnings.Add("Media Foundation could not start with hardware transforms enabled; the export retried with the hardware-transform preference disabled.");
            }
        }

        hardwareTransformsEnabled = false;
        try
        {
            return Create(false);
        }
        catch (COMException ex)
        {
            TryDelete(request.OutputPath);
            string hint = request.Codec == VideoExportCodec.Hevc
                ? " HEVC needs the HEVC Video Extensions from the Microsoft Store; H.264 works on every Windows 11 PC."
                : string.Empty;
            throw new InvalidOperationException(
                $"Windows could not start the {ExportPlanning.CodecName(request.Codec)} encoder (0x{ex.HResult:X8}).{hint}",
                ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Feeds the mix to the encoder in step with the video, downmixing to the encodable layout.</summary>
    private sealed class AudioPump(
        ExportAudioMixer mixer,
        MediaFoundationVideoWriter writer,
        MultichannelWaveWriter? stem,
        float[,] downmix,
        int outputChannels)
    {
        private readonly float[] _mix = new float[AudioChunkFrames * mixer.Channels];
        private readonly byte[] _pcm = new byte[AudioChunkFrames * outputChannels * 2];
        private long _written;

        public void WriteThrough(long targetFrames)
        {
            int inputChannels = mixer.Channels;
            while (_written < targetFrames)
            {
                int frames = (int)Math.Min(AudioChunkFrames, targetFrames - _written);
                int read = mixer.Read(_mix, 0, frames * inputChannels);
                Array.Clear(_mix, read, (frames * inputChannels) - read);
                stem?.Write(_mix, frames * inputChannels);
                for (int frame = 0; frame < frames; frame++)
                {
                    int input = frame * inputChannels;
                    for (int output = 0; output < outputChannels; output++)
                    {
                        float value = 0;
                        for (int channel = 0; channel < inputChannels; channel++)
                        {
                            value += downmix[output, channel] * _mix[input + channel];
                        }

                        short sample = (short)Math.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);
                        int target = ((frame * outputChannels) + output) * 2;
                        _pcm[target] = (byte)sample;
                        _pcm[target + 1] = (byte)(sample >> 8);
                    }
                }

                long time = _written * 10_000_000L / mixer.SampleRate;
                long end = (_written + frames) * 10_000_000L / mixer.SampleRate;
                writer.WriteAudio(_pcm, frames * outputChannels * 2, time, end - time);
                _written += frames;
            }
        }
    }

    /// <summary>Owns the GPU render target, the decoders for clips on screen and cached still images.</summary>
    private sealed class FrameRenderer : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly EditorProject _project;
        private readonly VideoExportProfile _profile;
        private readonly List<string> _warnings;
        private readonly CanvasRenderTarget _target;
        private readonly float _inkScale;
        private readonly bool _hasInk;
        private readonly Dictionary<string, CanvasBitmap?> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TimelineVideoFrameSource?> _videos = [];
        private readonly Dictionary<string, VideoColourGradingEffect> _colourGradingEffects = [];
        private readonly Dictionary<string, VideoChromaKeyEffect> _chromaKeyEffects = [];
        private readonly Dictionary<string, VideoBlurCanvasEffect> _blurEffects = [];
        private readonly Dictionary<string, VideoSharpenCanvasEffect> _sharpenEffects = [];
        private readonly Dictionary<string, VideoVignetteCanvasEffect> _vignetteEffects = [];
        private readonly HashSet<string> _missingProxyWarnings = new(StringComparer.Ordinal);

        public FrameRenderer(CanvasDevice device, VideoExportRequest request, List<string> warnings)
        {
            _device = device;
            _project = request.Project;
            _profile = request.Profile;
            _warnings = warnings;
            _target = new CanvasRenderTarget(device, _profile.Width, _profile.Height, 96);
            double reference = request.InkReferenceHeight > 0 ? request.InkReferenceHeight : 720;
            _inkScale = (float)(_profile.Height / reference);
            _hasInk = ExportPlanning.HasInk(_project);
        }

        public byte[] Render(int frame)
        {
            double seconds = frame / _profile.FrameRate;
            float width = _profile.Width;
            float height = _profile.Height;
            IReadOnlyList<TimelineClipLocation> layers = VideoTimelineEditing.GetVisibleLayers(
                _project.VideoTracks,
                seconds,
                _project.FrameRate,
                _project.VideoMulticamGroups);
            ReleaseFinishedVideos(layers);
            using (CanvasDrawingSession session = _target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Black);
                foreach (TimelineClipLocation layer in layers)
                {
                    float opacity = (float)VideoTimelineEditing.GetOpacity(layer, seconds, _project.FrameRate);
                    if (opacity <= 0)
                    {
                        continue;
                    }

                    switch (layer.Track.Kind)
                    {
                        case TimelineTrackKind.Video:
                            DrawVideo(session, layer.Clip, seconds, width, height, opacity);
                            break;
                        case TimelineTrackKind.Image when GetImage(layer.Clip.SourcePath) is CanvasBitmap image:
                            VideoTransformValue imageTransform = VideoTransformKeyframes.GetValueAtTime(
                                layer.Clip, seconds, _project.FrameRate);
                            VideoCropValue imageCrop = VideoCropKeyframes.GetValueAtTime(
                                layer.Clip, seconds, _project.FrameRate);
                            TimelineCompositor.DrawFitted(session, image, VideoCrop.GetSourceRect(image.Bounds, imageCrop), width, height,
                                imageTransform.Scale, imageTransform.X, imageTransform.Y, opacity,
                                imageTransform.RotationDegrees, layer.Clip, GetColourGradingEffect(layer.Clip, seconds),
                                GetChromaKeyEffect(layer.Clip), GetBlurEffect(layer.Clip, seconds),
                                GetVignetteEffect(layer.Clip, seconds),
                                VideoTransformKeyframes.GetFrameOffset(layer.Clip, seconds, _project.FrameRate),
                                GetSharpenEffect(layer.Clip, seconds),
                                VideoColourGradingKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate),
                                VideoVignetteKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate),
                                VideoBlurKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate),
                                VideoSharpenKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate),
                                VideoChromaKeyKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate));
                            break;
                        case TimelineTrackKind.Text:
                            TimelineCompositor.DrawTitle(
                                session, layer.Clip, width, height, opacity,
                                VideoTransformKeyframes.GetValueAtTime(layer.Clip, seconds, _project.FrameRate));
                            break;
                    }
                }

                if (_hasInk)
                {
                    TimelineCompositor.DrawInkFrame(
                        session,
                        _project,
                        ExportPlanning.GetInkFrameIndex(_project, frame, _profile.FrameRate),
                        width,
                        height,
                        _inkScale);
                }
            }

            return _target.GetPixelBytes();
        }

        public void Dispose()
        {
            foreach (TimelineVideoFrameSource? video in _videos.Values)
            {
                video?.Dispose();
            }

            foreach (CanvasBitmap? image in _images.Values)
            {
                image?.Dispose();
            }

            foreach (VideoColourGradingEffect effect in _colourGradingEffects.Values)
            {
                effect.Dispose();
            }

            foreach (VideoChromaKeyEffect effect in _chromaKeyEffects.Values)
            {
                effect.Dispose();
            }

            foreach (VideoBlurCanvasEffect effect in _blurEffects.Values)
            {
                effect.Dispose();
            }

            foreach (VideoVignetteCanvasEffect effect in _vignetteEffects.Values)
            {
                effect.Dispose();
            }

            foreach (VideoSharpenCanvasEffect effect in _sharpenEffects.Values)
            {
                effect.Dispose();
            }

            _target.Dispose();
        }

        private void DrawVideo(CanvasDrawingSession session, TimelineClipProject clip, double seconds, float width, float height, float opacity)
        {
            if (clip.UseProxy &&
                !VideoProxy.IsAvailable(clip) &&
                _missingProxyWarnings.Add(clip.Id))
            {
                _warnings.Add($"Proxy unavailable for {Path.GetFileName(clip.SourcePath)}; the original source was used.");
            }

            if (!_videos.TryGetValue(clip.Id, out TimelineVideoFrameSource? video))
            {
                video = OpenVideo(clip);
                _videos[clip.Id] = video;
            }

            if (video is null)
            {
                return;
            }

            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, seconds, _project.FrameRate);
            if (video.GetFrame(_device, sourceSeconds, clip.RollingShutterProfile) is CanvasBitmap bitmap)
            {
                (int x, int y, int w, int h) = video.Aperture;
                VideoTransformValue transform = VideoTransformKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate);
                VideoCropValue crop = VideoCropKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate);
                TimelineCompositor.DrawFitted(session, bitmap, VideoCrop.GetSourceRect(new Rect(x, y, w, h), crop), width, height,
                    transform.Scale, transform.X, transform.Y, opacity, transform.RotationDegrees, clip,
                    GetColourGradingEffect(clip, seconds), GetChromaKeyEffect(clip), GetBlurEffect(clip, seconds),
                    GetVignetteEffect(clip, seconds),
                    VideoTransformKeyframes.GetFrameOffset(clip, seconds, _project.FrameRate),
                    GetSharpenEffect(clip, seconds),
                    VideoColourGradingKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate),
                    VideoVignetteKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate),
                    VideoBlurKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate),
                    VideoSharpenKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate),
                    VideoChromaKeyKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate));
            }
        }

        private VideoColourGradingEffect? GetColourGradingEffect(TimelineClipProject clip, double? seconds = null)
        {
            VideoColourGradingValue value = VideoColourGradingKeyframes.GetValueAtTime(
                clip, seconds ?? 0, _project.FrameRate);
            if (VideoColourGrading.IsNeutral(clip, value))
            {
                return null;
            }

            if (!_colourGradingEffects.TryGetValue(clip.Id, out VideoColourGradingEffect? effect))
            {
                effect = new VideoColourGradingEffect();
                _colourGradingEffects.Add(clip.Id, effect);
            }

            return effect;
        }

        private VideoChromaKeyEffect? GetChromaKeyEffect(TimelineClipProject clip)
        {
            if (!clip.ChromaKeyEnabled)
            {
                return null;
            }

            if (!_chromaKeyEffects.TryGetValue(clip.Id, out VideoChromaKeyEffect? effect))
            {
                effect = new VideoChromaKeyEffect();
                _chromaKeyEffects.Add(clip.Id, effect);
            }

            return effect;
        }

        private VideoBlurCanvasEffect? GetBlurEffect(TimelineClipProject clip, double seconds)
        {
            if (VideoBlurKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate) <= 0)
            {
                return null;
            }

            if (!_blurEffects.TryGetValue(clip.Id, out VideoBlurCanvasEffect? effect))
            {
                effect = new VideoBlurCanvasEffect();
                _blurEffects.Add(clip.Id, effect);
            }

            return effect;
        }

        private VideoVignetteCanvasEffect? GetVignetteEffect(TimelineClipProject clip, double seconds)
        {
            if (VideoVignetteKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate) <= 0)
            {
                return null;
            }

            if (!_vignetteEffects.TryGetValue(clip.Id, out VideoVignetteCanvasEffect? effect))
            {
                effect = new VideoVignetteCanvasEffect();
                _vignetteEffects.Add(clip.Id, effect);
            }

            return effect;
        }

        private VideoSharpenCanvasEffect? GetSharpenEffect(TimelineClipProject clip, double seconds)
        {
            if (VideoSharpenKeyframes.GetValueAtTime(clip, seconds, _project.FrameRate) <= 0)
            {
                return null;
            }

            if (!_sharpenEffects.TryGetValue(clip.Id, out VideoSharpenCanvasEffect? effect))
            {
                effect = new VideoSharpenCanvasEffect();
                _sharpenEffects.Add(clip.Id, effect);
            }

            return effect;
        }

        private TimelineVideoFrameSource? OpenVideo(TimelineClipProject clip)
        {
            string path = VideoProxy.GetRenderPath(clip);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return new TimelineVideoFrameSource(path);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _warnings.Add($"Skipped video {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private void ReleaseFinishedVideos(IReadOnlyList<TimelineClipLocation> layers)
        {
            foreach (string id in _videos.Keys.ToList())
            {
                if (!layers.Any(layer => layer.Clip.Id == id))
                {
                    _videos[id]?.Dispose();
                    _videos.Remove(id);
                }
            }
        }

        private CanvasBitmap? GetImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (!_images.TryGetValue(path, out CanvasBitmap? image))
            {
                try
                {
                    image = CanvasBitmap.LoadAsync(_device, path).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _warnings.Add($"Skipped image {Path.GetFileName(path)}: {ex.Message}");
                    image = null;
                }

                _images[path] = image;
            }

            return image;
        }
    }

}
