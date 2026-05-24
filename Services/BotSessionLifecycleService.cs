using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotSessionLifecycleService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public void ContinueBotsWithHuman(string roomCode)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            if (room.Phase != GamePhase.Finished) return;

            foreach (var bot in room.Players.Where(p => p.IsBot && p.Status == PlayerStatus.Connected))
            {
                room.ContinuePlayerIds.Add(bot.Id);
            }

            if (room.Players.Any(p => p.IsBot))
                room.Log = "ИИ автоматически готовы продолжить новую партию.";
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

            var humans = room.Players.Where(p => !p.IsBot && p.Status != PlayerStatus.Eliminated).ToList();
            if (humans.Count == 0)
            {
                Rooms.Remove(room.Code);
                return false;
            }

            if (room.Phase == GamePhase.Finished && humans.Count == 1)
            {
                Rooms.Remove(room.Code);
                return false;
            }

            room.Log = "ИИ удалены из комнаты. Их память и история очищены.";
            return true;
        }
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
