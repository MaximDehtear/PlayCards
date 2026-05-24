using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotSessionLifecycleService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");
    private readonly MethodInfo _startNewRoundMethod = typeof(GameRoomService).GetMethod("StartNewRound", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService.StartNewRound method was not found.");

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

            StartNewRound(room, continuingIds);
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

    private void StartNewRound(Room room, HashSet<string> playerIds)
    {
        _startNewRoundMethod.Invoke(games, new object[] { room, playerIds });
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

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
