using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class BotRoomReconnectProtectionService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public IReadOnlyList<string> ProtectDisconnectedHumansInBotRooms()
    {
        lock (Sync)
        {
            var changed = new List<string>();
            foreach (var room in Rooms.Values)
            {
                if (room.Phase != GamePhase.Playing && room.Phase != GamePhase.Finished) continue;
                if (!room.Players.Any(p => p.IsBot)) continue;

                var protectedAny = false;
                foreach (var human in room.Players.Where(p => !p.IsBot && p.Status == PlayerStatus.Disconnected))
                {
                    human.DisconnectedAtUtc = DateTime.UtcNow;
                    protectedAny = true;
                }

                if (protectedAny)
                {
                    room.Log = "Живой игрок отключился, но может вернуться. ИИ ждут в комнате.";
                    changed.Add(room.Code);
                }
            }

            return changed;
        }
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
}
