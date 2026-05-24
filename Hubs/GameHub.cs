using Microsoft.AspNetCore.SignalR;
using PlayCards.Models;
using PlayCards.Services;

namespace PlayCards.Hubs;

public sealed class GameHub(
    GameRoomService games,
    BotPlayerService bots,
    SmartDefenseService smartDefense,
    ConnectionRecoveryService recovery,
    TurnRulesService rules,
    BotSessionLifecycleService botLifecycle) : Hub
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
        var (room, player) = recovery.RecoverByPlayerId(roomCode, playerId, Context.ConnectionId);
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
        rules.NormalizeRoom(roomCode);
        await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        await BroadcastAfterBotTurns(roomCode);
    }

    public async Task Attack(string roomCode, string playerId, string cardCode)
    {
        rules.EnsureCanAttack(roomCode, playerId);
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
        try
        {
            var stillInRoom = botLifecycle.ContinueHumanAndBots(roomCode, playerId);
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
            if (stillInRoom) await BroadcastAfterBotTurns(roomCode);
            else await Clients.Caller.SendAsync("LeftRoom");
        }
        catch (InvalidOperationException ex) when (IsMissingOrStaleSession(ex))
        {
            await Clients.Caller.SendAsync("ActionError", "Комната уже удалена или сессия устарела. Я вернул тебя на главный экран — создай новую комнату.");
            await Clients.Caller.SendAsync("LeftRoom");
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("ActionError", $"Не удалось продолжить игру: {ex.Message}");
            throw;
        }
    }

    public async Task LeaveRoom(string roomCode, string playerId)
    {
        try
        {
            var stillExists = games.LeaveRoom(roomCode, playerId);
            if (stillExists) stillExists = botLifecycle.RemoveBotsAfterHumanLeaves(roomCode);
            await Clients.Caller.SendAsync("LeftRoom");
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
            if (stillExists) await BroadcastAfterBotTurns(roomCode);
        }
        catch (InvalidOperationException ex) when (IsMissingOrStaleSession(ex))
        {
            await Clients.Caller.SendAsync("ActionError", "Комната уже удалена или сессия устарела. Я очистил локальную сессию.");
            await Clients.Caller.SendAsync("LeftRoom");
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("ActionError", $"Не удалось выйти из комнаты: {ex.Message}");
            throw;
        }
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
        try
        {
            rules.NormalizeRoom(roomCode);
            var changedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { roomCode };
            foreach (var changedRoom in bots.RunBotTurns()) changedRooms.Add(changedRoom);

            foreach (var changedRoom in changedRooms)
            {
                try
                {
                    rules.NormalizeRoom(changedRoom);
                    await BroadcastRoom(changedRoom);
                }
                catch (InvalidOperationException)
                {
                    // Room may be removed by cleanup/everyone leaving.
                }
            }

            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        }
        catch (InvalidOperationException ex) when (IsMissingOrStaleSession(ex))
        {
            await Clients.Caller.SendAsync("ActionError", "Комната была удалена во время обновления состояния. Я вернул тебя на главный экран.");
            await Clients.Caller.SendAsync("LeftRoom");
            await Clients.All.SendAsync("RoomsUpdated", games.GetRooms());
        }
    }

    public async Task BroadcastRoom(string roomCode)
    {
        rules.NormalizeRoom(roomCode);
        var scoreboard = games.GetScoreboard(roomCode);
        await Clients.Group(roomCode).SendAsync("ScoreboardUpdated", scoreboard);

        foreach (var (playerId, connectionId) in games.GetActiveConnections(roomCode))
        {
            var state = games.BuildState(roomCode, playerId);
            await Clients.Client(connectionId).SendAsync("GameStateUpdated", state);
        }
    }

    private static bool IsMissingOrStaleSession(InvalidOperationException ex) =>
        ex.Message.Contains("Комната не найдена", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("сессия устарела", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Игрок не найден", StringComparison.OrdinalIgnoreCase);
}
