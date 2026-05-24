using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
    public IReadOnlyList<RoomSummary> GetRooms()
    {
        lock (_sync)
        {
            return _rooms.Values
                .OrderByDescending(r => r.CreatedUtc)
                .Select(r => new RoomSummary(r.Code, r.Name, r.Players.Count(p => p.Status != PlayerStatus.Eliminated), MaxPlayers, r.Phase))
                .ToList();
        }
    }

    public IReadOnlyList<(string PlayerId, string ConnectionId)> GetActiveConnections(string roomCode)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            return room.Players
                .Where(p => p.Status != PlayerStatus.Eliminated && !string.IsNullOrWhiteSpace(p.ConnectionId))
                .Select(p => (p.Id, p.ConnectionId!))
                .ToList();
        }
    }

    public (Room room, Player player) CreateRoom(string roomName, string playerName, string connectionId)
    {
        lock (_sync)
        {
            var code = CreateRoomCode();
            var player = new Player { Name = CleanName(playerName), ConnectionId = connectionId };
            var room = new Room
            {
                Code = code,
                Name = string.IsNullOrWhiteSpace(roomName) ? $"Комната {code}" : roomName.Trim(),
                Players = [player]
            };
            _rooms[code] = room;
            return (room, player);
        }
    }

    public (Room room, Player player) JoinRoom(string roomCode, string playerName, string connectionId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var cleanName = CleanName(playerName);

            if (room.Phase == GamePhase.Playing)
            {
                var reconnectByName = room.Players.FirstOrDefault(p =>
                    p.Status == PlayerStatus.Disconnected &&
                    string.Equals(p.Name, cleanName, StringComparison.OrdinalIgnoreCase) &&
                    IsWithinGracePeriod(p));

                if (reconnectByName is not null)
                {
                    ReconnectPlayer(room, reconnectByName, connectionId);
                    return (room, reconnectByName);
                }

                throw new InvalidOperationException("Игра уже началась. Новые игроки не могут войти, но отключённый игрок может переподключиться в течение 2 минут.");
            }

            if (room.Phase == GamePhase.Finished) throw new InvalidOperationException("Игра завершена. Дождись новой партии или создай комнату.");
            if (room.Players.Count(p => p.Status != PlayerStatus.Eliminated) >= MaxPlayers) throw new InvalidOperationException("Комната заполнена.");

            var player = new Player { Name = cleanName, ConnectionId = connectionId };
            room.Players.Add(player);
            room.Log = $"{player.Name} вошёл в комнату.";
            return (room, player);
        }
    }

    public (Room room, Player player) ReconnectRoom(string roomCode, string playerId, string connectionId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var player = GetPlayer(room, playerId);
            if (player.Status == PlayerStatus.Eliminated) throw new InvalidOperationException("Игрок уже исключён из игры.");
            if (player.Status == PlayerStatus.Disconnected && !IsWithinGracePeriod(player)) throw new InvalidOperationException("Время на переподключение истекло.");
            ReconnectPlayer(room, player, connectionId);
            return (room, player);
        }
    }

    public bool LeaveRoom(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var player = GetPlayer(room, playerId);

            if (room.Phase == GamePhase.Playing)
            {
                EliminateDisconnectedPlayer(room, player, "вышел из комнаты");
                return _rooms.ContainsKey(room.Code);
            }

            room.ContinuePlayerIds.Remove(player.Id);
            room.Players.Remove(player);
            room.Log = $"{player.Name} вышел из комнаты.";

            if (room.Players.Count == 0)
            {
                _rooms.Remove(room.Code);
                return false;
            }

            if (room.Phase == GamePhase.Finished && room.Players.Count(p => p.Status == PlayerStatus.Connected) <= 1)
            {
                _rooms.Remove(room.Code);
                return false;
            }

            return true;
        }
    }

    public string? FindPlayerRoomByConnection(string connectionId, out string? playerId)
    {
        lock (_sync)
        {
            foreach (var room in _rooms.Values)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player is not null)
                {
                    playerId = player.Id;
                    return room.Code;
                }
            }
            playerId = null;
            return null;
        }
    }

    public void Disconnect(string connectionId)
    {
        lock (_sync)
        {
            foreach (var room in _rooms.Values)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player is not null && player.Status != PlayerStatus.Eliminated)
                {
                    player.ConnectionId = null;
                    player.Status = PlayerStatus.Disconnected;
                    player.DisconnectedAtUtc = DateTime.UtcNow;
                    room.Log = $"{player.Name} отключился. У него есть 2 минуты на возврат.";
                    return;
                }
            }
        }
    }
}
