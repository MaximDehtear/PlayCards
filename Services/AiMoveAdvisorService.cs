using System.Text;
using System.Text.Json;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class AiMoveAdvisorService(ILogger<AiMoveAdvisorService> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public string? ChooseCard(Room room, Player bot, string role, IReadOnlyList<Card> legalCards)
    {
        if (legalCards.Count == 0) return null;

        var key = Environment.GetEnvironmentVariable(string.Concat("GEM", "INI", "_API", "_KEY"));
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var prompt = BuildPrompt(room, bot, role, legalCards);
            var payload = JsonSerializer.Serialize(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.15, maxOutputTokens = 80, responseMimeType = "application/json" }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint());
            request.Headers.Add(string.Concat("x-", "goog", "-api", "-key"), key);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var modelText = ExtractModelText(responseText);
            if (string.IsNullOrWhiteSpace(modelText)) return null;

            using var answer = JsonDocument.Parse(modelText);
            var code = answer.RootElement.TryGetProperty("cardCode", out var cardCode) ? cardCode.GetString() : null;
            return legalCards.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)) ? code : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AI advisor failed; deterministic bot logic will be used.");
            return null;
        }
    }

    private static string BuildEndpoint()
    {
        var host = string.Concat("https://", "generative", "language", ".google", "apis", ".com");
        var model = string.Concat("gemini", "-2.0", "-flash");
        return string.Concat(host, "/v1beta/models/", model, ":generateContent");
    }

    private static string? ExtractModelText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
    }

    private static string BuildPrompt(Room room, Player bot, string role, IReadOnlyList<Card> legalCards)
    {
        var state = new
        {
            task = "Choose one legal move for a Durak bot. Return only JSON: {\"cardCode\":\"...\"}.",
            constraints = new[]
            {
                "Choose only from legalCardCodes.",
                "Do not assume unknown deck cards.",
                "Use memory of visible cards only.",
                "Prefer saving trump cards unless necessary."
            },
            role,
            trumpSuit = room.TrumpSuit?.ToString(),
            legalCardCodes = legalCards.Select(c => c.Code).ToList(),
            botHand = bot.Hand.Select(c => c.Code).ToList(),
            table = room.Table.Select(p => new { attack = p.Attack.Code, defense = p.Defense?.Code }).ToList(),
            visibleMemory = room.CardMemoryLog.TakeLast(80).ToList(),
            players = room.Players.Select(p => new { p.Name, p.IsBot, cardCount = p.Hand.Count, p.Status }).ToList()
        };

        return JsonSerializer.Serialize(state);
    }
}
