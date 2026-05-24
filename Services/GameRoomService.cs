using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
    public static readonly TimeSpan DisconnectGracePeriod = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RematchWaitPeriod = TimeSpan.FromSeconds(15);

    private const int MaxPlayers = 6;
    private readonly object _sync = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
}
