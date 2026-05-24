using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
    public Room StartGame(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            EnsureHost(room, playerId);
            if (room.Players.Count(p => p.Status != PlayerStatus.Eliminated) < 2) throw new InvalidOperationException("Нужно минимум 2 игрока.");
            StartNewRound(room, room.Players.Where(p => p.Status != PlayerStatus.Eliminated).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
            return room;
        }
    }

    public bool ContinueGame(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var player = GetPlayer(room, playerId);
            if (room.Phase != GamePhase.Finished) throw new InvalidOperationException("Партия ещё не завершена.");
            if (player.Status != PlayerStatus.Connected) throw new InvalidOperationException("Продолжить может только подключённый игрок.");

            var connectedPlayers = room.Players.Where(p => p.Status == PlayerStatus.Connected).ToList();
            if (connectedPlayers.Count <= 1)
            {
                room.Players.Remove(player);
                if (room.Players.Count == 0) _rooms.Remove(room.Code);
                return false;
            }

            room.ContinuePlayerIds.Add(player.Id);
            room.RematchDeadlineUtc ??= DateTime.UtcNow.Add(RematchWaitPeriod);
            room.Log = $"{player.Name} готов продолжить. Новая партия начнётся через 15 секунд для тех, кто нажал продолжить.";
            return true;
        }
    }

    public IReadOnlyList<string> TickRoomTimers()
    {
        lock (_sync)
        {
            var changedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var room in _rooms.Values.ToList())
            {
                if (room.Phase == GamePhase.Playing)
                {
                    var expired = room.Players.Where(p => p.Status == PlayerStatus.Disconnected && !IsWithinGracePeriod(p)).ToList();
                    foreach (var player in expired)
                    {
                        EliminateDisconnectedPlayer(room, player, "не вернулся за 2 минуты");
                        changedRooms.Add(room.Code);
                    }
                }

                if (room.Phase == GamePhase.Finished && room.RematchDeadlineUtc is not null && DateTime.UtcNow >= room.RematchDeadlineUtc.Value)
                {
                    ProcessRematchDeadline(room);
                    if (_rooms.ContainsKey(room.Code)) changedRooms.Add(room.Code);
                }
            }

            return changedRooms.ToList();
        }
    }

    private void StartNewRound(Room room, HashSet<string> playerIds)
    {
        room.Players = room.Players.Where(p => playerIds.Contains(p.Id) && p.Status == PlayerStatus.Connected).ToList();
        if (room.Players.Count < 2)
        {
            _rooms.Remove(room.Code);
            return;
        }

        room.Phase = GamePhase.Playing;
        room.Deck = CreateDeck().OrderBy(_ => _random.Next()).ToList();
        room.TrumpCard = room.Deck.LastOrDefault();
        room.TrumpSuit = room.TrumpCard?.Suit;
        room.Table.Clear();
        room.PassedPlayerIds.Clear();
        room.ContinuePlayerIds.Clear();
        room.RematchDeadlineUtc = null;

        foreach (var p in room.Players)
        {
            p.Hand.Clear();
            p.Status = PlayerStatus.Connected;
            p.DisconnectedAtUtc = null;
            DrawUpToSix(room, p);
        }

        room.AttackerIndex = Math.Max(0, FindLowestTrumpOwner(room));
        room.DefenderIndex = NextPlayableIndex(room, room.AttackerIndex);
        room.Log = $"Новая партия началась. Козырь: {room.TrumpCard?.Label}. Ходит {room.Players[room.AttackerIndex].Name}.";
    }

    private void ProcessRematchDeadline(Room room)
    {
        var continueIds = room.ContinuePlayerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        room.Players.RemoveAll(p => !continueIds.Contains(p.Id));

        if (room.Players.Count < 2)
        {
            _rooms.Remove(room.Code);
            return;
        }

        StartNewRound(room, continueIds);
    }

    private void FinishGame(Room room, string message)
    {
        room.Phase = GamePhase.Finished;
        room.Table.Clear();
        room.PassedPlayerIds.Clear();
        room.ContinuePlayerIds.Clear();
        room.RematchDeadlineUtc = DateTime.UtcNow.Add(RematchWaitPeriod);
        room.Log = $"{message} Новая партия начнётся через 15 секунд для тех, кто нажмёт продолжить.";
    }

    private void EliminateDisconnectedPlayer(Room room, Player player, string reason)
    {
        player.Hand.Clear();
        player.ConnectionId = null;
        player.Status = PlayerStatus.Eliminated;
        player.DisconnectedAtUtc = null;
        player.Losses++;
        room.PassedPlayerIds.Remove(player.Id);
        room.ContinuePlayerIds.Remove(player.Id);

        if (room.Table.Count > 0 && (room.Players[room.AttackerIndex].Id == player.Id || room.Players[room.DefenderIndex].Id == player.Id))
        {
            room.Table.Clear();
            room.PassedPlayerIds.Clear();
        }

        if (room.Players[room.AttackerIndex].Id == player.Id)
            room.AttackerIndex = NextPlayableIndex(room, room.AttackerIndex);

        if (room.Players[room.DefenderIndex].Id == player.Id || room.DefenderIndex == room.AttackerIndex)
            room.DefenderIndex = NextPlayableIndex(room, room.AttackerIndex);

        var active = room.Players.Where(p => CanStillPlay(room, p)).ToList();
        if (active.Count == 1)
        {
            active[0].Wins++;
            FinishGame(room, $"{player.Name} исключён: {reason}. {active[0].Name} автоматически побеждает.");
            return;
        }

        if (active.Count == 0)
        {
            FinishGame(room, $"{player.Name} исключён: {reason}. Активных игроков не осталось.");
            return;
        }

        room.Log = $"{player.Name} исключён: {reason}. Ему засчитано поражение. Игра продолжается.";
    }

    private void CheckInstantFinish(Room room)
    {
        if (room.Phase != GamePhase.Playing) return;

        var playersInGame = room.Players.Where(p => p.Status != PlayerStatus.Eliminated).ToList();
        if (playersInGame.Count <= 1)
        {
            if (playersInGame.Count == 1) playersInGame[0].Wins++;
            FinishGame(room, playersInGame.Count == 1 ? $"{playersInGame[0].Name} автоматически побеждает." : "Игра завершена: активных игроков не осталось.");
            return;
        }

        if (room.Deck.Count > 0) return;

        var stillHoldingCards = playersInGame.Where(p => p.Hand.Count > 0).ToList();
        if (stillHoldingCards.Count <= 1)
        {
            foreach (var p in playersInGame)
            {
                if (p.Hand.Count == 0) p.Wins++;
                else p.Losses++;
            }
            var loser = stillHoldingCards.FirstOrDefault();
            FinishGame(room, loser is null ? "Игра закончена без дурака." : $"Игра закончена. Дурак: {loser.Name}.");
        }
    }
}
