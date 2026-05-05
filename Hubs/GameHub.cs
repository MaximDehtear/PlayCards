using Microsoft.AspNetCore.SignalR;
using PlayCards.Models;
using PlayCards.Services;

namespace PlayCards.Hubs;

public sealed class GameHub(GameRoomService games) : Hub
{
    public Task<IReadOnlyList<RoomSummary>> GetRooms() => Task.FromResult(games.GetRooms());

    public async Task<object> CreateRoom(string roomName, string playerName)
    {
        var (room, player) = games.CreateRoom(roomName, playerName, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastRoom(room.Code);
        return new { roomCode = room.Code, playerId = player.Id };
    }

    public async Task<object> JoinRoom(string roomCode, string playerName)
    {
        var (room, player) = games.JoinRoom(roomCode, playerName, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastRoom(room.Code);
        return new { roomCode = room.Code, playerId = player.Id };
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        games.StartGame(roomCode, playerId);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastRoom(roomCode);
    }

    public async Task Attack(string roomCode, string playerId, string cardCode)
    {
        games.Attack(roomCode, playerId, cardCode);
        await BroadcastRoom(roomCode);
    }

    public async Task Defend(string roomCode, string playerId, string attackCode, string defenseCode)
    {
        games.Defend(roomCode, playerId, attackCode, defenseCode);
        await BroadcastRoom(roomCode);
    }

    public async Task Take(string roomCode, string playerId)
    {
        games.Take(roomCode, playerId);
        await BroadcastRoom(roomCode);
    }

    public async Task Pass(string roomCode, string playerId)
    {
        games.Pass(roomCode, playerId);
        await BroadcastRoom(roomCode);
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

    private async Task BroadcastRoom(string roomCode)
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
