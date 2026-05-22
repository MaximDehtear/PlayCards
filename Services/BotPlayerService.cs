using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotPlayerService(GameRoomService games, ILogger<BotPlayerService> logger)
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
            if (room.Players.Count(p => p.IsBot && p.Status != PlayerStatus.Eliminated) >= 3) throw new InvalidOperationException("Можно добавить максимум 3 ИИ-игрока.");
            if (room.Players.Count(p => p.Status != PlayerStatus.Eliminated) >= 6) throw new InvalidOperationException("Комната заполнена.");

            room.BotSequence++;
            var bot = new Player
            {
                Name = $"ИИ {room.BotSequence}",
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
                if (room.Players.Count == 0 || room.AttackerIndex < 0 || room.DefenderIndex < 0) continue;
                if (room.AttackerIndex >= room.Players.Count || room.DefenderIndex >= room.Players.Count) continue;

                var defender = room.Players[room.DefenderIndex];
                if (defender.IsBot && defender.Status == PlayerStatus.Connected)
                {
                    var undefended = room.Table.FirstOrDefault(p => p.Defense is null);
                    if (undefended is not null)
                    {
                        var defense = defender.Hand
                            .Where(c => CanBeat(undefended.Attack, c, room.TrumpSuit!.Value))
                            .OrderBy(c => DefenseCost(c, room.TrumpSuit!.Value))
                            .FirstOrDefault();
                        if (defense is not null) return new BotAction(room.Code, defender.Id, BotActionKind.Defend, defense.Code, undefended.Attack.Code);
                        RememberTableDestination(room, defender, "забрал");
                        return new BotAction(room.Code, defender.Id, BotActionKind.Take);
                    }
                }

                var attacker = room.Players[room.AttackerIndex];
                if (attacker.IsBot && attacker.Status == PlayerStatus.Connected)
                {
                    var attack = ChooseAttack(room, attacker);
                    if (attack is not null) return new BotAction(room.Code, attacker.Id, BotActionKind.Attack, attack.Code);
                    if (room.Table.Count > 0)
                    {
                        RememberTableDestination(room, attacker, "ушла в сброс после бито");
                        return new BotAction(room.Code, attacker.Id, BotActionKind.Pass);
                    }
                }

                foreach (var bot in room.Players.Where(p => p.IsBot && p.Status == PlayerStatus.Connected && p.Id != defender.Id && p.Id != attacker.Id))
                {
                    if (room.Table.Count == 0 || room.PassedPlayerIds.Contains(bot.Id)) continue;
                    var card = ChooseThrowIn(room, bot);
                    if (card is not null) return new BotAction(room.Code, bot.Id, BotActionKind.Attack, card.Code);
                    return new BotAction(room.Code, bot.Id, BotActionKind.Pass);
                }
            }
        }
        return null;
    }

    private void Execute(BotAction action)
    {
        switch (action.Kind)
        {
            case BotActionKind.Attack:
                games.Attack(action.RoomCode, action.PlayerId, action.CardCode!);
                RememberPlayedCard(action.RoomCode, action.PlayerId, action.CardCode!, "сыграл на стол");
                break;
            case BotActionKind.Defend:
                games.Defend(action.RoomCode, action.PlayerId, action.TargetAttackCode!, action.CardCode!);
                RememberPlayedCard(action.RoomCode, action.PlayerId, action.CardCode!, "отбился");
                break;
            case BotActionKind.Take:
                games.Take(action.RoomCode, action.PlayerId);
                break;
            case BotActionKind.Pass:
                games.Pass(action.RoomCode, action.PlayerId);
                break;
        }
    }

    private Card? ChooseAttack(Room room, Player bot)
    {
        if (room.Table.Count == 0)
        {
            return bot.Hand.OrderBy(c => c.Suit == room.TrumpSuit ? 1 : 0).ThenBy(c => c.Rank).FirstOrDefault();
        }
        return ChooseThrowIn(room, bot);
    }

    private static Card? ChooseThrowIn(Room room, Player bot)
    {
        var ranks = room.Table.Select(p => p.Attack.Rank).Concat(room.Table.Where(p => p.Defense is not null).Select(p => p.Defense!.Rank)).ToHashSet();
        return bot.Hand.Where(c => ranks.Contains(c.Rank)).OrderBy(c => c.Suit == room.TrumpSuit ? 1 : 0).ThenBy(c => c.Rank).FirstOrDefault();
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

    private readonly record struct BotAction(string RoomCode, string PlayerId, BotActionKind Kind, string? CardCode = null, string? TargetAttackCode = null);
    private enum BotActionKind { Attack, Defend, Take, Pass }
}
