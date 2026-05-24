using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class ConnectionRecoveryService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public (Room room, Player player) RecoverByPlayerId(string roomCode, string playerId, string connectionId)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            var player = room.Players.FirstOrDefault(p => string.Equals(p.Id, playerId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Игрок не найден.");

            if (player.Status == PlayerStatus.Eliminated)
                throw new InvalidOperationException("Игрок уже исключён из игры.");

            player.ConnectionId = connectionId;
            player.Status = PlayerStatus.Connected;
            player.DisconnectedAtUtc = null;
            room.Log = $"{player.Name} восстановил соединение.";
            return (room, player);
        }
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
