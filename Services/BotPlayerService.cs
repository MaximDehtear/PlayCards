using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotPlayerService(GameRoomService games, TurnRulesService rules, AiMoveAdvisorService ai, ILogger<BotPlayerService> logger)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public Room AddBot(string roomCode)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            if (room.Phase != GamePhase.Lobby) throw new InvalidOperationException("Ботов можно добавлять только до старта игры.");
            if (room.Players.Count(p => p.Status != PlayerStatus.Eliminated) >= 6) throw new InvalidOperationException("Комната заполнена. Максимум 6 игроков вместе с ИИ.");

            room.BotSequence++;
            var bot = new Player
            {
                Name = $"Бот {room.BotSequence}",
                IsBot = true,
                Status = PlayerStatus.Connected
            };
            room.Players.Add(bot);
            room.Log = $"{bot.Name} добавлен в комнату.";
            return room;
        }
    }

    public IReadOnlyList<string> RunBotTurns()
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var safety = 0; safety < 64; safety++)
        {
            var action = FindNextBotAction();
            if (action is null) break;
            try
            {
                Execute(action.Value);
                changed.Add(action.Value.RoomCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bot action failed in room {RoomCode}", action.Value.RoomCode);
                changed.Add(action.Value.RoomCode);
                break;
            }
        }
        return changed.ToList();
    }

    private BotAction? FindNextBotAction()
    {
        lock (Sync)
        {
            foreach (var room in Rooms.Values.Where(r => r.Phase == GamePhase.Playing))
            {
                rules.NormalizeRoom(room.Code);
                if (room.Phase != GamePhase.Playing) continue;
                if (room.Players.Count == 0 || room.AttackerIndex < 0 || room.DefenderIndex < 0) continue;
                if (room.AttackerIndex >= room.Players.Count || room.DefenderIndex >= room.Players.Count) continue;

                var defender = room.Players[room.DefenderIndex];
                var thrower = room.Players[room.AttackerIndex];

                if (defender.IsBot && defender.Status == PlayerStatus.Connected)
                {
                    var undefended = room.Table.FirstOrDefault(p => p.Defense is null);
                    if (undefended is not null)
                    {
                        var legalDefenses = defender.Hand
                            .Where(c => CanBeat(undefended.Attack, c, room.TrumpSuit!.Value))
                            .OrderBy(c => DefenseCost(c, room.TrumpSuit!.Value))
                            .ToList();

                        var aiDefense = ai.ChooseCard(room, defender, "defense", legalDefenses);
                        SetBotKindName(room, defender, aiDefense.UsedAi);
                        var defense = legalDefenses.FirstOrDefault(c => string.Equals(c.Code, aiDefense.CardCode, StringComparison.OrdinalIgnoreCase))
                            ?? legalDefenses.FirstOrDefault();

                        if (defense is not null) return new BotAction(room.Code, defender.Id, BotActionKind.Defend, defense.Code, undefended.Attack.Code);
                        RememberTableDestination(room, defender, "забрал");
                        return new BotAction(room.Code, defender.Id, BotActionKind.Take);
                    }
                }

                if (room.Table.Any(p => p.Defense is null)) continue;
                if (ShouldWaitForHumanThrowIn(room, defender)) return null;

                if (thrower.IsBot && thrower.Status == PlayerStatus.Connected && !room.PassedPlayerIds.Contains(thrower.Id))
                {
                    var attack = ChooseAttack(room, thrower);
                    if (attack.Card is not null) return new BotAction(room.Code, thrower.Id, BotActionKind.Attack, attack.Card.Code);
                    if (room.Table.Count > 0)
                    {
                        RememberTableDestination(room, thrower, "ушла в сброс после бито");
                        return new BotAction(room.Code, thrower.Id, BotActionKind.Pass);
                    }
                }
            }
        }
        return null;
    }

    private static bool ShouldWaitForHumanThrowIn(Room room, Player defender)
    {
        if (room.Table.Count == 0) return false;
        if (room.Table.Any(p => p.Defense is null)) return false;
        if (room.AttackerIndex < 0 || room.AttackerIndex >= room.Players.Count) return false;

        var thrower = room.Players[room.AttackerIndex];
        return !thrower.IsBot &&
            thrower.Status == PlayerStatus.Connected &&
            thrower.Id != defender.Id &&
            !room.PassedPlayerIds.Contains(thrower.Id) &&
            HasLegalThrowIn(room, thrower);
    }

    private static bool HasLegalThrowIn(Room room, Player player)
    {
        if (room.Table.Count == 0 || player.Hand.Count == 0 || !TurnRulesService.CanAddAttackCard(room)) return false;
        var ranks = room.Table.Select(p => p.Attack.Rank)
            .Concat(room.Table.Where(p => p.Defense is not null).Select(p => p.Defense!.Rank))
            .ToHashSet();
        return player.Hand.Any(c => ranks.Contains(c.Rank));
    }

    private void Execute(BotAction action)
    {
        switch (action.Kind)
        {
            case BotActionKind.Attack:
                rules.EnsureCanAttack(action.RoomCode, action.PlayerId);
                games.Attack(action.RoomCode, action.PlayerId, action.CardCode!);
                RememberPlayedCard(action.RoomCode, action.PlayerId, action.CardCode!, "сыграл на стол");
                break;
            case BotActionKind.Defend:
                games.Defend(action.RoomCode, action.PlayerId, action.TargetAttackCode!, action.CardCode!);
                RememberPlayedCard(action.RoomCode, action.PlayerId, action.CardCode!, "отбился");
                break;
            case BotActionKind.Take:
                games.Take(action.RoomCode, action.PlayerId);
                rules.NormalizeRoom(action.RoomCode);
                break;
            case BotActionKind.Pass:
                games.Pass(action.RoomCode, action.PlayerId);
                rules.NormalizeRoom(action.RoomCode);
                break;
        }
    }

    private BotChoice ChooseAttack(Room room, Player bot)
    {
        if (bot.Hand.Count == 0) return new BotChoice(null, false);
        if (room.Table.Count == 0)
        {
            var legal = OrderedAttackLeadCards(room, bot).ToList();
            var advice = ai.ChooseCard(room, bot, "attack", legal);
            SetBotKindName(room, bot, advice.UsedAi);
            var aiChoice = legal.FirstOrDefault(c => string.Equals(c.Code, advice.CardCode, StringComparison.OrdinalIgnoreCase));
            var card = IsStrategicAttackChoice(room, bot, aiChoice) ? aiChoice : legal.FirstOrDefault();
            return new BotChoice(card, advice.UsedAi && card is not null && string.Equals(card.Code, advice.CardCode, StringComparison.OrdinalIgnoreCase));
        }

        return ChooseThrowIn(room, bot);
    }

    private BotChoice ChooseThrowIn(Room room, Player bot)
    {
        if (bot.Hand.Count == 0 || !TurnRulesService.CanAddAttackCard(room)) return new BotChoice(null, false);
        var ranks = room.Table.Select(p => p.Attack.Rank)
            .Concat(room.Table.Where(p => p.Defense is not null).Select(p => p.Defense!.Rank))
            .ToHashSet();

        var legal = bot.Hand
            .Where(c => ranks.Contains(c.Rank))
            .OrderBy(c => ThrowInCost(room, bot, c))
            .ThenBy(c => c.Rank)
            .ToList();

        if (ShouldPassInsteadOfThrowing(room, bot, legal))
        {
            SetBotKindName(room, bot, false);
            return new BotChoice(null, false);
        }

        var advice = ai.ChooseCard(room, bot, "throw-in", legal);
        SetBotKindName(room, bot, advice.UsedAi);
        var aiChoice = legal.FirstOrDefault(c => string.Equals(c.Code, advice.CardCode, StringComparison.OrdinalIgnoreCase));
        var card = IsStrategicThrowInChoice(room, bot, aiChoice, legal) ? aiChoice : legal.FirstOrDefault();
        return new BotChoice(card, advice.UsedAi && card is not null && string.Equals(card.Code, advice.CardCode, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Card> OrderedAttackLeadCards(Room room, Player bot)
    {
        var deckIsLarge = room.Deck.Count >= 12;
        return bot.Hand
            .OrderBy(c => AttackLeadCost(room, bot, c, deckIsLarge))
            .ThenBy(c => c.Rank);
    }

    private static int AttackLeadCost(Room room, Player bot, Card card, bool deckIsLarge)
    {
        var isTrump = card.Suit == room.TrumpSuit;
        var sameRankCount = bot.Hand.Count(c => c.Rank == card.Rank);
        var sameRankTrumpCount = bot.Hand.Count(c => c.Rank == card.Rank && c.Suit == room.TrumpSuit);
        var cost = (int)card.Rank;

        if (isTrump) cost += deckIsLarge ? 500 : 140;
        if (sameRankCount >= 2) cost -= 8;
        if (deckIsLarge && sameRankTrumpCount > 0) cost += 40;
        if (bot.Hand.Count <= 3 && !deckIsLarge) cost -= 20;

        return cost;
    }

    private static int ThrowInCost(Room room, Player bot, Card card)
    {
        var deckIsLarge = room.Deck.Count >= 10;
        var defender = room.DefenderIndex >= 0 && room.DefenderIndex < room.Players.Count ? room.Players[room.DefenderIndex] : null;
        var isTrump = card.Suit == room.TrumpSuit;
        var cost = (int)card.Rank;

        if (isTrump) cost += deckIsLarge ? 650 : 180;
        if (defender is not null && defender.Hand.Count <= 2) cost -= 35;
        if (bot.Hand.Count <= 2 && room.Deck.Count == 0) cost -= 60;

        return cost;
    }

    private static bool ShouldPassInsteadOfThrowing(Room room, Player bot, IReadOnlyList<Card> legal)
    {
        if (legal.Count == 0) return true;
        var defender = room.DefenderIndex >= 0 && room.DefenderIndex < room.Players.Count ? room.Players[room.DefenderIndex] : null;
        var deckIsLarge = room.Deck.Count >= 10;
        var onlyTrump = legal.All(c => c.Suit == room.TrumpSuit);
        var hasNonTrump = legal.Any(c => c.Suit != room.TrumpSuit);

        if (deckIsLarge && onlyTrump) return true;
        if (deckIsLarge && hasNonTrump && legal.First().Suit == room.TrumpSuit) return true;
        if (defender is not null && defender.Hand.Count <= 1) return false;
        if (bot.Hand.Count <= 2 && room.Deck.Count == 0) return false;

        var alreadyPressed = room.Table.Count >= 3;
        if (deckIsLarge && alreadyPressed) return true;

        return false;
    }

    private static bool IsStrategicAttackChoice(Room room, Player bot, Card? card)
    {
        if (card is null) return false;
        if (card.Suit != room.TrumpSuit) return true;
        return room.Deck.Count <= 6 || bot.Hand.Count <= 2;
    }

    private static bool IsStrategicThrowInChoice(Room room, Player bot, Card? card, IReadOnlyList<Card> legal)
    {
        if (card is null) return false;
        if (card.Suit != room.TrumpSuit) return true;
        if (legal.Any(c => c.Suit != room.TrumpSuit)) return false;
        return room.Deck.Count <= 6 || bot.Hand.Count <= 2;
    }

    private static void SetBotKindName(Room room, Player bot, bool usedAi)
    {
        if (!bot.IsBot) return;
        var index = Math.Max(1, room.Players.Where(p => p.IsBot).ToList().FindIndex(p => p.Id == bot.Id) + 1);
        var expectedPrefix = usedAi ? "ИИ" : "Бот";
        bot.Name = $"{expectedPrefix} {index}";
    }

    private static bool CanBeat(Card attack, Card defense, Suit trump) =>
        defense.Suit == attack.Suit && defense.Rank > attack.Rank || defense.Suit == trump && attack.Suit != trump;

    private static int DefenseCost(Card card, Suit trump) => (card.Suit == trump ? 100 : 0) + (int)card.Rank;

    private void RememberPlayedCard(string roomCode, string playerId, string cardCode, string destination)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            var player = room.Players.FirstOrDefault(p => p.Id == playerId);
            var text = $"{DateTime.UtcNow:HH:mm:ss}: {cardCode} — {player?.Name ?? "игрок"} {destination}";
            room.SeenCardCodes.Add(cardCode);
            room.CardMemoryLog.Add(text);
            foreach (var bot in room.Players.Where(p => p.IsBot)) bot.BotMemory.Add(text);
        }
    }

    private static void RememberTableDestination(Room room, Player bot, string destination)
    {
        foreach (var pair in room.Table)
        {
            var cards = pair.Defense is null ? new[] { pair.Attack } : new[] { pair.Attack, pair.Defense };
            foreach (var card in cards)
            {
                var text = $"{DateTime.UtcNow:HH:mm:ss}: {card.Code} — {destination} ({bot.Name})";
                room.SeenCardCodes.Add(card.Code);
                room.CardMemoryLog.Add(text);
                foreach (var b in room.Players.Where(p => p.IsBot)) b.BotMemory.Add(text);
            }
        }
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");

    private readonly record struct BotChoice(Card? Card, bool UsedAi);
    private readonly record struct BotAction(string RoomCode, string PlayerId, BotActionKind Kind, string? CardCode = null, string? TargetAttackCode = null);
    private enum BotActionKind { Attack, Defend, Take, Pass }
}
