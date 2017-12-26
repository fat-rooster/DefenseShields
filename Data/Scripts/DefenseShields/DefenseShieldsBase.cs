﻿using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace DefenseShields.Base
{
    #region Session+protection Class

    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation)]
    public class DefenseShieldsBase : MySessionComponentBase
    {
        public static bool IsInit;
        private static List<Station.DefenseShields> _bulletShields = new List<Station.DefenseShields>(); // check 
        public static bool ControlsLoaded;

        // Initialisation

        protected override void UnloadData()
        {
            Logging.WriteLine("Logging stopped.");
            Logging.Close();
        }

        public override void UpdateBeforeSimulation()
        {
            if (IsInit) return;
            if (MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Utilities.IsDedicated) Init();
            else if (MyAPIGateway.Session.Player != null) Init();
        }

        public static void Init()
        {
            Logging.Init("debugdevelop.log");
            Logging.WriteLine(String.Format("{0} - Logging Started", DateTime.Now.ToString("MM-dd-yy_HH-mm-ss-fff")));
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, CheckDamage);
            IsInit = true;
        }

        // Prevent damage by bullets fired from outside zone.

        public static void CheckDamage(object block, ref MyDamageInformation info)
        {
            if (info.Type == MyDamageType.Deformation) // move below, modify match Type to 
            {
            }

            if (_bulletShields.Count == 0 || info.Type != MyDamageType.Bullet) return;

            Station.DefenseShields generator = _bulletShields[0];
            IMyEntity ent = block as IMyEntity;
            var slimBlock = block as IMySlimBlock;
            if (slimBlock != null) ent = slimBlock.CubeGrid;
            var dude = block as IMyCharacter;
            if (dude != null) ent = dude;
            if (ent == null) return;
            bool isProtected = false;
            foreach (var shield in _bulletShields)
                if (shield._inHash.Contains(ent))
                {
                    isProtected = true;
                    generator = shield;
                }
            if (!isProtected) return;
            IMyEntity attacker;
            if (!MyAPIGateway.Entities.TryGetEntityById(info.AttackerId, out attacker)) return;
            if (generator._inHash.Contains(attacker)) return;
            info.Amount = 0f;
        }
    }
    #endregion
}