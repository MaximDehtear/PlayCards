using System.Reflection;
using PlayCards.Models;

namespace PlayCards.Services;

public sealed class SmartDefenseService(GameRoomService games)
{
    private readonly FieldInfo _roomsField = typeof(GameRoomService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._rooms field was not found.");
    private readonly FieldInfo _syncField = typeof(GameRoomService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GameRoomService._sync field was not found.");

    public bool TryDefendFirst(string roomCode, string playerId, string defenseCardCode)
    {
        lock (Sync)
        {
            var room = GetRoom(roomCode);
            if (room.Phase != GamePhase.Playing) return false;
            if (room.DefenderIndex < 0 || room.DefenderIndex >= room.Players.Count) return false;

            var defender = room.Players[room.DefenderIndex];
            if (defender.Id != playerId) return false;
            if (defender.Status != PlayerStatus.Connected) return false;

            var pair = room.Table.FirstOrDefault(p => p.Defense is null);
            if (pair is null) return false;

            var defense = defender.Hand.FirstOrDefault(c => string.Equals(c.Code, defenseCardCode, StringComparison.OrdinalIgnoreCase));
            if (defense is null) throw new InvalidOperationException("Карты нет в руке.");
            if (!CanBeat(pair.Attack, defense, room.TrumpSuit!.Value)) throw new InvalidOperationException("Эта карта не бьёт атакующую.");

            defender.Hand.Remove(defense);
            pair.Defense = defense;
            room.Log = $"{defender.Name} отбивает {pair.Attack.Label} картой {defense.Label}.";
            return true;
        }
    }

    private static bool CanBeat(Card attack, Card defense, Suit trump)
    {
        if (defense.Suit == attack.Suit && defense.Rank > attack.Rank) return true;
        if (defense.Suit == trump && attack.Suit != trump) return true;
        return false;
    }

    private Dictionary<string, Room> Rooms => (Dictionary<string, Room>)_roomsField.GetValue(games)!;
    private object Sync => _syncField.GetValue(games)!;
    private Room GetRoom(string code) => Rooms.TryGetValue(code, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
}
