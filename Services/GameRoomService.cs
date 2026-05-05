using PlayCards.Models;

namespace PlayCards.Services;

public sealed class GameRoomService
{
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
                .Select(r => new RoomSummary(r.Code, r.Name, r.Players.Count, MaxPlayers, r.Phase))
                .ToList();
        }
    }

    public IReadOnlyList<(string PlayerId, string ConnectionId)> GetActiveConnections(string roomCode)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            return room.Players
                .Where(p => !string.IsNullOrWhiteSpace(p.ConnectionId))
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
            if (room.Phase != GamePhase.Lobby) throw new InvalidOperationException("Игра уже началась.");
            if (room.Players.Count >= MaxPlayers) throw new InvalidOperationException("Комната заполнена.");

            var player = new Player { Name = CleanName(playerName), ConnectionId = connectionId };
            room.Players.Add(player);
            room.Log = $"{player.Name} вошёл в комнату.";
            return (room, player);
        }
    }

    public Room StartGame(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            EnsureHost(room, playerId);
            if (room.Players.Count < 2) throw new InvalidOperationException("Нужно минимум 2 игрока.");

            room.Phase = GamePhase.Playing;
            room.Deck = CreateDeck().OrderBy(_ => _random.Next()).ToList();
            room.Table.Clear();
            room.PassedPlayerIds.Clear();

            foreach (var p in room.Players)
            {
                p.Hand.Clear();
                p.Status = PlayerStatus.Connected;
                DrawUpToSix(room, p);
            }

            room.TrumpCard = room.Deck.LastOrDefault();
            room.TrumpSuit = room.TrumpCard?.Suit;
            room.AttackerIndex = FindLowestTrumpOwner(room);
            room.DefenderIndex = NextActiveIndex(room, room.AttackerIndex);
            room.Log = $"Игра началась. Козырь: {room.TrumpCard?.Label}. Ходит {room.Players[room.AttackerIndex].Name}.";
            return room;
        }
    }

    public Room Attack(string roomCode, string playerId, string cardCode)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var player = GetPlayer(room, playerId);
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

            foreach (var pair in room.Table)
            {
                defender.Hand.Add(pair.Attack);
                if (pair.Defense is not null) defender.Hand.Add(pair.Defense);
            }
            room.Table.Clear();
            room.PassedPlayerIds.Clear();
            RefillHands(room);
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
            var player = GetPlayer(room, playerId);
            if (room.Table.Count == 0) throw new InvalidOperationException("Пасовать можно после атаки.");
            room.PassedPlayerIds.Add(player.Id);

            var defender = room.Players[room.DefenderIndex];
            var allDefended = room.Table.All(p => p.Defense is not null);
            var attackers = room.Players.Where(p => p.Id != defender.Id && p.Hand.Count > 0).ToList();
            var allPassed = attackers.All(p => room.PassedPlayerIds.Contains(p.Id));

            if (allDefended && allPassed)
            {
                room.Table.Clear();
                room.PassedPlayerIds.Clear();
                RefillHands(room);
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

    public GameState BuildState(string roomCode, string playerId)
    {
        lock (_sync)
        {
            var room = GetRoomOrThrow(roomCode);
            var player = GetPlayer(room, playerId);
            var defenderId = room.Phase == GamePhase.Playing && room.Players.Count > room.DefenderIndex ? room.Players[room.DefenderIndex].Id : null;
            var attackerId = room.Phase == GamePhase.Playing && room.Players.Count > room.AttackerIndex ? room.Players[room.AttackerIndex].Id : null;

            return new GameState
            {
                RoomCode = room.Code,
                RoomName = room.Name,
                Phase = room.Phase,
                CurrentPlayerId = player.Id,
                MyHand = player.Hand.OrderBy(c => c.Suit).ThenBy(c => c.Rank).ToList(),
                Players = room.Players.Select(p => new PublicPlayerState
                {
                    Id = p.Id,
                    Name = p.Name,
                    Cards = p.Hand.Count,
                    IsAttacker = p.Id == attackerId,
                    IsDefender = p.Id == defenderId,
                    Passed = room.PassedPlayerIds.Contains(p.Id),
                    Wins = p.Wins,
                    Losses = p.Losses,
                    Status = p.Status
                }).ToList(),
                Deck = room.Deck.ToList(),
                TrumpCard = room.TrumpCard,
                TrumpSuit = room.TrumpSuit,
                Table = room.Table.Select(p => new AttackPair { Attack = p.Attack, Defense = p.Defense }).ToList(),
                Log = room.Log,
                CanStart = room.Phase == GamePhase.Lobby && room.Players.FirstOrDefault()?.Id == player.Id && room.Players.Count >= 2,
                IsMyAttack = room.Phase == GamePhase.Playing && CanPlayerAttack(room, player),
                IsMyDefense = room.Phase == GamePhase.Playing && defenderId == player.Id,
                CanPass = room.Phase == GamePhase.Playing && room.Table.Count > 0
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
                .Select(p => new PublicPlayerState { Id = p.Id, Name = p.Name, Wins = p.Wins, Losses = p.Losses, Cards = p.Hand.Count, Status = p.Status })
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
                if (player is not null)
                {
                    player.Status = PlayerStatus.Disconnected;
                    room.Log = $"{player.Name} отключился.";
                    return;
                }
            }
        }
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
    private static void EnsureHost(Room room, string playerId) { if (room.Players.FirstOrDefault()?.Id != playerId) throw new InvalidOperationException("Запустить игру может только создатель комнаты."); }

    private void DrawUpToSix(Room room, Player player)
    {
        while (player.Hand.Count < 6 && room.Deck.Count > 0)
        {
            player.Hand.Add(room.Deck[0]);
            room.Deck.RemoveAt(0);
        }
    }

    private void RefillHands(Room room)
    {
        var order = room.Players.Skip(room.AttackerIndex).Concat(room.Players.Take(room.AttackerIndex));
        foreach (var player in order) DrawUpToSix(room, player);
    }

    private int FindLowestTrumpOwner(Room room)
    {
        var trump = room.TrumpSuit!.Value;
        var candidate = room.Players
            .Select((p, i) => new { Player = p, Index = i, Card = p.Hand.Where(c => c.Suit == trump).OrderBy(c => c.Rank).FirstOrDefault() })
            .Where(x => x.Card is not null)
            .OrderBy(x => x.Card!.Rank)
            .FirstOrDefault();
        return candidate?.Index ?? 0;
    }

    private int NextActiveIndex(Room room, int from)
    {
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (from + step) % room.Players.Count;
            if (room.Players[idx].Hand.Count > 0 || room.Deck.Count > 0) return idx;
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
        if (room.Players[room.DefenderIndex].Id == player.Id) return false;
        if (room.Table.Count == 0) return room.Players[room.AttackerIndex].Id == player.Id;
        return player.Hand.Count > 0 && !room.PassedPlayerIds.Contains(player.Id);
    }

    private void CheckInstantFinish(Room room)
    {
        if (room.Phase != GamePhase.Playing) return;
        if (room.Deck.Count > 0) return;

        var active = room.Players.Where(p => p.Hand.Count > 0).ToList();
        if (active.Count <= 1)
        {
            room.Phase = GamePhase.Finished;
            foreach (var p in room.Players)
            {
                if (p.Hand.Count == 0) p.Wins++;
                else p.Losses++;
            }
            var loser = active.FirstOrDefault();
            room.Log = loser is null ? "Игра закончена без дурака." : $"Игра закончена. Дурак: {loser.Name}.";
        }
    }
}
