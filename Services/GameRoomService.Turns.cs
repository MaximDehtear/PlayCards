using PlayCards.Models;

namespace PlayCards.Services;

public sealed partial class GameRoomService
{
    public Room Attack(string roomCode, string playerId, string cardCode)
    {
        lock (_sync)
        {
            var room = GetPlayingRoom(roomCode);
            var player = GetActionPlayer(room, playerId);
            if (!CanPlayerAttack(room, player)) throw new InvalidOperationException("Сейчас не твоя очередь подкидывать.");
            if (!CanAddAttackCard(room)) throw new InvalidOperationException("Нельзя подкинуть больше карт, чем защитник может отбить.");

            var card = TakeCard(player, cardCode);
            if (room.Table.Count > 0 && !TableRanks(room).Contains(card.Rank))
            {
                player.Hand.Add(card);
                throw new InvalidOperationException("Подкидывать можно только карту такого же ранга, который уже есть на столе.");
            }

            room.Table.Add(new AttackPair { Attack = card });
            room.PassedPlayerIds.Clear();
            room.Log = $"{player.Name} атакует {card.Label}.";

            if (room.Table.Count > 0)
                room.AttackerIndex = NextThrowerIndex(room, room.AttackerIndex);

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
            room.AttackerIndex = NextPlayableIndex(room, room.DefenderIndex);
            room.DefenderIndex = NextPlayableIndex(room, room.AttackerIndex);
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

            var defender = room.Players[room.DefenderIndex];
            if (player.Id == defender.Id) throw new InvalidOperationException("Защитник не пасует. Защитник должен отбиться или взять карты.");
            if (room.AttackerIndex < 0 || room.AttackerIndex >= room.Players.Count || room.Players[room.AttackerIndex].Id != player.Id)
                throw new InvalidOperationException("Сейчас не твоя очередь пасовать.");

            room.PassedPlayerIds.Add(player.Id);

            var allDefended = room.Table.All(p => p.Defense is not null);
            var allAttackersDone = AreAllAttackersPassedOrUnable(room, defender);

            if (allDefended && allAttackersDone)
            {
                room.Table.Clear();
                room.PassedPlayerIds.Clear();
                RefillHandsFair(room);
                room.AttackerIndex = room.DefenderIndex;
                room.DefenderIndex = NextPlayableIndex(room, room.AttackerIndex);
                room.Log = $"Бито. Следующий ход: {room.Players[room.AttackerIndex].Name}.";
            }
            else
            {
                room.AttackerIndex = NextThrowerIndex(room, room.AttackerIndex);
                room.Log = $"{player.Name} пасует. Очередь подкидывать: {room.Players[room.AttackerIndex].Name}.";
            }

            CheckInstantFinish(room);
            return room;
        }
    }
}
