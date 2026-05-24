using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class TurnRulesService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public void NormalizeRoom(string roomCode)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            NormalizeRoom(room);
        }
    }

    public void EnsureCanAttack(string roomCode, string playerId)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            if (room.Phase != GamePhase.Playing) return;
            NormalizeRoom(room);

            var player = room.Players.FirstOrDefault(p => p.Id == playerId)
                ?? throw new InvalidOperationException("Игрок не найден.");

            if (player.Hand.Count == 0)
                throw new InvalidOperationException("Игрок без карт не может атаковать.");

            if (room.Table.Count > 0 && !CanAddAttackCard(room))
                throw new InvalidOperationException("Нельзя подкинуть больше карт, чем защитник может отбить.");
        }
    }

    public static bool CanStillPlay(Room room, Player player) =>
        player.Status == PlayerStatus.Connected &&
        (room.Deck.Count > 0 || player.Hand.Count > 0);

    public static bool CanAddAttackCard(Room room)
    {
        if (room.Table.Count == 0) return true;
        if (room.DefenderIndex < 0 || room.DefenderIndex >= room.Players.Count) return false;

        var defender = room.Players[room.DefenderIndex];
        var defendedCards = room.Table.Count(p => p.Defense is not null);
        var maxAttackCards = defender.Hand.Count + defendedCards;
        return room.Table.Count < maxAttackCards;
    }

    private static void NormalizeRoom(Room room)
    {
        if (room.Phase != GamePhase.Playing || room.Players.Count == 0) return;
        if (room.AttackerIndex < 0 || room.AttackerIndex >= room.Players.Count) room.AttackerIndex = 0;
        if (room.DefenderIndex < 0 || room.DefenderIndex >= room.Players.Count) room.DefenderIndex = 0;

        // During an unresolved table, do not replace the defender: the defender must take/pass/finish the defense first.
        if (room.Table.Count > 0) return;

        var playable = room.Players.Where(p => CanStillPlay(room, p)).ToList();
        if (playable.Count <= 1)
        {
            FinishByPlayablePlayers(room, playable);
            return;
        }

        if (!CanStillPlay(room, room.Players[room.AttackerIndex]))
            room.AttackerIndex = NextPlayableIndex(room, room.AttackerIndex);

        if (room.DefenderIndex == room.AttackerIndex || !CanStillPlay(room, room.Players[room.DefenderIndex]))
            room.DefenderIndex = NextPlayableIndex(room, room.AttackerIndex);
    }

    private static int NextPlayableIndex(Room room, int from)
    {
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (from + step) % room.Players.Count;
            if (CanStillPlay(room, room.Players[idx])) return idx;
        }
        return from;
    }

    private static void FinishByPlayablePlayers(Room room, IReadOnlyList<Player> playable)
    {
        room.Phase = GamePhase.Finished;
        room.Table.Clear();
        room.PassedPlayerIds.Clear();
        room.ContinuePlayerIds.Clear();
        room.RematchDeadlineUtc = DateTime.UtcNow.Add(GameRoomService.RematchWaitPeriod);

        if (playable.Count == 1)
        {
            playable[0].Wins++;
            foreach (var player in room.Players.Where(p => p.Id != playable[0].Id && p.Status != PlayerStatus.Eliminated && p.Hand.Count > 0))
                player.Losses++;
            room.Log = $"{playable[0].Name} победил. Остальные игроки без карт ждут новую партию.";
            return;
        }

        room.Log = "Игра завершена: активных игроков не осталось.";
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
