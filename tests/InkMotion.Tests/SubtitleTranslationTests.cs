using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace InkMotion.Tests;

public sealed class LocalTranslationFactAttribute : FactAttribute
{
    public LocalTranslationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INKMOTION_TEST_TRANSLATION_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INKMOTION_TEST_TRANSLATION_MODEL")))
            Skip = "Optional integration test: set INKMOTION_TEST_TRANSLATION_ENDPOINT and INKMOTION_TEST_TRANSLATION_MODEL to a running local server and an already-installed model.";
    }
}

public sealed class SubtitleTranslationTests
{    private sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body));
            (HttpStatusCode status, string response) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    private sealed class DictionaryTranslator(Dictionary<string, string> map) : ISubtitleTranslator
    {
        public int Calls { get; private set; }

        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(map[text]);
        }
    }

    private static string Completion(string content) =>
        new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = content } } } }
            .ToJsonString();

    [Fact]
    public async Task TranslateTracks_KeepsTimingStyleAndOriginalsAndClearsRuns()
    {
        IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.CreateTracks(
        [
            new SubtitleCueText(1, 2.5, "Hello\nfriend")
            {
                Runs = [new SubtitleRun { Text = "Hello", Bold = true }, new SubtitleRun { Text = "\nfriend" }],
                Style = new SubtitleCueStyle { FontFamily = "Georgia", X = 0.3, Y = 0.2, FadeInSeconds = 0.25 },
            },
            new SubtitleCueText(3, 4, "Hello\nfriend"),
            new SubtitleCueText(3.5, 5, "Bye"),
        ], 25);
        TimelineTrackProject[] input = [.. tracks, new TimelineTrackProject { Kind = TimelineTrackKind.Text }];
        var translator = new DictionaryTranslator(new() { ["Hello\nfriend"] = "Bonjour\nl'ami", ["Bye"] = "Au revoir" });
        var reports = new List<(int, int)>();

        SubtitleTranslationResult result = await SubtitleTranslation.TranslateTracksAsync(input, " French ", translator,
            new SynchronousProgress(reports), CancellationToken.None);

        Assert.Equal(2, translator.Calls);
        Assert.Equal((3, 3), reports[^1]);
        Assert.Single(result.Warnings);
        Assert.Equal(tracks.Count, result.Tracks.Count);
        TimelineClipProject[] originals = tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        TimelineClipProject[] copies = result.Tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        Assert.Equal("Hello\nfriend", originals[0].Text);
        Assert.NotEmpty(originals[0].SubtitleRuns);
        Assert.Equal(["Bonjour\nl'ami", "Bonjour\nl'ami", "Au revoir"], copies.Select(clip => clip.Text));
        Assert.All(copies, clip => Assert.Empty(clip.SubtitleRuns));
        for (int index = 0; index < copies.Length; index++)
        {
            Assert.NotEqual(originals[index].Id, copies[index].Id);
            Assert.Equal(originals[index].StartFrame, copies[index].StartFrame);
            Assert.Equal(originals[index].DurationSeconds, copies[index].DurationSeconds);
            Assert.Equal(originals[index].FontFamily, copies[index].FontFamily);
            Assert.Equal(originals[index].X, copies[index].X);
            Assert.Equal(originals[index].Y, copies[index].Y);
            Assert.Equal(originals[index].FadeInSeconds, copies[index].FadeInSeconds);
        }

        Assert.All(result.Tracks, track => Assert.EndsWith(" (French)", track.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Tracks, copy => tracks.Any(track => track.Id == copy.Id));
    }

    [Theory]
    [InlineData("One\nTwo", "Un", "1 line")]
    [InlineData("One", "   ", "empty")]
    [InlineData("One", "Un\n\nDeux", "2 line(s)")]
    [InlineData("One\n\nTwo", "Un\nDeux", "2 line(s)")]
    [InlineData("One\n\nTwo", "Un\n\n", "1 line(s)")]
    public void ValidateTranslation_RejectsChangedLineStructure(string source, string translation, string expected)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SubtitleTranslation.ValidateTranslation(source, translation));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Good morning.\nHow are you?", "Bonjour.\n\nComment ça va ?", "Bonjour.\nComment ça va ?")]
    [InlineData("One\n\nTwo", " Un \n\nDeux", "Un\n\nDeux")]
    public void ValidateTranslation_NormalisesSpacingWithoutChangingLines(string source, string translation, string expected) =>
        Assert.Equal(expected, SubtitleTranslation.ValidateTranslation(source, translation));

    [Theory]
    [InlineData("http://127.0.0.1:5273/v1")]    [InlineData("http://localhost:11434/v1/")]
    [InlineData("https://[::1]:8080/api/v1")]
    public void ValidateEndpoint_AcceptsLoopback(string endpoint)
    {
        Uri result = LocalChatSubtitleTranslator.ValidateEndpoint(new Uri(endpoint));
        Assert.EndsWith("/", result.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.10:5273/v1")]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://localhost.example.com/v1")]
    [InlineData("http://user:pass@127.0.0.1:5273/v1")]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("http://127.0.0.1:5273/v1?key=1")]
    public void ValidateEndpoint_RejectsNonLocalOrUnsafeAddresses(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => LocalChatSubtitleTranslator.ValidateEndpoint(new Uri(endpoint)));
    }

    [Fact]
    public async Task Translator_PostsChatRequestStripsReasoningAndRetriesWrongLineCount()
    {
        int calls = 0;
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            Completion(++calls == 1 ? "Bonjour l'ami" : "<think>two lines</think>\nBonjour\nl'ami")));
        using var client = new HttpClient(handler);
        using var translator = new LocalChatSubtitleTranslator(new Uri("http://127.0.0.1:5273/v1"), "phi-4-mini", "French",
            "English", client);

        string result = await translator.TranslateAsync("Hello\nfriend", CancellationToken.None);

        Assert.Equal("Bonjour\nl'ami", result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://127.0.0.1:5273/v1/chat/completions", handler.Requests[0].Uri.AbsoluteUri);
        JsonNode request = JsonNode.Parse(handler.Requests[1].Body)!;
        Assert.Equal("phi-4-mini", request["model"]!.GetValue<string>());
        Assert.Equal(0, request["temperature"]!.GetValue<int>());
        string system = request["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("from English into French", system, StringComparison.Ordinal);
        Assert.Contains("exactly 2 lines", system, StringComparison.Ordinal);
        Assert.Contains("previous reply", system, StringComparison.Ordinal);
        Assert.Equal("Hello\nfriend", request["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Translator_ReportsServerErrorsAndListsModels()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"data":[{"id":"phi-4-mini"},{"id":"Phi-4"},{"id":"phi-4-mini"}]}""")
            : (HttpStatusCode.NotFound, "model not loaded"));
        using var client = new HttpClient(handler);

        IReadOnlyList<string> models = await LocalChatSubtitleTranslator.ListModelsAsync(new Uri("http://localhost:5273/v1"), client,
            CancellationToken.None);
        using var translator = new LocalChatSubtitleTranslator(new Uri("http://localhost:5273/v1"), "missing", "German", null, client);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            translator.TranslateAsync("Hi", CancellationToken.None));

        Assert.Equal(["Phi-4", "phi-4-mini"], models);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.Contains("model not loaded", error.Message, StringComparison.Ordinal);
    }

    private sealed class SynchronousProgress(List<(int, int)> reports) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => reports.Add(value);
    }

    [LocalTranslationFact]
    public async Task LocalServer_TranslatesCuesAndKeepsTimings()
    {
        IReadOnlyList<TimelineTrackProject> tracks = SubtitleSrt.CreateTracks(
            [new SubtitleCueText(1, 2.5, "Good morning.\nHow are you?"), new SubtitleCueText(3, 4, "Thank you very much.")], 25);
        using var translator = new LocalChatSubtitleTranslator(
            new Uri(Environment.GetEnvironmentVariable("INKMOTION_TEST_TRANSLATION_ENDPOINT")!),
            Environment.GetEnvironmentVariable("INKMOTION_TEST_TRANSLATION_MODEL")!, "French", "English");

        SubtitleTranslationResult result = await SubtitleTranslation.TranslateTracksAsync(tracks, "French", translator,
            null, CancellationToken.None);

        TimelineClipProject[] originals = tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        TimelineClipProject[] copies = result.Tracks.SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        Assert.Equal(originals.Select(clip => (clip.StartFrame, clip.DurationSeconds)), copies.Select(clip => (clip.StartFrame, clip.DurationSeconds)));
        Assert.Equal(2, copies[0].Text.Split('\n').Length);
        Assert.All(copies, clip => Assert.DoesNotContain(clip.Text, originals.Select(original => original.Text)));
        Console.WriteLine(string.Join(" | ", copies.Select(clip => clip.Text.Replace('\n', '/'))));
    }
}