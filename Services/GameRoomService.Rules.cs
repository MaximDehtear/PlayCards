using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
    private string CreateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string code;
        do
        {
            code = new string(Enumerable.Range(0, 5).Select(_ => chars[_random.Next(chars.Length)]).ToArray());
        } while (_rooms.ContainsKey(code));
        return code;
    }

    private static string CleanName(string name) => string.IsNullOrWhiteSpace(name) ? "Игрок" : name.Trim()[..Math.Min(name.Trim().Length, 24)];

    private static List<Card> CreateDeck() => Enum.GetValues<Suit>()
        .SelectMany(s => Enum.GetValues<Rank>().Select(r => new Card(s, r)))
        .ToList();

    private Room GetRoomOrThrow(string roomCode) =>
        _rooms.TryGetValue(roomCode, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");

    private Room GetPlayingRoom(string roomCode)
    {
        var room = GetRoomOrThrow(roomCode);
        if (room.Phase != GamePhase.Playing) throw new InvalidOperationException("Игра не запущена.");
        return room;
    }

    private static Player GetPlayer(Room room, string playerId) =>
        room.Players.FirstOrDefault(p => p.Id == playerId) ?? throw new InvalidOperationException("Игрок не найден.");

    private static Player GetActionPlayer(Room room, string playerId)
    {
        var player = GetPlayer(room, playerId);
        EnsureCanAct(player);
        return player;
    }

    private static void EnsureCanAct(Player player)
    {
        if (player.Status == PlayerStatus.Disconnected) throw new InvalidOperationException("Игрок отключён.");
        if (player.Status == PlayerStatus.Eliminated) throw new InvalidOperationException("Игрок исключён из игры.");
    }

    private static void EnsureHost(Room room, string playerId)
    {
        if (room.Players.FirstOrDefault()?.Id != playerId) throw new InvalidOperationException("Запустить игру может только создатель комнаты.");
    }

    private static bool IsWithinGracePeriod(Player player) =>
        player.Status != PlayerStatus.Disconnected ||
        player.DisconnectedAtUtc is null ||
        DateTime.UtcNow - player.DisconnectedAtUtc.Value <= DisconnectGracePeriod;

    private static bool CanStillPlay(Room room, Player player) =>
        player.Status == PlayerStatus.Connected && (room.Deck.Count > 0 || player.Hand.Count > 0);

    private static bool CanAddAttackCard(Room room)
    {
        if (room.Table.Count == 0) return true;
        if (room.DefenderIndex < 0 || room.DefenderIndex >= room.Players.Count) return false;

        var defender = room.Players[room.DefenderIndex];
        var activeAttackCards = room.Table.Count;
        var defendedCards = room.Table.Count(p => p.Defense is not null);
        var maxAttackCards = defender.Hand.Count + defendedCards;
        return activeAttackCards < maxAttackCards;
    }

    private static HashSet<Rank> TableRanks(Room room) => room.Table
        .Select(p => p.Attack.Rank)
        .Concat(room.Table.Where(p => p.Defense is not null).Select(p => p.Defense!.Rank))
        .ToHashSet();

    private static bool HasLegalThrowIn(Room room, Player player)
    {
        if (player.Hand.Count == 0 || room.Table.Count == 0) return false;
        if (!CanAddAttackCard(room)) return false;
        var ranks = TableRanks(room);
        return player.Hand.Any(c => ranks.Contains(c.Rank));
    }

    private static bool AreAllAttackersPassedOrUnable(Room room, Player defender)
    {
        if (!CanAddAttackCard(room)) return true;

        return room.Players
            .Where(p => CanStillPlay(room, p) && p.Id != defender.Id && p.Hand.Count > 0)
            .All(p => room.PassedPlayerIds.Contains(p.Id) || !HasLegalThrowIn(room, p));
    }

    private void DrawUpToSix(Room room, Player player)
    {
        while (player.Hand.Count < 6 && room.Deck.Count > 0)
        {
            player.Hand.Add(room.Deck[0]);
            room.Deck.RemoveAt(0);
        }
    }

    private void RefillHandsFair(Room room)
    {
        var active = room.Players.Where(p => p.Status != PlayerStatus.Eliminated).ToList();
        if (active.Count == 0) return;

        while (room.Deck.Count > 0)
        {
            var candidates = active.Where(p => p.Hand.Count < 6).ToList();
            if (candidates.Count == 0) return;

            var minCards = candidates.Min(p => p.Hand.Count);
            var next = OrderedFrom(room, room.AttackerIndex)
                .Where(p => candidates.Contains(p) && p.Hand.Count == minCards)
                .First();

            next.Hand.Add(room.Deck[0]);
            room.Deck.RemoveAt(0);
        }
    }

    private IEnumerable<Player> OrderedFrom(Room room, int start)
    {
        for (var step = 0; step < room.Players.Count; step++)
            yield return room.Players[(start + step) % room.Players.Count];
    }

    private int FindLowestTrumpOwner(Room room)
    {
        var trump = room.TrumpSuit!.Value;
        var candidate = room.Players
            .Select((p, i) => new { Player = p, Index = i, Card = p.Hand.Where(c => c.Suit == trump).OrderBy(c => c.Rank).FirstOrDefault() })
            .Where(x => CanStillPlay(room, x.Player) && x.Card is not null)
            .OrderBy(x => x.Card!.Rank)
            .FirstOrDefault();
        return candidate?.Index ?? Math.Max(0, room.Players.FindIndex(p => CanStillPlay(room, p)));
    }

    private int NextPlayableIndex(Room room, int from)
    {
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (from + step) % room.Players.Count;
            if (CanStillPlay(room, room.Players[idx])) return idx;
        }
        return from;
    }

    private static Card TakeCard(Player player, string code)
    {
        var card = player.Hand.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (card is null) throw new InvalidOperationException("Карты нет в руке.");
        player.Hand.Remove(card);
        return card;
    }

    private static bool CanBeat(Card attack, Card defense, Suit trump)
    {
        if (defense.Suit == attack.Suit && defense.Rank > attack.Rank) return true;
        if (defense.Suit == trump && attack.Suit != trump) return true;
        return false;
    }

    private static bool CanPlayerAttack(Room room, Player player)
    {
        if (player.Status != PlayerStatus.Connected) return false;
        if (room.DefenderIndex >= 0 && room.DefenderIndex < room.Players.Count && room.Players[room.DefenderIndex].Id == player.Id) return false;
        if (player.Hand.Count == 0) return false;
        if (!CanAddAttackCard(room)) return false;
        if (room.Table.Count == 0) return room.AttackerIndex >= 0 && room.AttackerIndex < room.Players.Count && room.Players[room.AttackerIndex].Id == player.Id;
        return !room.PassedPlayerIds.Contains(player.Id) && HasLegalThrowIn(room, player);
    }
}
