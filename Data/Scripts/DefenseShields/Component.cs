﻿using Sandbox.Game;
using VRageMath;
using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using System.Linq;
using DefenseShields.Support;
using Sandbox.Game.Entities;
using VRage.Voxels;
using CollisionLayers = Sandbox.Engine.Physics.MyPhysics.CollisionLayers;


namespace DefenseShields
{
    public static class MathematicalConstants
    {
        public const double Sqrt2 = 1.414213562373095048801688724209698078569671875376948073176679737990732478462107038850387534327641573;
        public const double Sqrt3 = 1.7320508075689d;
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_OreDetector), false, "DefenseShieldsLS", "DefenseShieldsSS", "DefenseShieldsST")]
    public partial class DefenseShields : MyGameLogicComponent
    {
        #region Simulation
        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (Debug == 1) Dsutil1.Sw.Restart();
                _tick = (uint)MyAPIGateway.Session.ElapsedPlayTime.TotalMilliseconds / MyEngineConstants.UPDATE_STEP_SIZE_IN_MILLISECONDS;
                if (!BlockFunctional()) return;

                if (ServerUpdate) SyncControlsServer();
                SyncControlsClient();

                if (GridIsMobile) MobileUpdate();
                if (_updateDimensions) RefreshDimensions();

                if (_longLoop == 0 && _blocksChanged)
                {
                    _blocksChanged = false;
                    MyAPIGateway.Parallel.StartBackground(BackGroundChecks);
                    CheckShieldLineOfSight();
                }
                if (_shieldLineOfSight == false && !Session.DedicatedServer) DrawHelper();

                ShieldActive = BlockWorking && _shieldLineOfSight;
                if (_prevShieldActive == false && BlockWorking) _shieldStarting = true;
                else if (_shieldStarting && _prevShieldActive && ShieldActive) _shieldStarting = false;
                _prevShieldActive = ShieldActive;

                if (_count++ == 59)
                {
                    _count = 0;
                    _longLoop++;
                    if (_longLoop == 10) _longLoop = 0;
                }

                if (_staleGrids.Count != 0) CleanUp(0);
                if (_longLoop == 9 && _count == 58) CleanUp(1);
                if (_effectsCleanup && (_count == 1 || _count == 21 || _count == 41)) CleanUp(2);
                if (_longLoop % 2 == 0 && _count == 5) CleanUp(3);
                if (_longLoop == 6 && _count == 35) CleanUp(4);
                if (_longLoop == 7 && _count == 30 && (Session.DedicatedServer || Session.IsServer)) SaveSettings();

                UpdateGridPower();
                CalculatePowerCharge();
                SetPower();

                if (_count == 29)
                {
                    if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
                    {
                        Shield.ShowInToolbarConfig = false;
                        Shield.ShowInToolbarConfig = true;
                    }
                    else if (_longLoop == 0 || _longLoop == 5)
                    {
                        Shield.RefreshCustomInfo();
                    }
                    _shieldDps = 0f;
                }
                if (_shieldStarting && GridIsMobile && FieldShapeBlocked()) return;
                if (ShieldActive)
                {
                    if (_shieldStarting)
                    {
                        if (!(_hidePassiveCheckBox.Getter(Shield).Equals(true))) _shellPassive.Render.UpdateRenderObject(true);

                        _shellActive.Render.UpdateRenderObject(true);
                        _shellActive.Render.UpdateRenderObject(false);
                        _shield.Render.Visible = true;
                        _shield.Render.UpdateRenderObject(true);
                        SyncThreadedEnts(true);
                        if (!GridIsMobile) EllipsoidOxyProvider.UpdateMatrix(_detectMatrixOutsideInv);
                        if (_warmUp) //smooth out init latency.
                        {
                            _warmUp = false;
                            if (Debug == 1) Log.Line($"Warmup complete");
                            return;
                        }
                    }
                    if (_subpartRotor.Closed.Equals(true)) BlockMoveAnimationReset();
                    if ((!Session.DedicatedServer) && Distance(1000))
                    {
                        if (_shieldMoving || _shieldStarting) BlockParticleUpdate();
                        var blockCam = Shield.PositionComp.WorldVolume;
                        if (MyAPIGateway.Session.Camera.IsInFrustum(ref blockCam))
                        {
                            if (_blockParticleStopped) BlockParticleStart();
                            _blockParticleStopped = false;
                            BlockMoveAnimation();

                            if (_animationLoop++ == 599) _animationLoop = 0;
                        }
                    }
                    SyncThreadedEnts();
                    _enablePhysics = false;
                    WebEntities();
                }
                else
                {
                    SyncThreadedEnts();
                    if (!_blockParticleStopped) BlockParticleStop();
                }
                if (Debug == 1) Dsutil1.StopWatchReport($"MainLoop: ShieldId:{Shield.EntityId.ToString()} - Active: {ShieldActive} - Tick: {_tick} loop: {_longLoop}-{_count}", 4);
            }
            catch (Exception ex) {Log.Line($"Exception in UpdateBeforeSimulation: {ex}"); }
        }
        #endregion

        #region Field Check
        private void CheckShieldLineOfSight()
        {
            if (GridIsMobile)
            {
                MobileUpdate();
                Icosphere.ReturnPhysicsVerts(DetectionMatrix, PhysicsOutside);
            }
            else RefreshDimensions();

            var testDist = 0d;
            _blocksLos.Clear();
            _noBlocksLos.Clear();
            _vertsSighted.Clear();
            if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsLS") testDist = 4.5d;
            else if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsSS") testDist = 0.8d;
            else if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsST") testDist = 8.0d;

            var testDir = _subpartRotor.PositionComp.WorldVolume.Center - Shield.PositionComp.WorldVolume.Center;
            testDir.Normalize();
            var testPos = Shield.PositionComp.WorldVolume.Center + testDir * testDist;
            _sightPos = testPos;

            MyAPIGateway.Parallel.For(0, PhysicsOutside.Length, i =>
            {
                var hit = Shield.CubeGrid.RayCastBlocks(testPos, PhysicsOutside[i]);
                if (hit.HasValue)
                {
                    _blocksLos.Add(i);
                    return;
                }
                _noBlocksLos.Add(i);
            });
            if (GridIsMobile)
            {
                MyAPIGateway.Parallel.For(0, _noBlocksLos.Count, i =>
                {
                    const int filter = CollisionLayers.VoxelCollisionLayer;
                    IHitInfo hitInfo;
                    var hit = MyAPIGateway.Physics.CastRay(testPos, PhysicsOutside[_noBlocksLos[i]], out hitInfo, filter);
                    if (hit) _blocksLos.Add(_noBlocksLos[i]);
                });
            }

            for (int i = 0; i < PhysicsOutside.Length; i++) if (!_blocksLos.Contains(i)) _vertsSighted.Add(i);
            _shieldLineOfSight = _blocksLos.Count < 500;
            if (Debug == 1) Log.Line($"ShieldId:{Shield.EntityId.ToString()} - blocked verts {_blocksLos.Count.ToString()} - visable verts: {_vertsSighted.Count.ToString()} - LoS: {_shieldLineOfSight.ToString()}");
        }

        private void DrawHelper()
        {
            var lineDist = 0d;
            const float lineWidth = 0.025f;
            if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsLS") lineDist = 5.0d;
            else if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsSS") lineDist = 3d;
            else if (Shield.BlockDefinition.SubtypeId == "DefenseShieldsST") lineDist = 7.5d;

            foreach (var blocking in _blocksLos)
            {
                var blockedDir = PhysicsOutside[blocking] - _sightPos;
                blockedDir.Normalize();
                var blockedPos = _sightPos + blockedDir * lineDist;
                DsDebugDraw.DrawLineToVec(_sightPos, blockedPos, Color.Black, lineWidth);
            }

            foreach (var sighted in _vertsSighted)
            {
                var sightedDir = PhysicsOutside[sighted] - _sightPos;
                sightedDir.Normalize();
                var sightedPos = _sightPos + sightedDir * lineDist;
                DsDebugDraw.DrawLineToVec(_sightPos, sightedPos, Color.Blue, lineWidth);
            }
            if (_count == 0) MyVisualScriptLogicProvider.ShowNotification("The shield emitter DOES NOT have a CLEAR ENOUGH LINE OF SIGHT to the shield, SHUTTING DOWN.", 960, "Red", Shield.OwnerId);
            if (_count == 0) MyVisualScriptLogicProvider.ShowNotification("Blue means clear line of sight, black means blocked......................................................................", 960, "Red", Shield.OwnerId);
        }

        private bool FieldShapeBlocked()
        {
            var pruneSphere = new BoundingSphereD(_detectionCenter, Range);
            var pruneList = new List<MyVoxelBase>();
            MyGamePruningStructure.GetAllVoxelMapsInSphere(ref pruneSphere, pruneList);

            if (pruneList.Count == 0) return false;
            MobileUpdate();
            Icosphere.ReturnPhysicsVerts(_detectMatrixOutside, PhysicsOutsideLow);
            foreach (var voxel in pruneList)
            {
                if (voxel.RootVoxel == null) continue;

                if (!CustomCollision.VoxelContact(Shield.CubeGrid, PhysicsOutsideLow, voxel, new MyStorageData(), _detectMatrixOutside)) continue;

                Shield.Enabled = false;
                MyVisualScriptLogicProvider.ShowNotification("The shield's field cannot form when in contact with a solid body", 6720, "Blue", Shield.OwnerId);
                return true;
            }
            return false;
        }
        #endregion

        #region Shield Shape
        private void MobileUpdate()
        {
            _sVelSqr = Shield.CubeGrid.Physics.LinearVelocity.LengthSquared();
            _sAvelSqr = Shield.CubeGrid.Physics.AngularVelocity.LengthSquared();
            if (_sVelSqr > 0.00001 || _sAvelSqr > 0.00001 || _shieldStarting) _shieldMoving = true;
            else _shieldMoving = false;

            _gridChanged = _oldGridAabb != Shield.CubeGrid.LocalAABB;
            _oldGridAabb = Shield.CubeGrid.LocalAABB;
            _entityChanged = Shield.CubeGrid.Physics.IsMoving || _gridChanged || _shieldStarting;
            if (_entityChanged || Range <= 0 || _shieldStarting) CreateShieldShape();
        }

        private void CreateShieldShape()
        {
            if (GridIsMobile)
            {
                _shieldGridMatrix = Shield.CubeGrid.WorldMatrix;
                if (_gridChanged) CreateMobileShape();
                DetectionMatrix = _shieldShapeMatrix * _shieldGridMatrix;
                _detectionCenter = Shield.CubeGrid.PositionComp.WorldVolume.Center;
                _sQuaternion = Quaternion.CreateFromRotationMatrix(Shield.CubeGrid.WorldMatrix);
                _sOriBBoxD = new MyOrientedBoundingBoxD(_detectionCenter, ShieldSize, _sQuaternion);
                _shieldAabb = new BoundingBox(ShieldSize, -ShieldSize);
                _shieldSphere = new BoundingSphereD(Shield.PositionComp.LocalVolume.Center, ShieldSize.AbsMax());
                EllipsoidSa.Update(_detectMatrixOutside.Scale.X, _detectMatrixOutside.Scale.Y, _detectMatrixOutside.Scale.Z);
            }
            else
            {
                _shieldGridMatrix = Shield.WorldMatrix;
                DetectionMatrix = MatrixD.Rescale(_shieldGridMatrix, new Vector3D(Width, Height, Depth));
                _shieldShapeMatrix = MatrixD.Rescale(Shield.LocalMatrix, new Vector3D(Width, Height, Depth));
                ShieldSize = DetectionMatrix.Scale;
                _detectionCenter = Shield.PositionComp.WorldVolume.Center;
                _sQuaternion = Quaternion.CreateFromRotationMatrix(Shield.CubeGrid.WorldMatrix);
                _sOriBBoxD = new MyOrientedBoundingBoxD(_detectionCenter, ShieldSize, _sQuaternion);
                _shieldAabb = new BoundingBox(ShieldSize, -ShieldSize);
                _shieldSphere = new BoundingSphereD(Shield.PositionComp.LocalVolume.Center, ShieldSize.AbsMax());
                EllipsoidSa.Update(_detectMatrixOutside.Scale.X, _detectMatrixOutside.Scale.Y, _detectMatrixOutside.Scale.Z);
            }
            Range = ShieldSize.AbsMax() + 7.5f;
            _ellipsoidSurfaceArea = EllipsoidSa.Surface;
            SetShieldShape();
        }

        private void CreateMobileShape()
        {
            Vector3D gridHalfExtents = Shield.CubeGrid.PositionComp.LocalAABB.HalfExtents;

            const double ellipsoidAdjust = MathematicalConstants.Sqrt2;
            const double buffer = 2.5d;
            var shieldSize = gridHalfExtents * ellipsoidAdjust + buffer;
            ShieldSize = shieldSize;
            var mobileMatrix = MatrixD.CreateScale(shieldSize);
            mobileMatrix.Translation = Shield.CubeGrid.PositionComp.LocalVolume.Center;
            _shieldShapeMatrix = mobileMatrix;
        }

        private void SetShieldShape()
        {
            _shellPassive.PositionComp.LocalMatrix = Matrix.Zero;  // Bug - Cannot just change X coord, so I reset first.
            _shellActive.PositionComp.LocalMatrix = Matrix.Zero;
            _shield.PositionComp.LocalMatrix = Matrix.Zero;

            _shellPassive.PositionComp.LocalMatrix = _shieldShapeMatrix;
            _shellActive.PositionComp.LocalMatrix = _shieldShapeMatrix;
            _shield.PositionComp.LocalMatrix = _shieldShapeMatrix;
            _shield.PositionComp.LocalAABB = _shieldAabb;

            var matrix = _shieldShapeMatrix * Shield.WorldMatrix;
            _shield.PositionComp.SetWorldMatrix(matrix);
            _shield.PositionComp.SetPosition(_detectionCenter);

            if (!GridIsMobile) EllipsoidOxyProvider.UpdateMatrix(_detectMatrixOutsideInv);
        }

        private void RefreshDimensions()
        {

            if (!_updateDimensions) return;
            _updateDimensions = false;
            CreateShieldShape();
            Icosphere.ReturnPhysicsVerts(DetectionMatrix, PhysicsOutside);
            _entityChanged = true;
        }
        #endregion

        #region Block Power Logic
        private bool BlockFunctional()
        {
            if (!MainInit || !AnimateInit || NoPower || HardDisable) return false;

            if (Range.Equals(0)) // populate matrices and prep for smooth init.
            {
                _updateDimensions = true;
                if (GridIsMobile) MobileUpdate();
                else RefreshDimensions();
                Icosphere.ReturnPhysicsVerts(DetectionMatrix, PhysicsOutside);
                BackGroundChecks();
                UpdateGridPower();
                return false;
            }
            var shieldPowerUsed = Sink.CurrentInputByType(GId);

            if (((MyCubeGrid)Shield.CubeGrid).GetFatBlocks().Count < 2 && ShieldActive && !Session.MpActive)
            {
                if (Debug == 1) Log.Line($"Shield going critical");
                MyVisualScriptLogicProvider.CreateExplosion(Shield.PositionComp.WorldVolume.Center, (float)Shield.PositionComp.WorldVolume.Radius * 1.25f, 2500);
                return false;
            }

            if (!Shield.IsWorking && Shield.Enabled && Shield.IsFunctional && shieldPowerUsed > 0)
            {
                if (Debug == 1) Log.Line($"fixing shield state power: {_power.ToString()}");
                Shield.Enabled = false;
                Shield.Enabled = true;
                return true;
            }

            if ((!Shield.IsWorking || !Shield.IsFunctional || _shieldDownLoop > -1))
            {
                _shieldCurrentPower = Sink.CurrentInputByType(GId);
                UpdateGridPower();
                if (!GridIsMobile) EllipsoidOxyProvider.UpdateMatrix(MatrixD.Zero);
                BlockParticleStop();
                ShieldActive = false;
                BlockWorking = false;
                _prevShieldActive = false;
                _shellPassive.Render.UpdateRenderObject(false);
                _shellActive.Render.UpdateRenderObject(false);
                _shield.Render.Visible = false;
                _shield.Render.UpdateRenderObject(false);
                Absorb = 0;
                ShieldBuffer = 0;
                _shieldChargeRate = 0;
                _shieldMaxChargeRate = 0;
                if (_shieldDownLoop > -1)
                {
                    _power = _gridMaxPower * _shieldMaintaintPower;
                    if (_power < 0 || float.IsNaN(_power)) _power = 0.0001f; // temporary definitely 100% will fix this to do - Find ThE NaN!
                    Sink.Update();

                    if (_shieldDownLoop == 0)
                    {
                        var realPlayerIds = new List<long>();
                        DsUtilsStatic.GetRealPlayers(Shield.PositionComp.WorldVolume.Center, 500f, realPlayerIds);
                        foreach (var id in realPlayerIds)
                        {
                            MyVisualScriptLogicProvider.ShowNotification("[ " + Shield.CubeGrid.DisplayName + " ]" + " -- shield has overloaded, restarting in 20 seconds!!", 19200, "Red", id);
                        }

                        CleanUp(0);
                        CleanUp(1);
                        CleanUp(3);
                        CleanUp(4);
                    }

                    _shieldDownLoop++;
                    if (_shieldDownLoop == 1200)
                    {
                        _shieldDownLoop = -1;
                        var nerf = ShieldNerf > 0 && ShieldNerf < 1;
                        var nerfer = nerf ? ShieldNerf : 1f;
                        ShieldBuffer = (_shieldMaxBuffer / 25) * nerfer; // replace this with something that scales based on charge rate
                    }
                    return false;
                }
                _power = 0.0001f;
                Sink.Update();
                return false;
            }

            var blockCount = ((MyCubeGrid)Shield.CubeGrid).BlocksCount;
            if (!_blocksChanged) _blocksChanged = blockCount != _oldBlockCount;
            _oldBlockCount = blockCount;

            BlockWorking = MainInit && AnimateInit && Shield.IsWorking && Shield.IsFunctional;
            return BlockWorking;
        }

        private void BackGroundChecks()
        {
            lock (_powerSources) _powerSources.Clear();
            lock (_functionalBlocks) _functionalBlocks.Clear();

            foreach (var block in ((MyCubeGrid)Shield.CubeGrid).GetFatBlocks())
            {
                lock (_functionalBlocks) if (block.IsFunctional) _functionalBlocks.Add(block);
                var source = block.Components.Get<MyResourceSourceComponent>();
                if (source == null) continue;
                foreach (var type in source.ResourceTypes)
                {
                    if (type != MyResourceDistributorComponent.ElectricityId) continue;
                    lock (_powerSources) _powerSources.Add(source);
                    break;
                }
            }
            if (Debug == 1) Log.Line($"ShieldId:{Shield.EntityId.ToString()} - powerCnt: {_powerSources.Count.ToString()}");
        }

        private void UpdateGridPower()
        {
            _gridMaxPower = 0;
            _gridCurrentPower = 0;

            lock (_powerSources)
                for (int i = 0; i < _powerSources.Count; i++)
                {
                    var source = _powerSources[i];
                    if (!source.Enabled || !source.ProductionEnabled) continue;
                    _gridMaxPower += source.MaxOutput;
                    _gridCurrentPower += source.CurrentOutput;
                }
            _gridAvailablePower = _gridMaxPower - _gridCurrentPower;
        }

        private void CalculatePowerCharge()
        {
            var nerf = ShieldNerf > 0 && ShieldNerf < 1;
            var rawNerf = nerf ? ShieldNerf : 1f;
            var nerfer = rawNerf / _shieldRatio;

            var shieldVol = _detectMatrixOutside.Scale.Volume;
            var powerForShield = 0f;
            const float ratio = 1.25f;
            var rate = _chargeSlider?.Getter(Shield) ?? 20f;
            var percent = rate * ratio;
            var shieldMaintainCost = 1 / percent;
            _shieldMaintaintPower = shieldMaintainCost;
            var fPercent = (percent / ratio) / 100;
            _sizeScaler = (shieldVol / _ellipsoidSurfaceArea) / 2.40063050674088;

            if (ShieldBuffer > 0 && _shieldCurrentPower < 0.00000000001f) // is this even needed anymore?
            {
                Log.Line($"ShieldId:{Shield.EntityId.ToString()} - if u see this it is needed");
                if (ShieldBuffer > _gridMaxPower * shieldMaintainCost) ShieldBuffer -= _gridMaxPower * shieldMaintainCost;
                else ShieldBuffer = 0f;
            }

            _shieldCurrentPower = Sink.CurrentInputByType(GId);

            var otherPower = _gridMaxPower - _gridAvailablePower - _shieldCurrentPower;
            var cleanPower = _gridMaxPower - otherPower;
            powerForShield = (cleanPower * fPercent);

            _shieldMaxChargeRate = powerForShield > 0 ? powerForShield : 0f;
            _shieldMaxBuffer = ((_gridMaxPower * (100 / percent) * ShieldBaseScaler) / (float)_sizeScaler) * nerfer;

            if (_sizeScaler < 1)
            {
                if (ShieldBuffer + (_shieldMaxChargeRate * nerfer) < _shieldMaxBuffer) _shieldChargeRate = (_shieldMaxChargeRate * nerfer);
                else if (_shieldMaxBuffer - ShieldBuffer > 0) _shieldChargeRate = _shieldMaxBuffer - ShieldBuffer;
                else _shieldMaxChargeRate = 0f;
                _shieldConsumptionRate = _shieldChargeRate;
            }
            else if (ShieldBuffer + (_shieldMaxChargeRate / (_sizeScaler / nerfer)) < _shieldMaxBuffer)
            {
                _shieldChargeRate = _shieldMaxChargeRate / ((float)_sizeScaler / nerfer);
                _shieldConsumptionRate = _shieldMaxChargeRate;
            }
            else
            {
                if (_shieldMaxBuffer - ShieldBuffer > 0)
                {
                    _shieldChargeRate = _shieldMaxBuffer - ShieldBuffer;
                    _shieldConsumptionRate = _shieldChargeRate;
                }
                else _shieldMaxChargeRate = 0f;
            }

            if (_shieldMaxChargeRate < 0.001f)
            {
                _shieldChargeRate = 0f;
                _shieldConsumptionRate = 0f;
                if (ShieldBuffer > _shieldMaxBuffer)  ShieldBuffer = _shieldMaxBuffer;
                return;
            }

            if (ShieldBuffer < _shieldMaxBuffer && _count == 29)
            {
                ShieldBuffer += _shieldChargeRate;
            }
            if (_count == 29)
            {
                _shieldPercent = 100f;
                if (ShieldBuffer < _shieldMaxBuffer) _shieldPercent = (ShieldBuffer / _shieldMaxBuffer) * 100;
                else _shieldPercent = 100f;
            }
        }

        private double PowerCalculation(IMyEntity breaching)
        {
            var bPhysics = breaching.Physics;
            var sPhysics = Shield.CubeGrid.Physics;

            const double wattsPerNewton = (3.36e6 / 288000);
            var velTarget = sPhysics.GetVelocityAtPoint(breaching.Physics.CenterOfMassWorld);
            var accelLinear = sPhysics.LinearAcceleration;
            var velTargetNext = velTarget + accelLinear * MyEngineConstants.PHYSICS_STEP_SIZE_IN_SECONDS;
            var velModifyNext = bPhysics.LinearVelocity;
            var linearImpulse = bPhysics.Mass * (velTargetNext - velModifyNext);
            var powerCorrectionInJoules = wattsPerNewton * linearImpulse.Length();

            return powerCorrectionInJoules * MyEngineConstants.PHYSICS_STEP_SIZE_IN_SECONDS;
        }

        private void SetPower()
        {
            _power = _shieldConsumptionRate + _gridMaxPower * _shieldMaintaintPower;
            if (_power <= 0 || float.IsNaN(_power)) _power = 0.0001f; // temporary definitely 100% will fix this to do - Find ThE NaN!
            Sink.Update();

            _shieldCurrentPower = Sink.CurrentInputByType(GId);
            if (Absorb > 0)
            {
                _shieldDps += Absorb;
                _effectsCleanup = true;
                ShieldBuffer -= (Absorb / ShieldEfficiency);
            }
            else if (Absorb < 0) ShieldBuffer += (Absorb / ShieldEfficiency);

            if (ShieldBuffer < 0)
            {
                //Log.Line($"bufffer size: {ShieldBuffer}");
                _shieldDownLoop = 0;
            }
            else if (ShieldBuffer > _shieldMaxBuffer) ShieldBuffer = _shieldMaxBuffer;

            Absorb = 0f;
        }

        private void AppendingCustomInfo(IMyTerminalBlock block, StringBuilder stringBuilder)
        {
            var shieldPercent = 100f;
            var secToFull = 0;
            if (ShieldBuffer < _shieldMaxBuffer) shieldPercent = (ShieldBuffer / _shieldMaxBuffer) * 100;
            if (_shieldChargeRate > 0) secToFull = (int) ((_shieldMaxBuffer - ShieldBuffer) / _shieldChargeRate);
            stringBuilder.Append("[Shield Status] MaxHP: " + (_shieldMaxBuffer * ShieldEfficiency).ToString("N0") +
                                 "\n" +
                                 "\n[Shield HP__]: " + (ShieldBuffer * ShieldEfficiency).ToString("N0") + " (" + shieldPercent.ToString("0") + "%)" +
                                 "\n[HP Per Sec_]: " + (_shieldChargeRate * ShieldEfficiency).ToString("N0") +
                                 "\n[DPS_______]: " + (_shieldDps).ToString("N0") +
                                 "\n[Charge Rate]: " + _shieldChargeRate.ToString("0.0") + " Mw" +
                                 "\n[Full Charge_]: " + secToFull.ToString("N0") + "s" +
                                 "\n[Efficiency__]: " + ShieldEfficiency.ToString("0.0") +
                                 "\n[Maintenance]: " + (_gridMaxPower * _shieldMaintaintPower).ToString("0.0") + " Mw" +
                                 "\n[Availabile]: " + _gridAvailablePower.ToString("0.0") + " Mw" +
                                 "\n[Current__]: " + Sink.CurrentInputByType(GId).ToString("0.0"));
        }
        #endregion

        #region Block Animation
        private void BlockMoveAnimationReset()
        {
            if (Debug == 1) Log.Line($"Resetting BlockMovement - Tick:{_tick.ToString()}");
            _subpartRotor.Subparts.Clear();
            Entity.TryGetSubpart("Rotor", out _subpartRotor);
        }

        private void BlockMoveAnimation()
        {
            _time -= 1;
            if (_animationLoop == 0) _time2 = 0;
            if (_animationLoop < 299) _time2 += 1;
            else _time2 -= 1;
            if (_count == 0) _emissiveIntensity = 2;
            if (_count < 30) _emissiveIntensity += 1;
            else _emissiveIntensity -= 1;
                
            var temp1 = MatrixD.CreateRotationY(0.05f * _time);
            var temp2 = MatrixD.CreateTranslation(0, 0.002f * _time2, 0);
            _subpartRotor.PositionComp.LocalMatrix = temp1 * temp2;
            _subpartRotor.SetEmissiveParts("PlasmaEmissive", Color.Aqua, 0.1f * _emissiveIntensity);
        }

        private void BlockParticleCreate()
        {
            for (int i = 0; i < _effects.Length; i++)
            {
                if (_effects[i] == null)
                {
                    if (Debug == 1) Log.Line($"Particle #{i.ToString()} is null, creating - tick:{_tick.ToString()}");
                    MyParticlesManager.TryCreateParticleEffect("EmitterEffect", out _effects[i]);
                    if (_effects[i] == null) continue;
                    _effects[i].UserScale = 1f;
                    _effects[i].UserRadiusMultiplier = 10f;
                    _effects[i].UserEmitterScale = 1f;
                }

                if (_effects[i] != null)
                {
                    _effects[i].WorldMatrix = _subpartRotor.WorldMatrix;
                    _effects[i].Stop();
                    _blockParticleStopped = true;
                }
            }
        }

        private void BlockParticleUpdate()
        {
            var predictedMatrix = Shield.PositionComp.WorldMatrix;
            if (_sVelSqr > 4000) predictedMatrix.Translation = Shield.PositionComp.WorldMatrix.Translation + Shield.CubeGrid.Physics.GetVelocityAtPoint(Shield.PositionComp.WorldMatrix.Translation) * MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
            for (int i = 0; i < _effects.Length; i++)
                if (_effects[i] != null)
                {
                    _effects[i].WorldMatrix = predictedMatrix;
                }
        }

        private void BlockParticleStop()
        {
            _blockParticleStopped = true;
            for (int i = 0; i < _effects.Length; i++)
            {
                if (_effects[i] != null)
                {
                    _effects[i].Stop();
                    _effects[i].Close(false, true);
                }
            }

        }

        private void BlockParticleStart()
        {
            for (int i = 0; i < _effects.Length; i++)
            {
                if (!_effects[i].IsStopped) continue;

                MyParticlesManager.TryCreateParticleEffect("EmitterEffect", out _effects[i]);
                _effects[i].UserScale = 1f;
                _effects[i].UserRadiusMultiplier = 10f;
                _effects[i].UserEmitterScale = 1f;
                BlockParticleUpdate();
            }
        }
        #endregion
      
        #region Shield Draw
        public void Draw(int onCount, bool sphereOnCamera)
        {
            _onCount = onCount;
            var enemy = false;
            var relation = MyAPIGateway.Session.Player.GetRelationTo(Shield.OwnerId);
            if (relation == MyRelationsBetweenPlayerAndBlock.Neutral || relation == MyRelationsBetweenPlayerAndBlock.Enemies) enemy = true;
            _enemy = enemy;

            var passiveVisible = !(_hidePassiveCheckBox.Getter(Shield).Equals(true) && !enemy);
            var activeVisible = !(_hideActiveCheckBox.Getter(Shield).Equals(true) && !enemy);

            if (!passiveVisible && !_hideShield)
            {
                _hideShield = true;
                _shellPassive.Render.UpdateRenderObject(false);
            }
            else if (passiveVisible && _hideShield)
            {
                _hideShield = false;
                _shellPassive.Render.UpdateRenderObject(true);
            }

            if (BulletCoolDown > -1) BulletCoolDown++;
            if (BulletCoolDown > 9) BulletCoolDown = -1;
            if (EntityCoolDown > -1) EntityCoolDown++;
            if (EntityCoolDown > 9) EntityCoolDown = -1;

            var impactPos = WorldImpactPosition;
            _localImpactPosition = Vector3D.NegativeInfinity;
            if (impactPos != Vector3D.NegativeInfinity & ((BulletCoolDown == -1 && EntityCoolDown == -1)))
            {
                if (EntityCoolDown == -1 && ImpactSize > 5) EntityCoolDown = 0;
                BulletCoolDown = 0;

                var cubeBlockLocalMatrix = Shield.CubeGrid.LocalMatrix;
                var referenceWorldPosition = cubeBlockLocalMatrix.Translation;
                var worldDirection = impactPos - referenceWorldPosition;
                var localPosition = Vector3D.TransformNormal(worldDirection, MatrixD.Transpose(cubeBlockLocalMatrix));
                _localImpactPosition = localPosition;
            }
            WorldImpactPosition = Vector3D.NegativeInfinity;

            if (Shield.IsWorking)
            {
                var prevlod = _prevLod;
                var lod = CalculateLod(_onCount);
                if (_gridChanged || lod != prevlod) Icosphere.CalculateTransform(_shieldShapeMatrix, lod);
                Icosphere.ComputeEffects(_shieldShapeMatrix, _localImpactPosition, _shellPassive, _shellActive, prevlod, _shieldPercent, passiveVisible, activeVisible);
                _entityChanged = false;
            }
            if (sphereOnCamera && Shield.IsWorking) Icosphere.Draw(GetRenderId());
        }

        private bool Distance(int x)
        {
            if (MyAPIGateway.Session.Player.Character == null) return false;

            var pPosition = MyAPIGateway.Session.Player.Character.GetPosition();
            var cPosition = Shield.CubeGrid.PositionComp.GetPosition();
            var range = Vector3D.DistanceSquared(cPosition, pPosition) <= (x + Range) * (x + Range);
            return range;
        }

        private int CalculateLod(int onCount)
        {
            var lod = 4;

            if (onCount > 20) lod = 2;
            else if (onCount > 10) lod = 3;

            _prevLod = lod;
            return lod;
        }

        private uint GetRenderId()
        {
            return Shield.CubeGrid.Render.GetRenderObjectID();
        }
        #endregion

        #region Cleanup
        private void CleanUp(int task)
        {
            try
            {
                switch (task)
                {
                    case 0:
                        IMyCubeGrid grid;
                        while (_staleGrids.TryDequeue(out grid)) lock (_webEnts) _webEnts.Remove(grid);
                        break;
                    case 1:
                        lock (_webEnts)
                        {
                            _webEntsTmp.AddRange(_webEnts.Where(info => _tick - info.Value.FirstTick > 599 && _tick - info.Value.LastTick > 1));
                            foreach (var webent in _webEntsTmp) _webEnts.Remove(webent.Key);
                        }
                        break;
                    case 2:
                        lock (_functionalBlocks)
                        {
                            foreach (var funcBlock in _functionalBlocks) funcBlock.SetDamageEffect(false);
                            _effectsCleanup = false;
                        }
                        break;
                    case 3:
                        {
                            FriendlyCache.Clear();
                        }
                        break;
                    case 4:
                        {
                            IgnoreCache.Clear();
                        }
                        break;
                }
            }
            catch (Exception ex) { Log.Line($"Exception in CleanUp: {ex}"); }
        }

        public override void OnAddedToScene()
        {
            try
            {
                /*
                if (!Entity.MarkedForClose)
                {
                    //Log.Line("Entity not closed in OnAddedToScene - gridSplit?.");
                    return;
                }
                */
                //Log.Line("Entity closed in OnAddedToScene.");
                //Session.Instance.Components.Add(this);
                //Icosphere = new Icosphere.Instance(Session.Instance.Icosphere);
                //Shield.CubeGrid.Components.Add(new ShieldGridComponent(this));
            }
            catch (Exception ex) { Log.Line($"Exception in OnAddedToScene: {ex}"); }
        }

        public override void OnRemovedFromScene()
        {
            try
            {
                //Log.Line("OnremoveFromScene");
                if (!Entity.MarkedForClose)
                {
                    //Log.Line("Entity not closed in OnRemovedFromScene- gridSplit?.");
                    return;
                }
                //Log.Line("Entity closed in OnRemovedFromScene.");
                _power = 0f;
                if (MainInit) Sink.Update();
                Icosphere = null;
                _shield?.Close();
                _shellPassive?.Close();
                _shellActive?.Close();
                BlockParticleStop();
                Shield?.CubeGrid.Components.Remove(typeof(ShieldGridComponent), this);
                MyAPIGateway.Session.OxygenProviderSystem.RemoveOxygenGenerator(EllipsoidOxyProvider);
                Session.Instance.Components.Remove(this);
            }
            catch (Exception ex) { Log.Line($"Exception in OnRemovedFromScene: {ex}"); }
        }

        public override void OnAddedToContainer() { if (Entity.InScene) OnAddedToScene(); }
        public override void OnBeforeRemovedFromContainer() { if (Entity.InScene) OnRemovedFromScene(); }
        public override void Close()
        {
            try
            {
                //Log.Line("Close");
                if (Session.Instance.Components.Contains(this)) Session.Instance.Components.Remove(this);
                _power = 0f;
                Icosphere = null;
                MyAPIGateway.Session.OxygenProviderSystem.RemoveOxygenGenerator(EllipsoidOxyProvider);
                if (MainInit) Sink.Update();
                BlockParticleStop();
            }
            catch (Exception ex) { Log.Line($"Exception in Close: {ex}"); }
            base.Close();
        }

        public override void MarkForClose()
        {
            try
            {
            }
            catch (Exception ex) { Log.Line($"Exception in MarkForClose: {ex}"); }
            base.MarkForClose();
        }
        #endregion
    }
}