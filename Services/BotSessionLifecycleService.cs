using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotSessionLifecycleService(GameRoomService games)
{
    private readonly Random _random = new();
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public void CleanupStaleFinishedSessions()
    {
        lock (Sync)
        {
            foreach (var room in Rooms.Values.ToList())
            {
                if (IsStaleFinishedSession(room))
                {
                    DestroyRoom(room);
                }
            }
        }
    }

    public bool ContinueHumanAndBots(string roomCode, string playerId)
    {
        lock (Sync)
        {
            CleanupStaleFinishedSessionsUnsafe();
            var room = GetRoom(roomCode);
            if (IsStaleFinishedSession(room))
            {
                DestroyRoom(room);
                return false;
            }
            if (room.Phase != GamePhase.Finished) throw new InvalidOperationException("Партия ещё не завершена.");

            var player = room.Players.FirstOrDefault(p => p.Id == playerId)
                ?? throw new InvalidOperationException("Игрок не найден.");
            if (player.IsBot) throw new InvalidOperationException("Продолжить должен живой игрок, не бот.");
            if (player.Status != PlayerStatus.Connected) throw new InvalidOperationException("Продолжить может только подключённый игрок.");

            ResetBotRoundMemory(room);
            room.ContinuePlayerIds.Clear();
            room.ContinuePlayerIds.Add(player.Id);
            foreach (var bot in room.Players.Where(p => p.IsBot && p.Status == PlayerStatus.Connected))
            {
                room.ContinuePlayerIds.Add(bot.Id);
            }

            var continuingIds = room.Players
                .Where(p => room.ContinuePlayerIds.Contains(p.Id) && p.Status == PlayerStatus.Connected)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (continuingIds.Count < 2)
            {
                DestroyRoom(room);
                return false;
            }

            StartBotRematchRound(room, continuingIds);
            return Rooms.ContainsKey(roomCode);
        }
    }

    public void ContinueBotsWithHuman(string roomCode)
    {
        lock (Sync)
        {
            CleanupStaleFinishedSessionsUnsafe();
            var room = GetRoom(roomCode);
            if (room.Phase != GamePhase.Finished) return;

            ResetBotRoundMemory(room);
            foreach (var bot in room.Players.Where(p => p.IsBot && p.Status == PlayerStatus.Connected))
            {
                room.ContinuePlayerIds.Add(bot.Id);
            }

            if (room.Players.Any(p => p.IsBot))
                room.Log = "ИИ автоматически готовы продолжить новую партию. Память ИИ очищена для новой партии.";
        }
    }

    public bool RemoveBotsAfterHumanLeaves(string roomCode)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            var bots = room.Players.Where(p => p.IsBot).ToList();
            foreach (var bot in bots)
            {
                bot.Hand.Clear();
                bot.BotMemory.Clear();
                room.ContinuePlayerIds.Remove(bot.Id);
                room.PassedPlayerIds.Remove(bot.Id);
                room.Players.Remove(bot);
            }

            room.SeenCardCodes.Clear();
            room.CardMemoryLog.Clear();

            if (IsStaleFinishedSession(room))
            {
                DestroyRoom(room);
                return false;
            }

            var humans = room.Players.Where(p => !p.IsBot && p.Status != PlayerStatus.Eliminated).ToList();
            if (humans.Count == 0)
            {
                DestroyRoom(room);
                return false;
            }

            if (room.Phase == GamePhase.Finished && humans.Count == 1)
            {
                DestroyRoom(room);
                return false;
            }

            room.Log = "ИИ удалены из комнаты. Их память и история очищены.";
            return true;
        }
    }

    private void CleanupStaleFinishedSessionsUnsafe()
    {
        foreach (var room in Rooms.Values.ToList())
        {
            if (IsStaleFinishedSession(room)) DestroyRoom(room);
        }
    }

    private static bool IsStaleFinishedSession(Room room)
    {
        if (room.Phase != GamePhase.Finished) return false;
        var livingHumans = room.Players.Count(p => !p.IsBot && p.Status != PlayerStatus.Eliminated);
        return livingHumans == 0;
    }

    private void DestroyRoom(Room room)
    {
        foreach (var player in room.Players)
        {
            player.Hand.Clear();
            player.BotMemory.Clear();
        }
        room.Table.Clear();
        room.PassedPlayerIds.Clear();
        room.ContinuePlayerIds.Clear();
        room.SeenCardCodes.Clear();
        room.CardMemoryLog.Clear();
        Rooms.Remove(room.Code);
    }

    private void StartBotRematchRound(Room room, HashSet<string> playerIds)
    {
        room.Players = room.Players
            .Where(p => playerIds.Contains(p.Id) && p.Status == PlayerStatus.Connected)
            .ToList();

        if (room.Players.Count < 2)
        {
            DestroyRoom(room);
            return;
        }

        room.Phase = GamePhase.Playing;
        room.Deck = CreateDeck().OrderBy(_ => _random.Next()).ToList();
        room.TrumpCard = room.Deck.LastOrDefault();
        room.TrumpSuit = room.TrumpCard?.Suit;
        room.Table.Clear();
        room.PassedPlayerIds.Clear();
        room.ContinuePlayerIds.Clear();
        room.SeenCardCodes.Clear();
        room.CardMemoryLog.Clear();
        room.RematchDeadlineUtc = null;

        foreach (var player in room.Players)
        {
            player.Hand.Clear();
            player.DisconnectedAtUtc = null;
            player.Status = PlayerStatus.Connected;
            if (player.IsBot) player.BotMemory.Clear();
            DrawUpToSix(room, player);
        }

        room.AttackerIndex = Math.Max(0, FindLowestTrumpOwner(room));
        room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);
        room.Log = $"Новая партия началась. Козырь: {room.TrumpCard?.Label}. Ходит {room.Players[room.AttackerIndex].Name}.";
    }

    private static void ResetBotRoundMemory(Room room)
    {
        room.SeenCardCodes.Clear();
        room.CardMemoryLog.Clear();
        room.PassedPlayerIds.Clear();
        foreach (var bot in room.Players.Where(p => p.IsBot))
        {
            bot.BotMemory.Clear();
        }
    }

    private static List<Card> CreateDeck() => Enum.GetValues<Suit>().SelectMany(s => Enum.GetValues<Rank>().Select(r => new Card(s, r))).ToList();

    private static void DrawUpToSix(Room room, Player player)
    {
        while (player.Hand.Count < 6 && room.Deck.Count > 0)
        {
            player.Hand.Add(room.Deck[0]);
            room.Deck.RemoveAt(0);
        }
    }

    private static int FindLowestTrumpOwner(Room room)
    {
        var trump = room.TrumpSuit!.Value;
        var candidate = room.Players
            .Select((p, i) => new { Player = p, Index = i, Card = p.Hand.Where(c => c.Suit == trump).OrderBy(c => c.Rank).FirstOrDefault() })
            .Where(x => x.Player.Status == PlayerStatus.Connected && x.Card is not null)
            .OrderBy(x => x.Card!.Rank)
            .FirstOrDefault();
        return candidate?.Index ?? Math.Max(0, room.Players.FindIndex(p => p.Status == PlayerStatus.Connected));
    }

    private static int NextActiveIndex(Room room, int from)
    {
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (from + step) % room.Players.Count;
            if (room.Players[idx].Status == PlayerStatus.Connected) return idx;
        }
        return from;
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
