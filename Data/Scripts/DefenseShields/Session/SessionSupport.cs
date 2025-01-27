﻿
using System.Collections.Generic;
using ParallelTasks;

namespace DefenseShields
{
    using Support;
    using Sandbox.ModAPI;
    using VRage.Game.ModAPI;
    using System;
    using VRage.Game.Entity;
    using VRage.Game;
    using Sandbox.Game.Entities;

    public partial class Session
    {
        public string ModPath()
        {
            var modPath = ModContext.ModPath;
            return modPath;
        }

        public bool TaskHasErrors(ref Task task, string taskName)
        {
            if (task.Exceptions != null && task.Exceptions.Length > 0)
            {
                foreach (var e in task.Exceptions)
                {
                    Log.Line($"{taskName} thread!\n{e}");
                }

                return true;
            }

            return false;
        }

        private void PlayerConnected(long id)
        {
            try
            {
                if (Players.ContainsKey(id))
                {
                    if (Enforced.Debug >= 3) Log.Line($"Player id({id}) already exists");
                    return;
                }
                MyAPIGateway.Multiplayer.Players.GetPlayers(null, myPlayer => FindPlayer(myPlayer, id));
            }
            catch (Exception ex) { Log.Line($"Exception in PlayerConnected: {ex}"); }
        }

        private void PlayerDisconnected(long l)
        {
            try
            {
                IMyPlayer removedPlayer;
                Players.TryRemove(l, out removedPlayer);
                PlayerEventId++;
                if (Enforced.Debug >= 3) Log.Line($"Removed player, new playerCount:{Players.Count}");
            }
            catch (Exception ex) { Log.Line($"Exception in PlayerDisconnected: {ex}"); }
        }

        private bool FindPlayer(IMyPlayer player, long id)
        {
            if (player.IdentityId == id)
            {
                Players[id] = player;
                PlayerEventId++;
                if (Enforced.Debug >= 3) Log.Line($"Added player: {player.DisplayName}, new playerCount:{Players.Count}");
            }
            return false;
        }

        private void SplitMonitor()
        {
            foreach (var pair in CheckForSplits)
            {
                if (WatchForSplits.Add(pair.Key))
                    pair.Key.OnGridSplit += GridSplitWatch;
                else if (Tick - pair.Value > 120)
                    _tmpWatchGridsToRemove.Add(pair.Key);
            }

            for (int i = 0; i < _tmpWatchGridsToRemove.Count; i++)
            {
                var grid = _tmpWatchGridsToRemove[i];
                grid.OnGridSplit -= GridSplitWatch;
                WatchForSplits.Remove(grid);
                CheckForSplits.Remove(grid);
            }
            _tmpWatchGridsToRemove.Clear();

            foreach (var parent in GetParentGrid)
            {
                ParentGrid oldParent;
                if (Tick - parent.Value.Age > 120)
                    GetParentGrid.TryRemove(parent.Key, out oldParent);
            }
        }

        #region Events

        internal struct ParentGrid
        {
            internal MyCubeGrid Parent;
            internal uint Age;
        }

        private void GridSplitWatch(MyCubeGrid parent, MyCubeGrid child)
        {
            GetParentGrid.TryAdd(child, new ParentGrid { Parent = parent, Age = Tick });
        }

        private void OnEntityRemove(MyEntity myEntity)
        {
            if (Environment.CurrentManagedThreadId == 1) {

                MyProtectors protector;
                if (GlobalProtect.TryGetValue(myEntity, out protector)) {

                    foreach (var s in protector.Shields.Keys) {

                        ProtectCache cache;
                        if (s.ProtectedEntCache.TryRemove(myEntity, out cache))
                            ProtectCachePool.Return(cache);
                    }
                    EntRefreshQueue.Enqueue(myEntity);
                }
            }
        }

        private void OnSessionReady()
        {
            SessionReady = true;
        }
        #endregion
    }
}
