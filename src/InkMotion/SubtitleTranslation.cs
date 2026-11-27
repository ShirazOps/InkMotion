using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InkMotion;

/// <summary>Translates one subtitle cue's text.</summary>
public interface ISubtitleTranslator
{
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken);
}

public sealed record SubtitleTranslationResult(IReadOnlyList<TimelineTrackProject> Tracks, IReadOnlyList<string> Warnings);

/// <summary>
/// Creates translated copies of subtitle tracks. Each copy keeps every clip's timing, position and style; only the
/// text changes. Word runs are cleared because word boundaries do not survive translation.
/// </summary>
public static class SubtitleTranslation
{
    public const int MaximumLanguageLength = 40;

    public static async Task<SubtitleTranslationResult> TranslateTracksAsync(IReadOnlyList<TimelineTrackProject> tracks,
        string targetLanguage, ISubtitleTranslator translator, IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(translator);
        string language = ValidateLanguage(targetLanguage, nameof(targetLanguage));
        TimelineTrackProject[] sources = tracks.Where(track => track.Kind == TimelineTrackKind.Subtitle && track.Clips.Count > 0)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidDataException("There are no subtitle cues to translate.");
        }

        int total = sources.Sum(track => track.Clips.Count);
        int completed = 0;
        var warnings = new SubtitleWarnings();
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var translated = new List<TimelineTrackProject>();
        progress?.Report((0, total));
        foreach (TimelineTrackProject source in sources)
        {
            TimelineTrackProject copy = VideoTimelineEditing.Clone(source);
            copy.Id = VideoTimelineEditing.NewTrackId();
            string suffix = $" ({language})";
            copy.Name = (source.Name.Length + suffix.Length <= 64 ? source.Name : source.Name[..(64 - suffix.Length)].TrimEnd()) + suffix;
            foreach (TimelineClipProject clip in copy.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                clip.Id = VideoTimelineEditing.NewClipId();
                string text = SubtitleSrt.NormaliseLineEndings(clip.Text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidDataException("A subtitle clip has no text to translate.");
                }

                if (!cache.TryGetValue(text, out string? result))
                {
                    result = ValidateTranslation(text, await translator.TranslateAsync(text, cancellationToken).ConfigureAwait(false));
                    cache[text] = result;
                }

                if (SubtitleRichText.HasRuns(clip))
                {
                    warnings.Add("Word timing and styling cannot follow a translation, so translated cues use the cue style only.");
                    clip.SubtitleRuns = [];
                }

                clip.Text = result;
                progress?.Report((++completed, total));
            }

            translated.Add(copy);
        }

        return new(translated, warnings.ToArray());
    }

    internal static string ValidateLanguage(string language, string parameterName)
    {
        string trimmed = language?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaximumLanguageLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"Enter a language name of 1 to {MaximumLanguageLength} characters.", parameterName);
        }

        return trimmed;
    }

    /// <summary>Checks that a translation is usable text with the same number of lines as the source.</summary>
    internal static string ValidateTranslation(string source, string translation)
    {
        string text = SubtitleSrt.NormaliseLineEndings(translation ?? string.Empty).Trim('\n', ' ', '\t');
        string[] sourceLines = source.Split('\n').Select(line => line.Trim()).ToArray();
        string[] lines = text.Split('\n').Select(line => line.Trim()).ToArray();
        bool sourceHasBlankLines = sourceLines.Any(line => line.Length == 0);
        if (!sourceHasBlankLines)
        {
            // Models often separate translated lines with a blank line the source never had.
            lines = lines.Where(line => line.Length > 0).ToArray();
        }

        int expected = sourceLines.Length;
        if (text.Length == 0 || (lines.Length == expected &&
            lines.Where((line, index) => line.Length == 0 && sourceLines[index].Length > 0).Any()))
        {
            throw new InvalidDataException($"The model returned an empty translation for \"{Shorten(source)}\".");
        }

        if (lines.Length != expected)
        {
            throw new InvalidDataException(
                $"The model returned {lines.Length} line(s) for \"{Shorten(source)}\", which has {expected}. Try another model.");
        }

        string joined = string.Join("\n", lines);
        if (joined.Length > 2048)
        {
            throw new InvalidDataException($"The translation of \"{Shorten(source)}\" exceeds 2048 characters.");
        }

        return joined;
    }

    private static string Shorten(string text) =>
        text.Length <= 40 ? text.Replace('\n', ' ') : text[..40].Replace('\n', ' ') + "…";
}

/// <summary>
/// Calls an OpenAI-compatible chat completions server that is already running on this computer, such as Foundry Local,
/// Ollama, LM Studio or llama.cpp. Only loopback addresses are accepted, proxies and redirects are disabled, and nothing
/// is downloaded: the server must already have the model available.
/// </summary>
public sealed partial class LocalChatSubtitleTranslator : ISubtitleTranslator, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _baseUri;
    private readonly string _model;
    private readonly string _targetLanguage;
    private readonly string? _sourceLanguage;

    public LocalChatSubtitleTranslator(Uri baseUri, string model, string targetLanguage, string? sourceLanguage,
        HttpClient? client = null)
    {
        _baseUri = ValidateEndpoint(baseUri);
        _model = string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl)
            ? throw new ArgumentException("Enter the model name the local server uses.", nameof(model))
            : model.Trim();
        _targetLanguage = SubtitleTranslation.ValidateLanguage(targetLanguage, nameof(targetLanguage));
        _sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
            ? null
            : SubtitleTranslation.ValidateLanguage(sourceLanguage, nameof(sourceLanguage));
        _ownsClient = client is null;
        _client = client ?? CreateClient();
    }

    /// <summary>Accepts only http(s) loopback addresses without credentials, query or fragment.</summary>
    public static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        bool loopback = endpoint.IsAbsoluteUri &&
            (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address)));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || !loopback ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException(
                "Enter the address of a translation server running on this computer, such as http://127.0.0.1:5273/v1.",
                nameof(endpoint));
        }

        string text = endpoint.GetLeftPart(UriPartial.Path);
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    /// <summary>Lists the models the local server reports as available.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(Uri baseUri, HttpClient? client, CancellationToken cancellationToken)
    {
        Uri root = ValidateEndpoint(baseUri);
        HttpClient http = client ?? CreateClient();
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(root, "models"), cancellationToken).ConfigureAwait(false);
            string body = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            JsonArray? data = Parse(body)["data"] as JsonArray;
            return data?.Select(item => item?["id"]?.GetValue<string>()).OfType<string>()
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        }
        finally
        {
            if (client is null)
            {
                http.Dispose();
            }
        }
    }

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        string first = await RequestAsync(text, retry: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return SubtitleTranslation.ValidateTranslation(text, first);
        }
        catch (InvalidDataException)
        {
            // Small local models occasionally add or merge lines; ask once more with a firmer reminder.
            return SubtitleTranslation.ValidateTranslation(text,
                await RequestAsync(text, retry: true, cancellationToken).ConfigureAwait(false));
        }
    }

    internal string BuildSystemPrompt(int lineCount, bool retry)
    {
        var prompt = new StringBuilder();
        prompt.Append("You translate video subtitles. Translate the user's subtitle text ")
            .Append(_sourceLanguage is null ? "from its original language" : $"from {_sourceLanguage}")
            .Append(" into ").Append(_targetLanguage).Append(". Reply with the translation only: no notes, quotes, ")
            .Append("explanations or alternatives. Keep names, numbers and meaning. The text has exactly ").Append(lineCount)
            .Append(lineCount == 1 ? " line; reply with exactly 1 line." : $" lines; reply with exactly {lineCount} lines, one translated line per original line, in the same order.");
        if (retry)
        {
            prompt.Append(" Your previous reply had the wrong number of lines. Follow the line count exactly.");
        }

        return prompt.ToString();
    }

    private async Task<string> RequestAsync(string text, bool retry, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = 0,
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(text.Split('\n').Length, retry) },
                new JsonObject { ["role"] = "user", ["content"] = text },
            },
        };
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _client.PostAsync(new Uri(_baseUri, "chat/completions"), content, cancellationToken)
            .ConfigureAwait(false);
        string body = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? reply = null;
        try
        {
            reply = Parse(body)["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
        }

        if (reply is null)
        {
            throw new InvalidDataException("The local translation server returned a response without message content.");
        }

        // Reasoning models put their working inside <think> tags before the answer.
        return ThinkBlock().Replace(reply, string.Empty);
    }

    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = body.Length > 200 ? body[..200] + "…" : body;
            throw new InvalidDataException(
                $"The local translation server returned {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
        }

        return body;
    }

    private static JsonNode Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body) ?? throw new InvalidDataException("The local translation server returned an empty response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("The local translation server did not return JSON.", ex);
        }
    }

    private static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = RequestTimeout };

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkBlock();

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
