using Microsoft.AspNetCore.SignalR;
using PlayCards.Hubs;

namespace PlayCards.Services;

public sealed class DisconnectedPlayerCleanupService(
    GameRoomService games,
    IHubContext<GameHub> hubContext,
    ILogger<DisconnectedPlayerCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            IReadOnlyList<string> changedRooms;
            try
            {
                changedRooms = games.ExpireDisconnectedPlayers();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to expire disconnected players.");
                continue;
            }

            foreach (var roomCode in changedRooms)
            {
                var scoreboard = games.GetScoreboard(roomCode);
                await hubContext.Clients.Group(roomCode).SendAsync("ScoreboardUpdated", scoreboard, stoppingToken);

                foreach (var (playerId, connectionId) in games.GetActiveConnections(roomCode))
                {
                    var state = games.BuildState(roomCode, playerId);
                    await hubContext.Clients.Client(connectionId).SendAsync("GameStateUpdated", state, stoppingToken);
                }

                await hubContext.Clients.All.SendAsync("RoomsUpdated", games.GetRooms(), stoppingToken);
            }
        }
    }
}
