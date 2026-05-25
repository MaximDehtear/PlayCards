using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
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
                CanPass = room.Phase == GamePhase.Playing && room.Table.Count > 0 && player.Status == PlayerStatus.Connected && defenderId != player.Id,
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
}
