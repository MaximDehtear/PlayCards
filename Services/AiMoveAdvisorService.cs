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
            TrimMemory(bot);
            var prompt = BuildPrompt(room, bot, role, legalCards);
            bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: {bot.Name} asks AI as {role}. Legal cards: {string.Join(", ", legalCards.Select(c => c.Code))}. Hand: {string.Join(", ", bot.Hand.Select(c => c.Code))}. Trump: {room.TrumpSuit}.");

            var payload = JsonSerializer.Serialize(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.15, maxOutputTokens = 160, responseMimeType = "application/json" }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint());
            request.Headers.Add(string.Concat("x-", "goog", "-api", "-key"), key);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: AI did not answer successfully; fallback logic will be used.");
                return null;
            }

            var responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var modelText = ExtractModelText(responseText);
            if (string.IsNullOrWhiteSpace(modelText))
            {
                bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: AI returned empty answer; fallback logic will be used.");
                return null;
            }

            using var answer = JsonDocument.Parse(modelText);
            var code = answer.RootElement.TryGetProperty("cardCode", out var cardCode) ? cardCode.GetString() : null;
            var reason = answer.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
            var legal = legalCards.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

            bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: AI advised {code ?? "none"}. Reason: {reason ?? "not provided"}. Valid: {legal}.");
            return legal ? code : null;
        }
        catch (Exception ex)
        {
            bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: AI advisor failed; fallback logic will be used.");
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
            task = "You are a separate Gemini chat for this specific Durak bot. Continue from this bot's private memory and choose one legal move. Return only JSON: {\"cardCode\":\"...\",\"reason\":\"short reason\"}.",
            botIdentity = new
            {
                bot.Id,
                bot.Name,
                role
            },
            botSays = new
            {
                trumpSuit = room.TrumpSuit?.ToString(),
                myCards = bot.Hand.Select(c => c.Code).ToList(),
                request = role switch
                {
                    "defense" => "I am defending. Which legal card should I use to beat the attack, or should I take if no legal card exists?",
                    "attack" => "I am attacking. Which legal card should I lead with?",
                    "throw-in" => "I can throw in. Which legal card should I add, or should I pass if throwing is bad?",
                    _ => "What is the best legal move?"
                }
            },
            strictRules = new[]
            {
                "Choose only from legalCardCodes.",
                "Do not invent cards.",
                "Do not assume unknown deck cards.",
                "Use only myCards, table, players card counts, and memory.",
                "Memory contains visible events: cards beaten, discarded, taken, and prior advice.",
                "Prefer saving trump cards unless necessary.",
                "If several moves are similar, prefer the lowest non-trump card."
            },
            legalCardCodes = legalCards.Select(c => c.Code).ToList(),
            table = room.Table.Select(p => new { attack = p.Attack.Code, defense = p.Defense?.Code }).ToList(),
            players = room.Players.Select(p => new { p.Name, p.IsBot, cardCount = p.Hand.Count, p.Status }).ToList(),
            privateBotMemory = bot.BotMemory.TakeLast(120).ToList(),
            sharedVisibleMemory = room.CardMemoryLog.TakeLast(80).ToList()
        };

        return JsonSerializer.Serialize(state);
    }

    private static void TrimMemory(Player bot)
    {
        const int maxItems = 240;
        if (bot.BotMemory.Count <= maxItems) return;
        bot.BotMemory.RemoveRange(0, bot.BotMemory.Count - maxItems);
    }
}
