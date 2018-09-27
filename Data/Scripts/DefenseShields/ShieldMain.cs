﻿using System;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game.Components;
using System.Linq;
using DefenseShields.Support;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRageMath;

namespace DefenseShields
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "DSControlLarge", "DSControlSmall", "DSControlTable")]
    public partial class DefenseShields : MyGameLogicComponent
    {
        #region Simulation
        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (Session.Enforced.Debug >= 1) Dsutil1.Sw.Restart();
                UpdateFields();
                if (!ShieldOn())
                {
                    if (Session.Enforced.Debug >= 1 && WasOnline) Log.Line($"Off: WasOn:{WasOnline} - On:{DsState.State.Online} - Active:{DsSet.Settings.ShieldActive}({_prevShieldActive}) - Buff:{DsState.State.Buffer} - Sus:{DsState.State.Suspended} - EW:{DsState.State.EmitterWorking} - Perc:{DsState.State.ShieldPercent} - Wake:{DsState.State.Waking} - ShieldId [{Shield.EntityId}]");
                    if (WasOnline) ShieldOff();
                    else if (DsState.State.Message) ShieldChangeState();
                    return;
                }
                if (Session.Enforced.Debug >= 1 && !WasOnline)
                {
                    Log.Line($"On: WasOn:{WasOnline} - On:{DsState.State.Online} - Active:{DsSet.Settings.ShieldActive}({_prevShieldActive}) - Buff:{DsState.State.Buffer} - Sus:{DsState.State.Suspended} - EW:{DsState.State.EmitterWorking} - Perc:{DsState.State.ShieldPercent} - Wake:{DsState.State.Waking} - ShieldId [{Shield.EntityId}]");
                }
                if (DsState.State.Online)
                {
                    if (ComingOnline) ComingOnlineSetup();

                    if (_isServer)
                    {
                        var createHeTiming = _count == 6 && (_lCount == 1 || _lCount == 6);
                        if (GridIsMobile && createHeTiming) CreateHalfExtents();
                        SyncThreadedEnts();
                        WebEntities();
                        var mpActive = Session.MpActive;
                        if (mpActive && _count == 29)
                        {
                            var newPercentColor = UtilsStatic.GetShieldColorFromFloat(DsState.State.ShieldPercent);
                            if (newPercentColor != _oldPercentColor)
                            {
                                if (Session.Enforced.Debug >= 2) Log.Line($"StateUpdate: Percent Threshold Reached");
                                ShieldChangeState();
                                _oldPercentColor = newPercentColor;
                            }
                            else if (_lCount == 7 && _eCount == 7)
                            {
                                if (Session.Enforced.Debug >= 2) Log.Line($"StateUpdate: Timer Reached");
                                ShieldChangeState();
                            }
                        }
                    }
                    else WebEntitiesClient();

                    if (!_isDedicated && _tick60) HudCheck();
                }
                if (Session.Enforced.Debug >= 1) Dsutil1.StopWatchReport($"PerfCon: Online: {DsState.State.Online} - Tick: {_tick} loop: {_lCount}-{_count}", 4);
            }
            catch (Exception ex) {Log.Line($"Exception in UpdateBeforeSimulation: {ex}"); }
        }

        private void UpdateFields()
        {
            _tick = Session.Instance.Tick;
            _tick60 = _tick % 60 == 0;
            _tick600 = _tick % 600 == 0;
            MyGrid = Shield.CubeGrid as MyCubeGrid;
            if (MyGrid != null) IsStatic = MyGrid.IsStatic;
        }

        private void ShieldOff()
        {
            _power = 0.001f;
            Sink.Update();
            WasOnline = false;
            ShieldEnt.Render.Visible = false;
            ShieldEnt.PositionComp.SetPosition(Vector3D.Zero);
            if (_isServer && !DsState.State.Lowered && !DsState.State.Sleeping)
            {
                DsState.State.ShieldPercent = 0f;
                DsState.State.Buffer = 0f;
            }

            if (_isServer)
            {
                if (Session.Enforced.Debug >= 1) Log.Line($"StateUpdate: ShieldOff - ShieldId [{Shield.EntityId}]");
                ShieldChangeState();
            }
            else
            {
                UpdateSubGrids();
                Shield.RefreshCustomInfo();
            }
        }

        private void ComingOnlineSetup()
        {
            if (!_isDedicated) ShellVisibility();
            ShieldEnt.Render.Visible = true;
            ComingOnline = false;
            WasOnline = true;
            WarmedUp = true;
            if (_isServer)
            {
                SyncThreadedEnts(true);
                _offlineCnt = -1;
                if (Session.Enforced.Debug >= 1) Log.Line($"StateUpdate: ComingOnlineSetup - ShieldId [{Shield.EntityId}]");
                ShieldChangeState();

            }
            else
            {
                UpdateSubGrids();
                Shield.RefreshCustomInfo();
            }
        }

        private void Timing(bool cleanUp)
        {
            if (_count++ == 59)
            {
                _count = 0;
                _lCount++;
                if (_lCount == 10)
                {
                    _lCount = 0;
                    _eCount++;
                    if (_eCount == 10) _eCount = 0;
                }
            }

            if (_count == 33)
            {
                if (SettingsUpdated)
                {
                    SettingsUpdated = false;
                    DsSet.SaveSettings();
                    ResetShape(false, false);
                    if (Session.Enforced.Debug >= 1) Log.Line($"SettingsUpdated: server:{Session.IsServer} - ShieldId [{Shield.EntityId}]");
                }
            }
            else if (_count == 34)
            {
                if (ClientUiUpdate && !_isServer)
                {
                    ClientUiUpdate = false;
                    DsSet.NetworkUpdate();
                }
            }

            if (_isServer && (_shapeEvent || FitChanged)) CheckExtents(true);

            if (_isServer) HeatManager();

            if (_count == 29)
            {
                Shield.RefreshCustomInfo();
                if (!_isDedicated)
                {
                    if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
                    {
                        Shield.ShowInToolbarConfig = false;
                        Shield.ShowInToolbarConfig = true;
                    }
                }
                _runningDamage = DpsAvg.Add((int) _damageReadOut);
                _damageReadOut = 0;
            }

            if (cleanUp)
            {
                if (_staleGrids.Count != 0) CleanUp(0);
                if (_lCount == 9 && _count == 58) CleanUp(1);
                if (_effectsCleanup && (_count == 1 || _count == 21 || _count == 41)) CleanUp(2);

                if ((_lCount * 60 + _count + 1) % 150 == 0)
                {
                    CleanUp(3);
                    CleanUp(4);
                }
            }
        }

        private void BlockMonitor()
        {
            if (_blockChanged)
            {
                _blockEvent = true;
                _shapeEvent = true;
                _losCheckTick = _tick + 1800;
                if (_blockAdded) _shapeTick = _tick + 300;
                else _shapeTick = _tick + 1800;
            }
            if (_functionalChanged) _functionalEvent = true;

            _functionalAdded = false;
            _functionalRemoved = false;
            _functionalChanged = false;

            _blockChanged = false;
            _blockRemoved = false;
            _blockAdded = false;
        }

        private void BlockChanged(bool backGround)
        {
            if (_blockEvent)
            {
                var notReady = !FuncTask.IsComplete || DsState.State.Sleeping || DsState.State.Suspended;
                if (notReady) return;
                if (Session.Enforced.Debug >= 1) Log.Line($"BlockChanged: functional:{_functionalEvent} - funcComplete:{FuncTask.IsComplete} - Sleeping:{DsState.State.Sleeping} - Suspend:{DsState.State.Suspended} - ShieldId [{Shield.EntityId}]");
                if (_functionalEvent) FunctionalChanged(backGround);
                _blockEvent = false;
                _funcTick = _tick + 60;
            }
        }

        private void FunctionalChanged(bool backGround)
        {
            if (backGround) FuncTask = MyAPIGateway.Parallel.StartBackground(BackGroundChecks);
            else BackGroundChecks();
            _functionalEvent = false;
        }

        private void CheckExtents(bool backGround)
        {
            FitChanged = false;
            _shapeEvent = false;
            if (!_isServer) return;
            if (GridIsMobile)
            {
                CreateHalfExtents();
                if (backGround) MyAPIGateway.Parallel.StartBackground(GetShapeAdjust);
                else GetShapeAdjust();
            }
        }

        private void BackGroundChecks()
        {
            var gridDistNeedUpdate = _updateGridDistributor || MyGridDistributor?.SourcesEnabled == MyMultipleEnabledEnum.NoObjects;
            _updateGridDistributor = false;
             
            lock (SubLock)
            {
                _powerSources.Clear();
                _functionalBlocks.Clear();
                _batteryBlocks.Clear();

                foreach (var grid in ShieldComp.GetLinkedGrids)
                {
                    var mechanical = ShieldComp.GetSubGrids.Contains(grid);
                    if (grid == null) continue;
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (mechanical && gridDistNeedUpdate)
                        {
                            var controller = block as MyShipController;
                            if (controller != null)
                            {
                                var distributor = controller.GridResourceDistributor;
                                if (distributor.SourcesEnabled != MyMultipleEnabledEnum.NoObjects)
                                {
                                    if (Session.Enforced.Debug >= 1) Log.Line($"Found MyGridDistributor: ShieldId [{Shield.EntityId}]");
                                    MyGridDistributor = controller.GridResourceDistributor;
                                    gridDistNeedUpdate = false;
                                }
                            }
                        }
                        var battery = block as IMyBatteryBlock;
                        if (battery != null && block.IsFunctional && mechanical) _batteryBlocks.Add(battery);
                        if (block.IsFunctional && mechanical) _functionalBlocks.Add(block);

                        var source = block.Components.Get<MyResourceSourceComponent>();

                        if (source == null) continue;
                        foreach (var type in source.ResourceTypes)
                        {
                            if (type != MyResourceDistributorComponent.ElectricityId) continue;
                            _powerSources.Add(source);
                            break;
                        }
                    }
                }
            }
        }
        #endregion

        #region Block Power Logic
        private bool PowerOnline()
        {
            if (!UpdateGridPower()) return false;
            CalculatePowerCharge();
            _power = _shieldConsumptionRate + _shieldMaintaintPower;
            if (!WarmedUp) return true;
            if (_isServer && _hadPowerBefore && _shieldConsumptionRate.Equals(0f) && DsState.State.Buffer.Equals(0.01f) && _genericDownLoop == -1)
            {
                _power = 0.0001f;
                _genericDownLoop = 0;
                return false;
            }
            if (_power < 0.0001f) _power = 0.001f;

            if (_power < _shieldCurrentPower || _count == 28 && !_power.Equals(_shieldCurrentPower)) Sink.Update();
            if (Absorb > 0)
            {
                _damageReadOut += Absorb;
                _effectsCleanup = true;
                DsState.State.Buffer -= Absorb / Session.Enforced.Efficiency;
            }
            else if (Absorb < 0) DsState.State.Buffer += Absorb / Session.Enforced.Efficiency;

            if (_isServer && DsState.State.Buffer < 0)
            {
                DsState.State.Buffer = 0;
                if (!_empOverLoad) _overLoadLoop = 0;
                else _empOverLoadLoop = 0;
            }
            Absorb = 0f;
            return true;
        }

        private bool UpdateGridPower()
        {
            var tempGridMaxPower = _gridMaxPower;
            var dirtyDistributor = FuncTask.IsComplete && MyGridDistributor != null && !_functionalEvent;
            _gridMaxPower = 0;
            _gridCurrentPower = 0;
            _gridAvailablePower = 0;
            _batteryMaxPower = 0;
            _batteryCurrentPower = 0;
            lock (SubLock)
            {
                if (dirtyDistributor)
                {
                    _gridMaxPower += MyGridDistributor.MaxAvailableResourceByType(GId);
                    if (_gridMaxPower <= 0)
                    {
                        var distOnState = MyGridDistributor.SourcesEnabled;
                        var noObjects = distOnState == MyMultipleEnabledEnum.NoObjects;

                        if (noObjects)
                        {
                            if (Session.Enforced.Debug >= 1) Log.Line($"NoObjects: {MyGrid?.DebugName} - Max:{MyGridDistributor?.MaxAvailableResourceByType(GId)} - Status:{MyGridDistributor?.SourcesEnabled} - Sources:{_powerSources.Count}");
                            FallBackPowerCalc();
                            FunctionalChanged(true);
                        }
                    }
                    else
                    {
                        _gridCurrentPower += MyGridDistributor.TotalRequiredInputByType(GId);
                        if (!DsSet.Settings.UseBatteries)
                        {
                            for (int i = 0; i < _batteryBlocks.Count; i++)
                            {
                                var battery = _batteryBlocks[i];
                                if (!battery.IsWorking) continue;
                                var maxOutput = battery.MaxOutput;
                                if (maxOutput <= 0) continue;
                                var currentOutput = battery.CurrentOutput;

                                _gridMaxPower -= maxOutput;
                                _gridCurrentPower -= currentOutput;
                                _batteryMaxPower += maxOutput;
                                _batteryCurrentPower += currentOutput;
                            }
                        }
                    }
                }
                else FallBackPowerCalc();
            }
            _gridAvailablePower = _gridMaxPower - _gridCurrentPower;
            if (!_gridMaxPower.Equals(tempGridMaxPower) || _roundedGridMax <= 0) _roundedGridMax = Math.Round(_gridMaxPower, 1);
            _shieldCurrentPower = Sink.CurrentInputByType(GId);
            return _gridMaxPower > 0;
        }

        private void FallBackPowerCalc()
        {
            var rId = GId;
            for (int i = 0; i < _powerSources.Count; i++)
            {
                var source = _powerSources[i];
                if (!source.Enabled || !source.ProductionEnabledByType(rId) || source.Entity is IMyReactor && !source.HasCapacityRemainingByType(rId)) continue;
                if (source.Entity is IMyBatteryBlock)
                {
                    _batteryMaxPower += source.MaxOutputByType(rId);
                    _batteryCurrentPower += source.CurrentOutputByType(rId);
                }
                else
                {
                    _gridMaxPower += source.MaxOutputByType(rId);
                    _gridCurrentPower += source.CurrentOutputByType(rId);
                }
            }

            if (DsSet.Settings.UseBatteries)
            {
                _gridMaxPower += _batteryMaxPower;
                _gridCurrentPower += _batteryCurrentPower;
            }
        }

        private void CalculatePowerCharge()
        {
            var nerf = Session.Enforced.Nerf > 0 && Session.Enforced.Nerf < 1;
            var rawNerf = nerf ? Session.Enforced.Nerf : 1f;
            var capScaler = Session.Enforced.CapScaler;
            var nerfer = rawNerf / _shieldRatio;
            const float ratio = 1.25f;
            var percent = DsSet.Settings.Rate * ratio;
            var shieldMaintainPercent = Session.Enforced.MaintenanceCost / percent;
            var sizeScaler = _shieldVol / (_ellipsoidSurfaceArea * 2.40063050674088);
            var gridIntegrity = DsState.State.GridIntegrity * 0.01f;
            var hpScaler = 1f;
            _sizeScaler = sizeScaler >= 1d ? sizeScaler : 1d;

            float bufferScaler;
            if (ShieldMode == ShieldType.Station && DsState.State.Enhancer) bufferScaler = 100 / percent * Session.Enforced.BaseScaler * nerfer;
            else bufferScaler = 100 / percent * Session.Enforced.BaseScaler / (float)_sizeScaler * nerfer;

            var hpBase = _gridMaxPower * bufferScaler;
            if (capScaler > 0 && hpBase > gridIntegrity) hpScaler = gridIntegrity * capScaler / hpBase;

            shieldMaintainPercent = shieldMaintainPercent * DsState.State.EnhancerPowerMulti * (DsState.State.ShieldPercent * 0.01f);
            if (DsState.State.Lowered) shieldMaintainPercent = shieldMaintainPercent * 0.25f;
            _shieldMaintaintPower = _gridMaxPower * hpScaler * shieldMaintainPercent;

            ShieldMaxBuffer = hpBase * hpScaler;

            //if (_tick600) Log.Line($"gridName:{MyGrid.DebugName} - {hpBase} > {gridIntegrity} ({hpBase > gridIntegrity}) - hpScaler:{hpScaler}");

            var powerForShield = PowerNeeded(percent, ratio, nerfer);

            if (!WarmedUp) return;

            if (DsState.State.Buffer > ShieldMaxBuffer) DsState.State.Buffer = ShieldMaxBuffer;

            if (PowerLoss(powerForShield)) return;

            ChargeBuffer();
            if (DsState.State.Buffer < ShieldMaxBuffer) DsState.State.ShieldPercent = DsState.State.Buffer / ShieldMaxBuffer * 100;
            else if (DsState.State.Buffer < ShieldMaxBuffer * 0.1) DsState.State.ShieldPercent = 0f;
            else DsState.State.ShieldPercent = 100f;
        }

        private float PowerNeeded(float percent, float ratio, float nerfer)
        {
            var powerForShield = 0f;
            var fPercent = percent / ratio * 0.01f;

            var cleanPower = _gridAvailablePower + _shieldCurrentPower;
            _otherPower = _gridMaxPower - cleanPower;
            powerForShield = (cleanPower * fPercent) - _shieldMaintaintPower;
            var rawMaxChargeRate = powerForShield > 0 ? powerForShield : 0f;
            _shieldMaxChargeRate = rawMaxChargeRate;
            var chargeSize = _shieldMaxChargeRate * Session.Enforced.HpsEfficiency / _sizeScaler * nerfer;
            if (DsState.State.Buffer + chargeSize < ShieldMaxBuffer)
            {
                _shieldChargeRate = (float) chargeSize;
                _shieldConsumptionRate = _shieldMaxChargeRate;
            }
            else
            {
                var remaining = ShieldMaxBuffer - DsState.State.Buffer;
                var remainingScaled = remaining / chargeSize;
                _shieldConsumptionRate = (float) (remainingScaled * _shieldMaxChargeRate);
                _shieldChargeRate = (float) (chargeSize * remainingScaled);
            }

            _powerNeeded = _shieldMaintaintPower + _shieldConsumptionRate + _otherPower;
            return powerForShield;
        }

        private bool PowerLoss(float powerForShield)
        {
            if (_powerNeeded > _roundedGridMax || powerForShield <= 0)
            {
                if (_isServer && !DsState.State.Online)
                {
                    DsState.State.Buffer = 0.01f;
                    _shieldChargeRate = 0f;
                    _shieldConsumptionRate = 0f;
                    return true;
                }
                _powerLossLoop++;
                if (_isServer && !DsState.State.NoPower)
                {
                    DsState.State.NoPower = true;
                    DsState.State.Message = true;
                    if (Session.Enforced.Debug >= 1) Log.Line($"StateUpdate: NoPower - forShield:{powerForShield} - rounded:{_roundedGridMax} - max:{_gridMaxPower} - avail{_gridAvailablePower} - sCurr:{_shieldCurrentPower} - count:{_powerSources.Count} - DistEna:{MyGridDistributor.SourcesEnabled} - State:{MyGridDistributor?.ResourceState} - ShieldId [{Shield.EntityId}]");
                    ShieldChangeState();
                }

                var shieldLoss = ShieldMaxBuffer * 0.0016667f;
                DsState.State.Buffer = DsState.State.Buffer - shieldLoss;
                if (DsState.State.Buffer< 0.01f) DsState.State.Buffer = 0.01f;

                if (DsState.State.Buffer < ShieldMaxBuffer) DsState.State.ShieldPercent = DsState.State.Buffer / ShieldMaxBuffer * 100;
                else if (DsState.State.Buffer < ShieldMaxBuffer * 0.1) DsState.State.ShieldPercent = 0f;
                else DsState.State.ShieldPercent = 100f;

                _shieldChargeRate = 0f;
                _shieldConsumptionRate = 0f;
                return true;
            }

            _powerLossLoop = 0;

            if (_isServer && DsState.State.NoPower)
            {
                _powerNoticeLoop++;
                if (_powerNoticeLoop >= PowerNoticeCount)
                {
                    DsState.State.NoPower = false;
                    _powerNoticeLoop = 0;
                    if (Session.Enforced.Debug >= 1) Log.Line($"StateUpdate: PowerRestored - ShieldId [{Shield.EntityId}]");
                    ShieldChangeState();
                }
            }
            return false;
        }

        private void ChargeBuffer()
        {
            var heat = DsState.State.Heat * 0.1;
            if (heat > 10) heat = 10;

            if (heat >= 10) _shieldChargeRate = 0;
            else
            {
                var expChargeReduction = (float)Math.Pow(2, heat);
                _shieldChargeRate = _shieldChargeRate / expChargeReduction;
            }
            if (_count == 29 && DsState.State.Buffer < ShieldMaxBuffer) DsState.State.Buffer += _shieldChargeRate;
            else if (DsState.State.Buffer.Equals(ShieldMaxBuffer))
            {
                _shieldChargeRate = 0f;
                _shieldConsumptionRate = 0f;
            }
        }

        private void HeatManager()
        {
            var hp = ShieldMaxBuffer * Session.Enforced.Efficiency;
            var oldHeat = DsState.State.Heat;
            if (_damageReadOut > 0 && _heatCycle == -1)
            {
                if (_count == 29) _accumulatedHeat += _damageReadOut;
                _heatCycle = 0;
            }
            else if (_heatCycle > -1)
            {
                if (_count == 29) _accumulatedHeat += _damageReadOut;
                _heatCycle++;
            }

            var empProt = DsState.State.EmpProtection && ShieldMode != ShieldType.Station;
            if (empProt && _heatCycle == 0)
            {
                _empScaleHp = 0.1f;
                _empScaleTime = 10;
            }
            else if (!empProt && _heatCycle == 0)
            {
                _empScaleHp = 1f;
                _empScaleTime = 1;
            }

            var hpLoss = 0.01 * _empScaleHp;
            var nextThreshold = hp * hpLoss * _currentHeatStep;
            var currentThreshold = hp * hpLoss * (_currentHeatStep - 1);
            var scaledOverHeat = OverHeat / _empScaleTime;
            var lastStep = _currentHeatStep == 10;
            var overloadStep = _heatCycle == scaledOverHeat;
            var scaledHeatingSteps = HeatingStep / _empScaleTime;
            var afterOverload = _heatCycle > scaledOverHeat;
            var nextCycle = _heatCycle == _currentHeatStep * scaledHeatingSteps + scaledOverHeat;
            var overload = _accumulatedHeat > hpLoss;
            var pastThreshold = _accumulatedHeat > nextThreshold;
            var metThreshold = _accumulatedHeat > currentThreshold;
            var underThreshold = !pastThreshold && !metThreshold;
            var venting = lastStep && pastThreshold;
            var leftCritical = lastStep && _tick >= _heatVentingTick;
            var backOneCycles = (_currentHeatStep - 1) * scaledHeatingSteps + scaledOverHeat + 1;
            var backTwoCycles = (_currentHeatStep - 2) * scaledHeatingSteps + scaledOverHeat + 1;

            if (overloadStep)
            {
                if (overload)
                {
                    _currentHeatStep = 1;
                    DsState.State.Heat = _currentHeatStep * 10;
                    if (Session.Enforced.Debug >= 1) Log.Line($"overh - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:xxxx - heat:{_accumulatedHeat} - threshold:{hpLoss} - ShieldId [{Shield.EntityId}]");
                    _accumulatedHeat = 0;
                }
                else
                {
                    DsState.State.Heat = 0;
                    _currentHeatStep = 0;
                    if (Session.Enforced.Debug >= 1) Log.Line($"under - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:[-1] - heat:{_accumulatedHeat} - threshold:{hpLoss} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                    _heatCycle = -1;
                    _accumulatedHeat = 0;
                }
            }
            else if (nextCycle && afterOverload && !lastStep)
            {
                if (_empScaleTime == 10)
                {
                    if (_accumulatedHeat > 0) _fallbackCycle = 1;
                    else _fallbackCycle++;
                }
                 
                if (pastThreshold)
                {
                    _currentHeatStep++;
                    DsState.State.Heat = _currentHeatStep * 10;
                    if (Session.Enforced.Debug >= 1) Log.Line($"incre - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:xxxx - heat:{_accumulatedHeat} - threshold:{currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                    _accumulatedHeat = 0;
                }
                else if (metThreshold)
                {
                    DsState.State.Heat = _currentHeatStep * 10;
                    if (Session.Enforced.Debug >= 1) Log.Line($"uncha - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:{backOneCycles} - heat:{_accumulatedHeat} - threshold:{currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                    _heatCycle = backOneCycles;
                    _accumulatedHeat = 0;
                }
                else
                {
                    _heatCycle = backOneCycles;
                    _accumulatedHeat = 0;
                }

                if (empProt && _fallbackCycle == FallBackStep || !empProt && underThreshold)
                {
                    if (_currentHeatStep > 0) _currentHeatStep--;
                    if (_currentHeatStep == 0)
                    {
                        DsState.State.Heat = 0;
                        _currentHeatStep = 0;
                        if (Session.Enforced.Debug >= 1) Log.Line($"nohea - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:[-1] - heat:{_accumulatedHeat} - threshold:{currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                        _heatCycle = -1;
                        _accumulatedHeat = 0;
                        _fallbackCycle = 0;
                    }
                    else
                    {
                        DsState.State.Heat = _currentHeatStep * 10;
                        if (Session.Enforced.Debug >= 1) Log.Line($"decto - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:{backTwoCycles} - heat:{_accumulatedHeat} - threshold:{currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                        _heatCycle = backTwoCycles;
                        _accumulatedHeat = 0;
                        _fallbackCycle = 0;
                    }
                }
            }
            else if (venting)
            {
                if (Session.Enforced.Debug >= 1) Log.Line($"mainc - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:xxxx - heat:{_accumulatedHeat} - threshold: {currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                _heatVentingTick = _tick + CoolingStep;
                _accumulatedHeat = 0;
            }
            else if (leftCritical)
            {
                if (_currentHeatStep >= 10) _currentHeatStep--;
                if (Session.Enforced.Debug >= 1) Log.Line($"leftc - stage:{_currentHeatStep} - cycle:{_heatCycle} - resetCycle:{backTwoCycles} - heat:{_accumulatedHeat} - threshold: {currentThreshold} - nThreshold:{nextThreshold} - ShieldId [{Shield.EntityId}]");
                DsState.State.Heat = _currentHeatStep * 10;
                _heatCycle = backTwoCycles;
                _heatVentingTick = uint.MaxValue;
                _accumulatedHeat = 0;
            }

            if (_heatCycle > HeatingStep * 10 + OverHeat && _tick >= _heatVentingTick)
            {
                if (Session.Enforced.Debug >= 1) Log.Line($"HeatCycle over limit, resetting: heatCycle:{_heatCycle} - fallCycle:{_fallbackCycle}");
                _heatCycle = -1;
                _fallbackCycle = 0;
            }

            if (!oldHeat.Equals(DsState.State.Heat))
            {
                if (Session.Enforced.Debug >= 2) Log.Line($"StateUpdate: HeatChange - ShieldId [{Shield.EntityId}]");
                ShieldChangeState();
            }
        }
        #endregion

        #region Checks / Terminal
        private string GetShieldStatus()
        {
            if (!DsState.State.Online && (!Shield.IsWorking || !Shield.IsFunctional)) return "[Controller Failure]";
            if (!DsState.State.Online && DsState.State.NoPower) return "[Insufficient Power]";
            if (!DsState.State.Online && DsState.State.Overload) return "[Overloaded]";
            if (!DsState.State.ControllerGridAccess) return "[Invalid Owner]";
            if (DsState.State.Waking) return "[Coming Online]";
            if (DsState.State.Suspended || DsState.State.Mode == 4) return "[Controller Standby]";
            if (DsState.State.Lowered) return "[Shield Down]";
            if (!DsState.State.EmitterWorking) return "[Emitter Failure]";
            if (DsState.State.Sleeping) return "[Suspended]";
            if (!DsState.State.Online) return "[Shield Offline]";
            return "[Shield Up]";
        }

        private void AppendingCustomInfo(IMyTerminalBlock block, StringBuilder stringBuilder)
        {
            try
            {
                var secToFull = 0;
                var shieldPercent = !DsState.State.Online ? 0f : 100f;

                if (DsState.State.Buffer < ShieldMaxBuffer) shieldPercent = DsState.State.Buffer / ShieldMaxBuffer * 100;
                if (_shieldChargeRate > 0)
                {
                    var toMax = ShieldMaxBuffer - DsState.State.Buffer;
                    var secs = toMax / _shieldChargeRate;
                    if (secs.Equals(1)) secToFull = 0;
                    else secToFull = (int)secs;
                }

                var shieldPowerNeeds = _powerNeeded;
                var powerUsage = shieldPowerNeeds;
                var otherPower = _otherPower;
                var gridMaxPower = _gridMaxPower;
                if (!DsSet.Settings.UseBatteries)
                {
                    powerUsage = powerUsage + _batteryCurrentPower;
                    otherPower = _otherPower + _batteryCurrentPower;
                    gridMaxPower = gridMaxPower + _batteryMaxPower;
                }
                var status = GetShieldStatus();
                if (status == "[Shield Up]" || status == "[Shield Down]" || status == "[Shield Offline]")
                {
                    stringBuilder.Append(status + " MaxHP: " + (ShieldMaxBuffer * Session.Enforced.Efficiency).ToString("N0") +
                                         "\n" +
                                         "\n[Shield HP__]: " + (DsState.State.Buffer * Session.Enforced.Efficiency).ToString("N0") + " (" + shieldPercent.ToString("0") + "%)" +
                                         "\n[HP Per Sec_]: " + (_shieldChargeRate * Session.Enforced.Efficiency).ToString("N0") +
                                         "\n[Damage In__]: " + _damageReadOut.ToString("N0") +
                                         "\n[Charge Rate]: " + _shieldChargeRate.ToString("0.0") + " Mw" +
                                         "\n[Full Charge_]: " + secToFull.ToString("N0") + "s" +
                                         "\n[Over Heated]: " + DsState.State.Heat.ToString("0") + "%" +
                                         "\n[Maintenance]: " + _shieldMaintaintPower.ToString("0.0") + " Mw" +
                                         "\n[Power Usage]: " + powerUsage.ToString("0.0") + " (" + gridMaxPower.ToString("0.0") + ")Mw" +
                                         "\n[Shield Power]: " + Sink.CurrentInputByType(GId).ToString("0.0") + " Mw");
                }
                else
                {
                    stringBuilder.Append("Shield Status " + status +
                                         "\n" +
                                         "\n[Maintenance]: " + _shieldMaintaintPower.ToString("0.0") + " Mw" +
                                         "\n[Other Power]: " + otherPower.ToString("0.0") + " Mw" +
                                         "\n[HP Stored]: " + (DsState.State.Buffer * Session.Enforced.Efficiency).ToString("N0") + " (" + shieldPercent.ToString("0") + "%)" +
                                         "\n[Needed Power]: " + shieldPowerNeeds.ToString("0.0") + " (" + gridMaxPower.ToString("0.0") + ") Mw" +
                                         "\n[Emitter Detected]: " + DsState.State.EmitterWorking +
                                         "\n" +
                                         "\n[Grid Owns Controller]: " + DsState.State.IsOwner +
                                         "\n[In Grid's Faction]: " + DsState.State.InFaction);
                }
            }
            catch (Exception ex) { Log.Line($"Exception in Controller AppendingCustomInfo: {ex}"); }
        }

        private void HierarchyUpdate()
        {
            var checkGroups = Shield.IsWorking && Shield.IsFunctional && (DsState.State.Online || DsState.State.NoPower || DsState.State.Sleeping || DsState.State.Waking);
            if (Session.Enforced.Debug >= 2) Log.Line($"SubCheckGroups: check:{checkGroups} - SW:{Shield.IsWorking} - SF:{Shield.IsFunctional} - Online:{DsState.State.Online} - Power:{!DsState.State.NoPower} - Sleep:{DsState.State.Sleeping} - Wake:{DsState.State.Waking} - ShieldId [{Shield.EntityId}]");
            if (checkGroups)
            {
                _subTick = _tick + 10;
                UpdateSubGrids();
                if (Session.Enforced.Debug >= 2) Log.Line($"HierarchyWasDelayed: this:{_tick} - delayedTick: {_subTick} - ShieldId [{Shield.EntityId}]");
            }
        }

        private void UpdateSubGrids()
        {
            var gotGroups = MyAPIGateway.GridGroups.GetGroup(MyGrid, GridLinkTypeEnum.Physical);
            if (gotGroups.Count == ShieldComp.GetLinkedGrids.Count) return;
            if (Session.Enforced.Debug >= 1) Log.Line($"SubGroupCnt: subCountChanged:{ShieldComp.GetLinkedGrids.Count != gotGroups.Count} - old:{ShieldComp.GetLinkedGrids.Count} - new:{gotGroups.Count} - ShieldId [{Shield.EntityId}]");

            lock (SubLock)
            {
                ShieldComp.GetSubGrids.Clear();
                ShieldComp.GetLinkedGrids.Clear();
                for (int i = 0; i < gotGroups.Count; i++)
                {
                    var sub = gotGroups[i];
                    if (sub == null) continue;
                    if (MyAPIGateway.GridGroups.HasConnection(MyGrid, sub, GridLinkTypeEnum.Mechanical)) ShieldComp.GetSubGrids.Add(sub as MyCubeGrid);
                    ShieldComp.GetLinkedGrids.Add(sub as MyCubeGrid);
                }
            }

            _blockChanged = true;
            _functionalChanged = true;
            _updateGridDistributor = true;
            _subUpdate = false;
        }

        private void ShieldDoDamage(float damage, long entityId, float shieldFractionLoss = 0f)
        {
            EmpSize = shieldFractionLoss;
            ImpactSize = damage;

            if (shieldFractionLoss > 0)
            {
                damage = shieldFractionLoss;
            }

            Shield.SlimBlock.DoDamage(damage, MPdamage, true, null, entityId);
        }
        #endregion

        #region Shield Support Blocks
        public void GetModulationInfo()
        {
            var update = false;
            if (ShieldComp.Modulator != null && ShieldComp.Modulator.ModState.State.Online)
            {
                var modEnergyRatio = ShieldComp.Modulator.ModState.State.ModulateEnergy * 0.01f;
                var modKineticRatio = ShieldComp.Modulator.ModState.State.ModulateKinetic * 0.01f;
                if (!DsState.State.ModulateEnergy.Equals(modEnergyRatio) || !DsState.State.ModulateKinetic.Equals(modKineticRatio) || !DsState.State.EmpProtection.Equals(ShieldComp.Modulator.ModSet.Settings.EmpEnabled)) update = true;
                DsState.State.ModulateEnergy = modEnergyRatio;
                DsState.State.ModulateKinetic = modKineticRatio;
                DsState.State.EmpProtection = ShieldComp.Modulator.ModSet.Settings.EmpEnabled;
                if (update) ShieldChangeState();
            }
            else
            {
                if (!DsState.State.ModulateEnergy.Equals(1f) || !DsState.State.ModulateKinetic.Equals(1f) || DsState.State.EmpProtection) update = true;
                DsState.State.ModulateEnergy = 1f;
                DsState.State.ModulateKinetic = 1f;
                DsState.State.EmpProtection = false;
                if (update) ShieldChangeState();

            }
        }

        public void GetEnhancernInfo()
        {
            var update = false;
            if (ShieldComp.Enhancer != null && ShieldComp.Enhancer.EnhState.State.Online)
            {
                if (!DsState.State.EnhancerPowerMulti.Equals(2) || !DsState.State.EnhancerProtMulti.Equals(1000) || !DsState.State.Enhancer) update = true;
                DsState.State.EnhancerPowerMulti = 2;
                DsState.State.EnhancerProtMulti = 1000;
                DsState.State.Enhancer = true;
                if (update) ShieldChangeState();
            }
            else
            {
                if (!DsState.State.EnhancerPowerMulti.Equals(1) || !DsState.State.EnhancerProtMulti.Equals(1) || DsState.State.Enhancer) update = true;
                DsState.State.EnhancerPowerMulti = 1;
                DsState.State.EnhancerProtMulti = 1;
                DsState.State.Enhancer = false;
                if (update) ShieldChangeState();
            }
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
                        MyCubeGrid grid;
                        while (_staleGrids.TryDequeue(out grid))
                        {
                            EntIntersectInfo gridRemoved;
                            WebEnts.TryRemove(grid, out gridRemoved);
                        }
                        break;
                    case 1:
                        EnemyShields.Clear();
                        _webEntsTmp.AddRange(WebEnts.Where(info => _tick - info.Value.FirstTick > 599 && _tick - info.Value.LastTick > 1));
                        foreach (var webent in _webEntsTmp)
                        {
                            EntIntersectInfo gridRemoved;
                            WebEnts.TryRemove(webent.Key, out gridRemoved);
                        }
                        break;
                    case 2:
                        if (DsState.State.Online && !DsState.State.Lowered)
                        {
                            lock (SubLock)
                            {
                                foreach (var funcBlock in _functionalBlocks)
                                {
                                    if (funcBlock == null) continue;
                                    if (funcBlock.IsFunctional) funcBlock.SetDamageEffect(false);
                                }
                            }
                        }
                        _effectsCleanup = false;

                        break;
                    case 3:
                        {
                            FriendlyCache.Clear();
                            PartlyProtectedCache.Clear();
                            AuthenticatedCache.Clear();
                            foreach (var sub in ShieldComp.GetSubGrids)
                            {
                                if (sub == null) continue;

                                if (!GridIsMobile && ShieldEnt.PositionComp.WorldVolume.Intersects(sub.PositionComp.WorldVolume))
                                {
                                    var cornersInShield = CustomCollision.NotAllCornersInShield(sub, DetectMatrixOutsideInv);
                                    if (cornersInShield != 8) PartlyProtectedCache.Add(sub);
                                    else if (cornersInShield == 8) FriendlyCache.Add(sub);
                                    continue;
                                }
                                FriendlyCache.Add(sub);
                            }
                            FriendlyCache.Add(ShieldEnt);
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
        #endregion
    }
}