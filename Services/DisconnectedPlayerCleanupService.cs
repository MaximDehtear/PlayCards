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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            IReadOnlyList<string> changedRooms;
            try
            {
                changedRooms = games.TickRoomTimers();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to tick room timers.");
                continue;
            }

            foreach (var roomCode in changedRooms)
            {
                await BroadcastRoom(roomCode, stoppingToken);
            }

            await hubContext.Clients.All.SendAsync("RoomsUpdated", games.GetRooms(), stoppingToken);
        }
    }

    private async Task BroadcastRoom(string roomCode, CancellationToken stoppingToken)
    {
        try
        {
            var scoreboard = games.GetScoreboard(roomCode);
            await hubContext.Clients.Group(roomCode).SendAsync("ScoreboardUpdated", scoreboard, stoppingToken);

            foreach (var (playerId, connectionId) in games.GetActiveConnections(roomCode))
            {
                var state = games.BuildState(roomCode, playerId);
                await hubContext.Clients.Client(connectionId).SendAsync("GameStateUpdated", state, stoppingToken);
            }
        }
        catch (InvalidOperationException)
        {
            // Room may have been removed after rematch timeout or everyone leaving.
        }
    }
}
