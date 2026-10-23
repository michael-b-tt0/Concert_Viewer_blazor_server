using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concert_Viewer.Data.Entities;

namespace Concert_Viewer.Services;

public sealed class TavilyResearchClient(HttpClient http, IConfiguration configuration)
{
    public async Task<JsonElement> ResearchAsync(CanonicalEvent item, CancellationToken token,
        Action<string, string>? report = null)
    {
        var key = configuration["Tavily:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure Tavily:ApiKey in appsettings.json before researching corrections.");
        var properties = new JsonObject();
        foreach (var (field, description) in new[] {
            ("event_title", "Official event title. Use an empty string if unknown."),
            ("venue", "Official venue name. Use an empty string if unknown."),
            ("city", "City where the event takes place. Use an empty string if unknown."),
            ("description", "Factual event description supported by web evidence. Use an empty string if unknown.")
        })
            properties[field] = new JsonObject { ["type"] = "string", ["description"] = description };
        properties["artists"] = JsonNode.Parse("""
            {"type":"array","description":"All confirmed performing artists, including support acts, with one entry per artist.",
             "items":{"type":"object","description":"A confirmed performing artist, identified by name only.","properties":{
              "name":{"type":"string","description":"Official artist name."}},
              "required":["name"]}}
            """);
        properties["sources"] = JsonNode.Parse("""
            {"type":"array","description":"Web source URLs supporting the event information and performer lineup.",
             "items":{"type":"string","description":"Full HTTP or HTTPS URL of a supporting source."}}
            """);
        var prompt = """
            Determine the correct information for this music event from current web evidence.
            Use the provided source links and search for additional sources. Keep the event's date fixed; research
            this specific occurrence, not a different date or similarly named event.
            Return the official event title, venue, city, factual description, and ALL performing
            artists including support acts, find as many of these as possible as they may not all be listed on every event page.
            One entry per artist, with only the artists name and no malformed duplicates.
            Use an empty string for unknown event fields; do not invent facts.
            Return source URLs in sources. An empty artist list means no performers were confirmed.
            Event data:
            """ + JsonSerializer.Serialize(new {
                item.EventTitle, item.Venue, item.City,  item.EventDate,
                item.DiceUrl, item.SongkickUrl, item.BandsintownUrl}
                );
            
            
        using var request = new HttpRequestMessage(HttpMethod.Post, "research");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new {
            input = prompt, model = configuration["Tavily:Model"] ?? "mini", stream = false,
            output_schema = new JsonObject {
                // Tavily permits only properties and required at the schema root.
                ["properties"] = properties,
                ["required"] = new JsonArray("event_title", "venue", "city", "description", "artists", "sources")
            }
        });
        // Never automatically retry submission: a lost response could still represent a paid job.
        report?.Invoke("info", $"Submitting Tavily research (model: {configuration["Tavily:Model"] ?? "mini"}).");
        using var response = await http.SendAsync(request, token);
        await EnsureSuccessAsync(response, "submission", key, token);
        using var submission = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var id = submission.RootElement.GetProperty("request_id").GetString()
            ?? throw new InvalidOperationException("Tavily returned no request ID.");
        report?.Invoke("info", $"Tavily accepted research request {id}.");
        var delay = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Tavily:PollSeconds", 5), 1, 60));
        var pollCount = 0;
        while (true)
        {
            await Task.Delay(delay, token);
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"research/{Uri.EscapeDataString(id)}");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var result = await http.SendAsync(poll, token);
            pollCount++;
            if ((int)result.StatusCode == 429 || (int)result.StatusCode >= 500)
            {
                report?.Invoke("warn", $"Tavily poll {pollCount}: HTTP {(int)result.StatusCode}; retrying in {delay.TotalSeconds} seconds.");
                continue;
            }
            await EnsureSuccessAsync(result, $"status poll for {id}", key, token);
            using var document = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString() : null;
            report?.Invoke("info", $"Tavily poll {pollCount}: {status}.");
            switch (status)
            {
                case "completed":
                    var content = root.GetProperty("content");
                    if (content.ValueKind != JsonValueKind.String) return content.Clone();
                    using (var parsed = JsonDocument.Parse(content.GetString()!)) return parsed.RootElement.Clone();
                case "failed":
                    throw new InvalidOperationException($"Tavily research failed: {ErrorDetail(root, key)}. No changes were made.");
                case "pending": case "processing": case "in_progress": break;
                default:
                    throw new InvalidOperationException($"Tavily request {id} returned an unexpected research status: '{Redact(status ?? "<missing or invalid>", key)}' (HTTP {(int)result.StatusCode}).");
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string stage, string apiKey, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;

        // EnsureSuccessStatusCode discards the API's validation explanation. Keep a bounded,
        // redacted error detail so both the browser and server logs can explain a rejection.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var buffer = new char[8192];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        var body = new string(buffer, 0, count);
        string detail;
        try
        {
            using var document = JsonDocument.Parse(body);
            detail = ErrorDetail(document.RootElement, apiKey);
        }
        catch (JsonException) { detail = Redact(body, apiKey); }
        if (string.IsNullOrWhiteSpace(detail)) detail = "The service returned no error details.";
        throw new HttpRequestException(
            $"Tavily research {stage} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}",
            inner: null, statusCode: response.StatusCode);
    }

    private static string ErrorDetail(JsonElement root, string apiKey)
    {
        // Tavily commonly returns { "detail": { "error": "..." } }.
        foreach (var field in new[] { "detail", "error", "message" })
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value))
                return value.ValueKind == JsonValueKind.Object ? ErrorDetail(value, apiKey)
                    : Redact(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText(), apiKey);
        return "The service returned no error details.";
    }

    private static string Redact(string value, string apiKey)
    {
        var text = value.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"tvly-[A-Za-z0-9_-]+", "[redacted]");
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
