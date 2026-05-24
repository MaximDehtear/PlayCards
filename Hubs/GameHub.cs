using Microsoft.AspNetCore.SignalR;
using PlayCards.Models;
using PlayCards.Services;

namespace PlayCards.Hubs;

public sealed class GameHub(GameRoomService games, BotPlayerService bots, SmartDefenseService smartDefense) : Hub
{
    public Task<IReadOnlyList<RoomSummary>> GetRooms() => Task.FromResult(games.GetRooms());

    public async Task<object> CreateRoom(string roomName, string playerName)
    {
        var (room, player) = games.CreateRoom(roomName, playerName, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastAfterBotTurns(room.Code);
        return new { roomCode = room.Code, playerId = player.Id };
    }

    public async Task<object> JoinRoom(string roomCode, string playerName)
    {
        var (room, player) = games.JoinRoom(roomCode, playerName, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastAfterBotTurns(room.Code);
        return new { roomCode = room.Code, playerId = player.Id };
    }

    public async Task<object> ReconnectRoom(string roomCode, string playerId)
    {
        var (room, player) = games.ReconnectRoom(roomCode, playerId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastAfterBotTurns(room.Code);
        return new { roomCode = room.Code, playerId = player.Id };
    }

    public async Task AddBot(string roomCode)
    {
        bots.AddBot(roomCode);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastRoom(roomCode);
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        games.StartGame(roomCode, playerId);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task Attack(string roomCode, string playerId, string cardCode)
    {
        if (!smartDefense.TryDefendFirst(roomCode, playerId, cardCode))
        {
            games.Attack(roomCode, playerId, cardCode);
        }
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task Defend(string roomCode, string playerId, string attackCode, string defenseCode)
    {
        games.Defend(roomCode, playerId, attackCode, defenseCode);
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task Take(string roomCode, string playerId)
    {
        games.Take(roomCode, playerId);
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task Pass(string roomCode, string playerId)
    {
        games.Pass(roomCode, playerId);
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task ContinueGame(string roomCode, string playerId)
    {
        var stillInRoom = games.ContinueGame(roomCode, playerId);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        if (stillInRoom) await BroadcastAfterBotTurns(roomCode);
        else await Clients.Caller.SendAsync("LeftRoom");
    }

    public async Task LeaveRoom(string roomCode, string playerId)
    {
        var stillExists = games.LeaveRoom(roomCode, playerId);
        await Clients.Caller.SendAsync("LeftRoom");
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        if (stillExists) await BroadcastAfterBotTurns(roomCode);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var roomCode = games.FindPlayerRoomByConnection(Context.ConnectionId, out _);
        games.Disconnect(Context.ConnectionId);
        if (roomCode is not null)
        {
            await BroadcastRoom(roomCode);
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastAfterBotTurns(string roomCode)
    {
        var changedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { roomCode };
        foreach (var changedRoom in bots.RunBotTurns()) changedRooms.Add(changedRoom);

        foreach (var changedRoom in changedRooms)
        {
            try
            {
                await BroadcastRoom(changedRoom);
            }
            catch (InvalidOperationException)
            {
                // Room may be removed by cleanup/rematch.
            }
        }

        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
    }

    public async Task BroadcastRoom(string roomCode)
    {
        var scoreboard = games.GetScoreboard(roomCode);
        await Clients.Group(roomCode).SendAsync("ScoreboardUpdated", scoreboard);

        foreach (var (playerId, connectionId) in games.GetActiveConnections(roomCode))
        {
            var state = games.BuildState(roomCode, playerId);
            await Clients.Client(connectionId).SendAsync("GameStateUpdated", state);
        }
    }
}
