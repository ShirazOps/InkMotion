using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace InkMotion;

/// <param name="InkReferenceHeight">Height in DIPs of the ink canvas the strokes were drawn on, used to scale brushes.</param>
public sealed record VideoExportRequest(
    EditorProject Project,
    VideoExportProfile Profile,
    VideoExportCodec Codec,
    string OutputPath,
    double InkReferenceHeight,
    bool PreferHardware = true,
    double Quality = 0.5);

public sealed record VideoExportProgress(int Frame, int FrameCount, string Stage)
{
    public double Fraction => FrameCount <= 0 ? 0 : Math.Clamp((double)Frame / FrameCount, 0, 1);
}

public sealed record VideoExportResult(
    string OutputPath,
    string? SurroundStemPath,
    int FrameCount,
    double DurationSeconds,
    bool UsedHardwareEncoder,
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
        ExportPlanning.ValidateOutputPath(project, request.OutputPath);
        VideoExportProfile profile = request.Profile;
        var warnings = new List<string>();
        string? stemPath = null;
        bool completed = false;

        progress?.Report(new VideoExportProgress(0, 0, "Preparing audio"));
        using var mixer = new ExportAudioMixer(project);
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
        bool usedHardware = false;
        try
        {
            try
            {
                writer = CreateWriter(request, frameCount, hasAudio ? encodedInfo : null, warnings, out usedHardware);
            }
            catch (AudioEncoderUnavailableException) when (encodedInfo.ChannelCount > 2)
            {
                warnings.Add($"Windows could not encode {encodedInfo.DisplayName} AAC on this PC, so the MP4 carries a stereo downmix.");
                encodedLayout = AudioChannelLayout.Stereo;
                encodedInfo = AudioChannelLayouts.Get(encodedLayout);
                downmix = AudioChannelLayouts.CreateMixMatrix(mixer.Layout, encodedLayout);
                writer = CreateWriter(request, frameCount, encodedInfo, warnings, out usedHardware);
            }

            AudioChannelLayoutInfo mixInfo = AudioChannelLayouts.Get(mixer.Layout);
            if (hasAudio && mixInfo.ChannelCount > 2 && mixInfo.ChannelCount > encodedInfo.ChannelCount)
            {
                stemPath = ExportPlanning.GetSurroundStemPath(request.OutputPath);
                stem = new MultichannelWaveWriter(stemPath, ExportPlanning.AudioSampleRate, mixInfo.ChannelCount, mixInfo.ChannelMask);
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
                    audio.WriteThrough(ExportPlanning.GetAudioFramesThrough(frame, profile.FrameRate));
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
                usedHardware,
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
        out bool usedHardware)
    {
        VideoExportProfile profile = request.Profile;
        int bitRate = ExportPlanning.GetVideoBitRate(profile, request.Codec, request.Quality);
        MediaFoundationVideoWriter Create(bool hardware) => new(
            request.OutputPath,
            profile.Width,
            profile.Height,
            profile.FrameRate,
            request.Codec,
            bitRate,
            audio?.ChannelCount ?? 0,
            audio?.ChannelMask ?? 0,
            hardware);

        if (request.PreferHardware)
        {
            try
            {
                usedHardware = true;
                return Create(true);
            }
            catch (COMException)
            {
                TryDelete(request.OutputPath);
                warnings.Add("The GPU encoder was unavailable, so the software encoder was used.");
            }
        }

        usedHardware = false;
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

                long time = _written * 10_000_000L / ExportPlanning.AudioSampleRate;
                long end = (_written + frames) * 10_000_000L / ExportPlanning.AudioSampleRate;
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
        private readonly Dictionary<string, VideoSource?> _videos = [];

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
                _project.FrameRate);
            ReleaseFinishedVideos(layers);
            using (CanvasDrawingSession session = _target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Black);
                foreach (TimelineClipLocation layer in layers)
                {
                    float opacity = (float)VideoTimelineEditing.GetOpacity(layer.Clip, layer.Track.Kind, seconds, _project.FrameRate);
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
                            TimelineCompositor.DrawFitted(session, image, image.Bounds, width, height, layer.Clip.Scale, layer.Clip.X, layer.Clip.Y, opacity);
                            break;
                        case TimelineTrackKind.Text:
                            TimelineCompositor.DrawTitle(session, layer.Clip, width, height, opacity);
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
            foreach (VideoSource? video in _videos.Values)
            {
                video?.Dispose();
            }

            foreach (CanvasBitmap? image in _images.Values)
            {
                image?.Dispose();
            }

            _target.Dispose();
        }

        private void DrawVideo(CanvasDrawingSession session, TimelineClipProject clip, double seconds, float width, float height, float opacity)
        {
            if (!_videos.TryGetValue(clip.Id, out VideoSource? video))
            {
                video = OpenVideo(clip);
                _videos[clip.Id] = video;
            }

            if (video is null)
            {
                return;
            }

            double sourceSeconds = VideoTimelineEditing.GetSourceSeconds(clip, seconds, _project.FrameRate);
            if (video.Update(_device, sourceSeconds) is CanvasBitmap bitmap)
            {
                (int x, int y, int w, int h) = video.Reader.Aperture;
                TimelineCompositor.DrawFitted(session, bitmap, new Rect(x, y, w, h), width, height, 1, 0.5, 0.5, opacity);
            }
        }

        private VideoSource? OpenVideo(TimelineClipProject clip)
        {
            if (string.IsNullOrWhiteSpace(clip.SourcePath))
            {
                return null;
            }

            try
            {
                return new VideoSource(new MediaFoundationVideoReader(clip.SourcePath));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _warnings.Add($"Skipped video {Path.GetFileName(clip.SourcePath)}: {ex.Message}");
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

    /// <summary>Uploads decoded frames to the GPU only when the decoder produced a new picture.</summary>
    private sealed class VideoSource(MediaFoundationVideoReader reader) : IDisposable
    {
        private CanvasBitmap? _bitmap;
        private long _version = -1;

        public MediaFoundationVideoReader Reader => reader;

        public CanvasBitmap? Update(CanvasDevice device, double sourceSeconds)
        {
            if (!reader.TryGetFrame(sourceSeconds) && _bitmap is null)
            {
                return null;
            }

            if (reader.FrameVersion != _version && reader.Width > 0 && reader.Height > 0)
            {
                if (_bitmap is null || _bitmap.SizeInPixels.Width != reader.Width || _bitmap.SizeInPixels.Height != reader.Height)
                {
                    _bitmap?.Dispose();
                    _bitmap = CanvasBitmap.CreateFromBytes(
                        device,
                        reader.Pixels,
                        reader.Width,
                        reader.Height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        96,
                        CanvasAlphaMode.Ignore);
                }
                else
                {
                    _bitmap.SetPixelBytes(reader.Pixels);
                }

                _version = reader.FrameVersion;
            }

            return _bitmap;
        }

        public void Dispose()
        {
            _bitmap?.Dispose();
            reader.Dispose();
        }
    }
}
