using System.Xml.Linq;
using Xunit;

namespace InkMotion.Tests;

public sealed class PictureInterchangeTests
{
    private static string MediaPath => Path.Combine(Path.GetTempPath(), "InkMotion media", "cut & picture.mp4");
    private static PictureTimeline Example(int rate = 24) => new(rate, 1920, 1080,
        [new("Picture", [new(MediaPath, 0, rate * 2, rate * 3, rate * 12), new(MediaPath, rate * 5, rate, rate * 2, rate * 12)])]);

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void Xml_PreservesFramesPathsGapsAndSourceBounds(int rate)
    {
        PictureTimeline original = Example(rate);
        PictureTimeline restored = PictureInterchange.ReadXml(PictureInterchange.WriteXml(original));
        Assert.Equal(original.Tracks[0].Cuts, restored.Tracks[0].Cuts);
        EditorProject project = PictureInterchange.ToProject(restored);
        Assert.Equal(rate * 5 + 1, project.VideoTracks[0].Clips[1].StartFrame);
        Assert.Equal(0, project.VideoTracks[0].Clips[0].Volume);
        Assert.Equal(2, project.VideoTracks[0].Clips[0].SourceInSeconds);
        Assert.Equal(5, project.VideoTracks[0].Clips[0].SourceOutSeconds);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    public void Edl_PreservesSourceRecordTimecodesAndMediaComments(int rate)
    {
        var timeline = Example(rate);
        var restored = PictureInterchange.ReadEdl(PictureInterchange.WriteEdl(timeline), rate, Path.GetTempPath());
        Assert.Equal(timeline.Tracks[0].Cuts.Select(c => (c.Path, c.Start, c.In, c.Length)),
            restored.Tracks[0].Cuts.Select(c => (c.Path, c.Start, c.In, c.Length)));
    }

    [Fact]
    public void Xml_ResolvesFileIdsAndMultipleTracks()
    {
        XDocument xml = XDocument.Parse(PictureInterchange.WriteXml(Example()));
        XElement video = xml.Descendants("sequence").Single().Element("media")!.Element("video")!;
        XElement track = video.Element("track")!;
        XElement secondFile = track.Elements("clipitem").Last().Element("file")!;
        secondFile.ReplaceWith(new XElement("file", new XAttribute("id", "file-1")));
        video.Add(new XElement(track));
        var restored = PictureInterchange.ReadXml(xml.ToString());
        Assert.Equal(2, restored.Tracks.Count);
        Assert.Equal(MediaPath, restored.Tracks[1].Cuts[1].Path);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("transitionitem")]
    [InlineData("sequence")]
    [InlineData("generatoritem")]
    public void Xml_RejectsUnrepresentableMaterial(string element)
    {
        XDocument xml = XDocument.Parse(PictureInterchange.WriteXml(Example()));
        xml.Descendants("clipitem").First().Add(new XElement(element));
        Assert.Throws<InvalidDataException>(() => PictureInterchange.ReadXml(xml.ToString()));
    }

    [Theory]
    [InlineData("ntsc", "TRUE")]
    [InlineData("start", "-1")]
    [InlineData("out", "999")]
    [InlineData("enabled", "FALSE")]
    public void Xml_RejectsFractionalRetimedTransitionAndDisabledClips(string field, string value)
    {
        XDocument xml = XDocument.Parse(PictureInterchange.WriteXml(Example()));
        XElement clip = xml.Descendants("clipitem").First();
        clip.Descendants(field).First().Value = value;
        Assert.Throws<InvalidDataException>(() => PictureInterchange.ReadXml(xml.ToString()));
    }

    [Theory]
    [InlineData("FCM: DROP FRAME")]
    [InlineData("FCM: NON-DROP FRAME\n001 AX V D 024 00:00:00:00 00:00:01:00 00:00:00:00 00:00:01:00")]
    [InlineData("FCM: NON-DROP FRAME\n001 AX A C 00:00:00:00 00:00:01:00 00:00:00:00 00:00:01:00")]
    [InlineData("FCM: NON-DROP FRAME\n001 AX V C 00:00:00:24 00:00:01:00 00:00:00:00 00:00:01:00")]
    [InlineData("FCM: NON-DROP FRAME\nM2 AX 48 00:00:00:00")]
    public void Edl_RejectsDropFrameTransitionsAudioAndInvalidTimecodes(string text) =>
        Assert.Throws<InvalidDataException>(() => PictureInterchange.ReadEdl(text, 24, Path.GetTempPath()));

    [Fact]
    public void ProjectExport_RejectsEffectsArtworkSubframesAndWrongMediaRate()
    {
        var project = PictureInterchange.ToProject(Example());
        project.AudioTracks.Add(new AudioTrackProject());
        var metadata = new Dictionary<string, PictureMediaInfo> { [MediaPath] = new(24, 1920, 1080) };
        Assert.Equal(2, PictureInterchange.FromProject(project, metadata).Tracks[0].Cuts.Count);
        project.VideoTracks[0].Clips[0].Opacity = 0.5;
        Assert.Contains("Opacity", Assert.Throws<InvalidDataException>(() => PictureInterchange.FromProject(project, metadata)).Message);
        project.VideoTracks[0].Clips[0].Opacity = 1;
        project.VideoTracks[0].Clips[0].SourceInSeconds += 0.001;
        Assert.Throws<InvalidDataException>(() => PictureInterchange.FromProject(project, metadata));
        project.VideoTracks[0].Clips[0].SourceInSeconds = 2;
        metadata[MediaPath] = new(25, 1920, 1080);
        Assert.Throws<InvalidDataException>(() => PictureInterchange.FromProject(project, metadata));
    }

    [Fact]
    public void OverlapsAndSubframeRounding_AreRejected()
    {
        var timeline = Example();
        timeline.Tracks[0].Cuts[1] = timeline.Tracks[0].Cuts[1] with { Start = 1 };
        Assert.Throws<InvalidDataException>(() => PictureInterchange.WriteXml(timeline));
        Assert.Throws<InvalidDataException>(() => PictureInterchange.Frames(1.001, 24));
    }

    [Fact]
    public void ExternalParserFixtures_WriteAndReadWhenRequested()
    {
        string? directory = Environment.GetEnvironmentVariable("INKMOTION_INTERCHANGE_FIXTURES");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "inkmotion.xml"), PictureInterchange.WriteXml(Example()));
        File.WriteAllText(Path.Combine(directory, "inkmotion.edl"), PictureInterchange.WriteEdl(Example()));
        foreach (string extension in new[] { "xml", "edl" })
        {
            string input = Path.Combine(directory, $"external.{extension}");
            if (!File.Exists(input)) continue;
            var imported = extension == "xml" ? PictureInterchange.ReadXml(File.ReadAllText(input)) :
                PictureInterchange.ReadEdl(File.ReadAllText(input), 24, directory);
            Assert.Equal(Example().Tracks[0].Cuts.Select(c => (c.Start, c.In, c.Length)),
                imported.Tracks[0].Cuts.Select(c => (c.Start, c.In, c.Length)));
        }
    }
}
