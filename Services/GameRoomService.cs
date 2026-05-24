using PlayCards.Models;

namespace PlayCards.Services;

public sealed class GameRoomService
{
    public static readonly TimeSpan DisconnectGracePeriod = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RematchWaitPeriod = TimeSpan.FromSeconds(15);

    private const int MaxPlayers = 6;
    private readonly object _sync = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();

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

    public Room Attack(string roomCode, string playerId, string cardCode)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var player = GetActionPlayer(room, playerId);
            if (!CanPlayerAttack(room, player)) throw new InvalidOperationException("Сейчас не твоя атака.");

            var card = TakeCard(player, cardCode);
            if (room.Table.Count > 0 && !room.Table.Any(p => p.Attack.Rank == card.Rank || p.Defense?.Rank == card.Rank))
            {
                player.Hand.Add(card);
                throw new InvalidOperationException("Подкидывать можно только карту такого же ранга, который уже есть на столе.");
            }

            room.Table.Add(new AttackPair { Attack = card });
            room.PassedPlayerIds.Remove(player.Id);
            room.Log = $"{player.Name} атакует {card.Label}.";
            CheckInstantFinish(room);
            return room;
        }
    }

    public Room Defend(string roomCode, string playerId, string attackCode, string defenseCode)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var defender = room.Players[room.DefenderIndex];
            if (defender.Id != playerId) throw new InvalidOperationException("Сейчас не твоя защита.");
            EnsureCanAct(defender);

            var pair = room.Table.FirstOrDefault(p => p.Attack.Code == attackCode && p.Defense is null)
                ?? throw new InvalidOperationException("Эта карта уже отбита или не найдена.");
            var defense = TakeCard(defender, defenseCode);
            if (!CanBeat(pair.Attack, defense, room.TrumpSuit!.Value))
            {
                defender.Hand.Add(defense);
                throw new InvalidOperationException("Эта карта не бьёт атакующую.");
            }

            pair.Defense = defense;
            room.Log = $"{defender.Name} отбивает {pair.Attack.Label} картой {defense.Label}.";
            CheckInstantFinish(room);
            return room;
        }
    }

    public Room Take(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var defender = room.Players[room.DefenderIndex];
            if (defender.Id != playerId) throw new InvalidOperationException("Брать может только защитник.");
            EnsureCanAct(defender);

            foreach (var pair in room.Table)
            {
                defender.Hand.Add(pair.Attack);
                if (pair.Defense is not null) defender.Hand.Add(pair.Defense);
            }

            room.Table.Clear();
            room.PassedPlayerIds.Clear();
            RefillHandsFair(room);
            room.AttackerIndex = NextActiveIndex(room, room.DefenderIndex);
            room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);
            room.Log = $"{defender.Name} берёт карты. Следующий ход: {room.Players[room.AttackerIndex].Name}.";
            CheckInstantFinish(room);
            return room;
        }
    }

    public Room Pass(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var player = GetActionPlayer(room, playerId);
            if (room.Table.Count == 0) throw new InvalidOperationException("Пасовать можно после атаки.");
            room.PassedPlayerIds.Add(player.Id);

            var defender = room.Players[room.DefenderIndex];
            var allDefended = room.Table.All(p => p.Defense is not null);
            var attackers = room.Players.Where(p => IsActiveInGame(p) && p.Id != defender.Id && p.Hand.Count > 0).ToList();
            var allPassed = attackers.All(p => room.PassedPlayerIds.Contains(p.Id));

            if (allDefended && allPassed)
            {
                room.Table.Clear();
                room.PassedPlayerIds.Clear();
                RefillHandsFair(room);
                room.AttackerIndex = room.DefenderIndex;
                room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);
                room.Log = $"Бито. Следующий ход: {room.Players[room.AttackerIndex].Name}.";
            }
            else
            {
                room.Log = $"{player.Name} пасует.";
            }

            CheckInstantFinish(room);
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

    public GameState BuildState(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var player = GetPlayer(room, playerId);
            var defenderId = room.Phase == GamePhase.Playing && room.Players.Count > room.DefenderIndex ? room.Players[room.DefenderIndex].Id : null;
            var attackerId = room.Phase == GamePhase.Playing && room.Players.Count > room.AttackerIndex ? room.Players[room.AttackerIndex].Id : null;
            var now = DateTime.UtcNow;

            return new GameState
            {
                RoomCode = room.Code,
                RoomName = room.Name,
                Phase = room.Phase,
                CurrentPlayerId = player.Id,
                MyHand = player.Status == PlayerStatus.Eliminated ? [] : player.Hand.OrderBy(c => c.Suit).ThenBy(c => c.Rank).ToList(),
                Players = room.Players.Select(p => new PublicPlayerState
                {
                    Id = p.Id,
                    Name = p.Name,
                    IsBot = p.IsBot,
                    Cards = p.Hand.Count,
                    IsAttacker = p.Id == attackerId,
                    IsDefender = p.Id == defenderId,
                    Passed = room.PassedPlayerIds.Contains(p.Id),
                    WantsContinue = room.ContinuePlayerIds.Contains(p.Id),
                    Wins = p.Wins,
                    Losses = p.Losses,
                    Status = p.Status,
                    SecondsToAutoKick = p.Status == PlayerStatus.Disconnected && p.DisconnectedAtUtc is not null
                        ? Math.Max(0, (int)Math.Ceiling((DisconnectGracePeriod - (now - p.DisconnectedAtUtc.Value)).TotalSeconds))
                        : null
                }).ToList(),
                Deck = room.Deck.ToList(),
                TrumpCard = room.TrumpCard,
                TrumpSuit = room.TrumpSuit,
                Table = room.Table.Select(p => new AttackPair { Attack = p.Attack, Defense = p.Defense }).ToList(),
                Log = room.Log,
                CanStart = room.Phase == GamePhase.Lobby && room.Players.FirstOrDefault()?.Id == player.Id && room.Players.Count(p => p.Status != PlayerStatus.Eliminated) >= 2,
                IsMyAttack = room.Phase == GamePhase.Playing && CanPlayerAttack(room, player),
                IsMyDefense = room.Phase == GamePhase.Playing && defenderId == player.Id && player.Status == PlayerStatus.Connected,
                CanPass = room.Phase == GamePhase.Playing && room.Table.Count > 0 && player.Status == PlayerStatus.Connected,
                WantsContinue = room.ContinuePlayerIds.Contains(player.Id),
                SecondsToRematch = room.Phase == GamePhase.Finished && room.RematchDeadlineUtc is not null
                    ? Math.Max(0, (int)Math.Ceiling((room.RematchDeadlineUtc.Value - now).TotalSeconds))
                    : null
            };
        }
    }

    public IReadOnlyList<PublicPlayerState> GetScoreboard(string roomCode)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            return room.Players
                .OrderByDescending(p => p.Wins)
                .ThenBy(p => p.Losses)
                .Select(p => new PublicPlayerState
                {
                    Id = p.Id,
                    Name = p.Name,
                    IsBot = p.IsBot,
                    Wins = p.Wins,
                    Losses = p.Losses,
                    Cards = p.Hand.Count,
                    Status = p.Status,
                    WantsContinue = room.ContinuePlayerIds.Contains(p.Id),
                    SecondsToAutoKick = p.Status == PlayerStatus.Disconnected && p.DisconnectedAtUtc is not null
                        ? Math.Max(0, (int)Math.Ceiling((DisconnectGracePeriod - (DateTime.UtcNow - p.DisconnectedAtUtc.Value)).TotalSeconds))
                        : null
                })
                .ToList();
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
        room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);
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

    private void ReconnectPlayer(Room room, Player player, string connectionId)
    {
        player.ConnectionId = connectionId;
        player.Status = PlayerStatus.Connected;
        player.DisconnectedAtUtc = null;
        room.Log = $"{player.Name} вернулся в игру.";
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
            room.AttackerIndex = NextActiveIndex(room, room.AttackerIndex);

        if (room.Players[room.DefenderIndex].Id == player.Id || room.DefenderIndex == room.AttackerIndex)
            room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);

        var active = room.Players.Where(IsActiveInGame).ToList();
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
    private static List<Card> CreateDeck() => Enum.GetValues<Suit>().SelectMany(s => Enum.GetValues<Rank>().Select(r => new Card(s, r))).ToList();
    private Room GetRoomOrThrow(string roomCode) => _rooms.TryGetValue(roomCode, out var room) ? room : throw new InvalidOperationException("Комната не найдена.");
    private Room GetPlayingRoom(string roomCode) { var room = GetRoomOrThrow(roomCode); if (room.Phase != GamePhase.Playing) throw new InvalidOperationException("Игра не запущена."); return room; }
    private static Player GetPlayer(Room room, string playerId) => room.Players.FirstOrDefault(p => p.Id == playerId) ?? throw new InvalidOperationException("Игрок не найден.");
    private static Player GetActionPlayer(Room room, string playerId) { var player = GetPlayer(room, playerId); EnsureCanAct(player); return player; }
    private static void EnsureCanAct(Player player)
    {
        if (player.Status == PlayerStatus.Disconnected) throw new InvalidOperationException("Игрок отключён.");
        if (player.Status == PlayerStatus.Eliminated) throw new InvalidOperationException("Игрок исключён из игры.");
    }
    private static void EnsureHost(Room room, string playerId) { if (room.Players.FirstOrDefault()?.Id != playerId) throw new InvalidOperationException("Запустить игру может только создатель комнаты."); }
    private static bool IsActiveInGame(Player player) => player.Status != PlayerStatus.Eliminated;
    private static bool IsWithinGracePeriod(Player player) => player.Status != PlayerStatus.Disconnected || player.DisconnectedAtUtc is null || DateTime.UtcNow - player.DisconnectedAtUtc.Value <= DisconnectGracePeriod;

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
        var active = room.Players.Where(IsActiveInGame).ToList();
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
            .Where(x => IsActiveInGame(x.Player) && x.Card is not null)
            .OrderBy(x => x.Card!.Rank)
            .FirstOrDefault();
        return candidate?.Index ?? room.Players.FindIndex(IsActiveInGame);
    }

    private int NextActiveIndex(Room room, int from)
    {
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (from + step) % room.Players.Count;
            if (IsActiveInGame(room.Players[idx])) return idx;
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
        if (room.Players[room.DefenderIndex].Id == player.Id) return false;
        if (room.Table.Count == 0) return room.Players[room.AttackerIndex].Id == player.Id;
        return player.Hand.Count > 0 && !room.PassedPlayerIds.Contains(player.Id);
    }

    private void CheckInstantFinish(Room room)
    {
        if (room.Phase != GamePhase.Playing) return;

        var activePlayers = room.Players.Where(IsActiveInGame).ToList();
        if (activePlayers.Count <= 1)
        {
            if (activePlayers.Count == 1) activePlayers[0].Wins++;
            FinishGame(room, activePlayers.Count == 1 ? $"{activePlayers[0].Name} автоматически побеждает." : "Игра завершена: активных игроков не осталось.");
            return;
        }

        if (room.Deck.Count > 0) return;

        var stillHoldingCards = activePlayers.Where(p => p.Hand.Count > 0).ToList();
        if (stillHoldingCards.Count <= 1)
        {
            foreach (var p in activePlayers)
            {
                if (p.Hand.Count == 0) p.Wins++;
                else p.Losses++;
            }
            var loser = stillHoldingCards.FirstOrDefault();
            FinishGame(room, loser is null ? "Игра закончена без дурака." : $"Игра закончена. Дурак: {loser.Name}.");
        }
    }
}
