namespace PlayCards.Models;

public enum GamePhase
{
    Lobby,
    Playing,
    Finished
}

public enum PlayerStatus
{
    Connected,
    Disconnected,
    Eliminated
}

public sealed class Player
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ConnectionId { get; set; }
    public PlayerStatus Status { get; set; } = PlayerStatus.Connected;
    public DateTime? DisconnectedAtUtc { get; set; }
    public List<Card> Hand { get; set; } = [];
    public int Wins { get; set; }
    public int Losses { get; set; }
}

public sealed class AttackPair
{
    public Card Attack { get; set; } = default!;
    public Card? Defense { get; set; }
}

public sealed class Room
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public GamePhase Phase { get; set; } = GamePhase.Lobby;
    public List<Player> Players { get; set; } = [];
    public List<Card> Deck { get; set; } = [];
    public Card? TrumpCard { get; set; }
    public Suit? TrumpSuit { get; set; }
    public List<AttackPair> Table { get; set; } = [];
    public int AttackerIndex { get; set; }
    public int DefenderIndex { get; set; }
    public HashSet<string> PassedPlayerIds { get; set; } = [];
    public string Log { get; set; } = "Комната создана.";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed record RoomSummary(string Code, string Name, int Players, int MaxPlayers, GamePhase Phase);

public sealed class PublicPlayerState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Cards { get; set; }
    public bool IsAttacker { get; set; }
    public bool IsDefender { get; set; }
    public bool Passed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public PlayerStatus Status { get; set; }
    public int? SecondsToAutoKick { get; set; }
}

public sealed class GameState
{
    public string RoomCode { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public GamePhase Phase { get; set; }
    public string? CurrentPlayerId { get; set; }
    public List<Card> MyHand { get; set; } = [];
    public List<PublicPlayerState> Players { get; set; } = [];
    public List<Card> Deck { get; set; } = [];
    public Card? TrumpCard { get; set; }
    public Suit? TrumpSuit { get; set; }
    public List<AttackPair> Table { get; set; } = [];
    public string Log { get; set; } = string.Empty;
    public bool CanStart { get; set; }
    public bool IsMyAttack { get; set; }
    public bool IsMyDefense { get; set; }
    public bool CanPass { get; set; }
}
