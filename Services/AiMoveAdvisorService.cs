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
            var limits = BuildRoundLimits(room);
            bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: {bot.Name} asks AI as {role}. Legal cards: {string.Join(", ", legalCards.Select(c => c.Code))}. Hand: {string.Join(", ", bot.Hand.Select(c => c.Code))}. Trump: {room.TrumpSuit}. Deck: {room.Deck.Count}. Defender cards: {limits.DefenderCardCount}. Attacks on table: {limits.AttackCardsOnTable}. Can add attacks: {limits.CanAddMoreAttackCards}.");

            var payload = JsonSerializer.Serialize(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.15, maxOutputTokens = 180, responseMimeType = "application/json" }
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
            var code = answer.RootElement.TryGetProperty("cardCode", out var cardCode) && cardCode.ValueKind != JsonValueKind.Null ? cardCode.GetString() : null;
            var reason = answer.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
            var wantsPass = string.IsNullOrWhiteSpace(code) || string.Equals(code, "pass", StringComparison.OrdinalIgnoreCase);
            var legal = !wantsPass && legalCards.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

            bot.BotMemory.Add($"{DateTime.UtcNow:HH:mm:ss}: AI advised {(wantsPass ? "pass" : code ?? "none")}. Reason: {reason ?? "not provided"}. Valid card: {legal}.");
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
        var limits = BuildRoundLimits(room);
        var state = new
        {
            task = "You are a separate Gemini chat for this Durak bot. Choose one legal move. Return only JSON: {\"cardCode\":\"...\",\"reason\":\"short reason\"}. For attack/defense choose a card from legalCardCodes. For throw-in you may pass by returning {\"cardCode\":null,\"reason\":\"pass to save cards\"}.",
            botIdentity = new
            {
                bot.Id,
                bot.Name,
                role
            },
            botSays = new
            {
                trumpSuit = room.TrumpSuit?.ToString(),
                deckCardsLeft = room.Deck.Count,
                myCards = bot.Hand.Select(c => c.Code).ToList(),
                request = role switch
                {
                    "defense" => "I am defending. Choose the cheapest legal card that beats the attack. Save trump cards if a non-trump defense works.",
                    "attack" => "I am leading an attack. Prefer low non-trump cards. Avoid starting with a trump while many deck cards remain unless it helps me finish soon.",
                    "throw-in" => "It is my throw-in turn. I may throw one legal card or pass. Avoid wasting trump cards while many deck cards remain. Pass if the only useful throw-in is an expensive trump and the defender is not near losing.",
                    _ => "What is the best legal move?"
                }
            },
            strictRules = new[]
            {
                "Choose only from legalCardCodes, or choose null only when role is throw-in and passing is strategically better.",
                "Do not invent cards.",
                "Do not assume unknown deck cards.",
                "You only know your own hand. Other players' hands are hidden; you only know their card counts.",
                "Use only myCards, table, players card counts, roundLimits, deckCardsLeft, and memory.",
                "Memory contains visible events: cards beaten, discarded, taken, and prior advice.",
                "When attacking early or mid-game, prefer the lowest non-trump card.",
                "Do not lead or throw in trump cards while deckCardsLeft is high unless there is a clear tactical reason.",
                "When throwing in, pass instead of wasting trump if deckCardsLeft is high and defender has several cards.",
                "Pressure harder when defenderCardCount is low, deckCardsLeft is low, or this can help you finish your hand.",
                "If several moves are similar, prefer the lowest non-trump card."
            },
            legalCardCodes = legalCards.Select(c => c.Code).ToList(),
            roundLimits = limits,
            table = room.Table.Select(p => new { attack = p.Attack.Code, defense = p.Defense?.Code }).ToList(),
            players = room.Players.Select(p => new { p.Name, p.IsBot, cardCount = p.Hand.Count, p.Status }).ToList(),
            privateBotMemory = bot.BotMemory.TakeLast(120).ToList(),
            sharedVisibleMemory = room.CardMemoryLog.TakeLast(80).ToList()
        };

        return JsonSerializer.Serialize(state);
    }

    private static RoundLimits BuildRoundLimits(Room room)
    {
        var defender = room.DefenderIndex >= 0 && room.DefenderIndex < room.Players.Count
            ? room.Players[room.DefenderIndex]
            : null;
        var attackCardsOnTable = room.Table.Count;
        var defendedCardsOnTable = room.Table.Count(p => p.Defense is not null);
        var defenderCardCount = defender?.Hand.Count ?? 0;
        var maxTotalAttackCardsAgainstDefender = defenderCardCount + defendedCardsOnTable;

        return new RoundLimits(
            DefenderName: defender?.Name,
            DefenderIsBot: defender?.IsBot,
            DefenderCardCount: defenderCardCount,
            AttackCardsOnTable: attackCardsOnTable,
            DefendedCardsOnTable: defendedCardsOnTable,
            MaxTotalAttackCardsAgainstDefender: maxTotalAttackCardsAgainstDefender,
            RemainingAttackSlotsAgainstDefender: Math.Max(0, maxTotalAttackCardsAgainstDefender - attackCardsOnTable),
            CanAddMoreAttackCards: attackCardsOnTable < maxTotalAttackCardsAgainstDefender);
    }

    private static void TrimMemory(Player bot)
    {
        const int maxItems = 240;
        if (bot.BotMemory.Count <= maxItems) return;
        bot.BotMemory.RemoveRange(0, bot.BotMemory.Count - maxItems);
    }

    private sealed record RoundLimits(
        string? DefenderName,
        bool? DefenderIsBot,
        int DefenderCardCount,
        int AttackCardsOnTable,
        int DefendedCardsOnTable,
        int MaxTotalAttackCardsAgainstDefender,
        int RemainingAttackSlotsAgainstDefender,
        bool CanAddMoreAttackCards);
}
