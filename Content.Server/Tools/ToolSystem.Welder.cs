﻿using System.Linq;
using Content.Server.Chemistry.Components;
using Content.Server.Chemistry.Components.SolutionManager;
using Content.Server.Chemistry.EntitySystems;
using Content.Server.Tools.Components;
using Content.Server.Weapon.Melee;
using Content.Shared.Audio;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Popups;
using Content.Shared.Temperature;
using Content.Shared.Tools.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Player;

namespace Content.Server.Tools
{
    public sealed partial class ToolSystem
    {
        private readonly HashSet<EntityUid> _activeWelders = new();

        private const float WelderUpdateTimer = 1f;
        private float _welderTimer = 0f;

        public void InitializeWelders()
        {
            SubscribeLocalEvent<WelderComponent, ComponentStartup>(OnWelderStartup);
            SubscribeLocalEvent<WelderComponent, IsHotEvent>(OnWelderIsHotEvent);
            SubscribeLocalEvent<WelderComponent, ExaminedEvent>(OnWelderExamine);
            SubscribeLocalEvent<WelderComponent, SolutionChangedEvent>(OnWelderSolutionChange);
            SubscribeLocalEvent<WelderComponent, ActivateInWorldEvent>(OnWelderActivate);
            SubscribeLocalEvent<WelderComponent, AfterInteractEvent>(OnWelderAfterInteract);
            SubscribeLocalEvent<WelderComponent, ToolUseAttemptEvent>(OnWelderToolUseAttempt);
            SubscribeLocalEvent<WelderComponent, ToolUseFinishAttemptEvent>(OnWelderToolUseFinishAttempt);
            SubscribeLocalEvent<WelderComponent, ComponentShutdown>(OnWelderShutdown);
            SubscribeLocalEvent<WelderComponent, ComponentGetState>(OnWelderGetState);
            SubscribeLocalEvent<WelderComponent, MeleeHitEvent>(OnMeleeHit);
        }

        private void OnMeleeHit(EntityUid uid, WelderComponent component, MeleeHitEvent args)
        {
            if (!args.Handled && component.Lit)
                args.BonusDamage += component.LitMeleeDamageBonus;
        }

        public (FixedPoint2 fuel, FixedPoint2 capacity) GetWelderFuelAndCapacity(EntityUid uid, WelderComponent? welder = null, SolutionContainerManagerComponent? solutionContainer = null)
        {
            if (!Resolve(uid, ref welder, ref solutionContainer)
                || !_solutionContainerSystem.TryGetSolution(uid, welder.FuelSolution, out var fuelSolution, solutionContainer))
                return (FixedPoint2.Zero, FixedPoint2.Zero);

            return (_solutionContainerSystem.GetReagentQuantity(uid, welder.FuelReagent), fuelSolution.MaxVolume);
        }

        public bool TryToggleWelder(EntityUid uid, EntityUid? user,
            WelderComponent? welder = null,
            SolutionContainerManagerComponent? solutionContainer = null,
            SharedItemComponent? item = null,
            PointLightComponent? light = null,
            AppearanceComponent? appearance = null)
        {
            // Right now, we only need the welder.
            // So let's not unnecessarily resolve components
            if (!Resolve(uid, ref welder))
                return false;

            return !welder.Lit
                ? TryTurnWelderOn(uid, user, welder, solutionContainer, item, light, appearance)
                : TryTurnWelderOff(uid, user, welder, item, light, appearance);
        }

        public bool TryTurnWelderOn(EntityUid uid, EntityUid? user,
            WelderComponent? welder = null,
            SolutionContainerManagerComponent? solutionContainer = null,
            SharedItemComponent? item = null,
            PointLightComponent? light = null,
            AppearanceComponent? appearance = null)
        {
            if (!Resolve(uid, ref welder, ref solutionContainer))
                return false;

            // Optional components.
            Resolve(uid, ref item, ref light, ref appearance, false);

            if (!_solutionContainerSystem.TryGetSolution(uid, welder.FuelSolution, out var solution, solutionContainer))
                return false;

            var fuel = solution.GetReagentQuantity(welder.FuelReagent);

            // Not enough fuel to lit welder.
            if (fuel == FixedPoint2.Zero || fuel < welder.FuelLitCost)
            {
                if(user != null)
                    _popupSystem.PopupEntity(Loc.GetString("welder-component-no-fuel-message"), uid, Filter.Entities(user.Value));
                return false;
            }

            solution.RemoveReagent(welder.FuelReagent, welder.FuelLitCost);

            welder.Lit = true;

            if(item != null)
                item.EquippedPrefix = "on";

            appearance?.SetData(WelderVisuals.Lit, true);

            if (light != null)
                light.Enabled = true;

            SoundSystem.Play(welder.WelderOnSounds.GetSound(), Filter.Pvs(uid), uid, AudioHelpers.WithVariation(0.125f).WithVolume(-5f));

            // TODO: Use TransformComponent directly.
            _atmosphereSystem.HotspotExpose(EntityManager.GetComponent<TransformComponent>(welder.Owner).Coordinates, 700, 50, true);

            welder.Dirty();

            _activeWelders.Add(uid);
            return true;
        }

        public bool TryTurnWelderOff(EntityUid uid, EntityUid? user,
            WelderComponent? welder = null,
            SharedItemComponent? item = null,
            PointLightComponent? light = null,
            AppearanceComponent? appearance = null)
        {
            if (!Resolve(uid, ref welder))
                return false;

            // Optional components.
            Resolve(uid, ref item, ref light, ref appearance, false);

            welder.Lit = false;

            // TODO: Make all this use visualizers.
            if (item != null)
                item.EquippedPrefix = "off";

            // Layer 1 is the flame.
            appearance?.SetData(WelderVisuals.Lit, false);

            if (light != null)
                light.Enabled = false;

            SoundSystem.Play(welder.WelderOffSounds.GetSound(), Filter.Pvs(uid), uid, AudioHelpers.WithVariation(0.125f).WithVolume(-5f));

            welder.Dirty();

            _activeWelders.Remove(uid);
            return true;
        }

        private void OnWelderStartup(EntityUid uid, WelderComponent component, ComponentStartup args)
        {
            component.Dirty();
        }

        private void OnWelderIsHotEvent(EntityUid uid, WelderComponent welder, IsHotEvent args)
        {
            args.IsHot = welder.Lit;
        }

        private void OnWelderExamine(EntityUid uid, WelderComponent welder, ExaminedEvent args)
        {
            if (welder.Lit)
            {
                args.PushMarkup(Loc.GetString("welder-component-on-examine-welder-lit-message"));
            }
            else
            {
                args.PushMarkup(Loc.GetString("welder-component-on-examine-welder-not-lit-message"));
            }

            if (args.IsInDetailsRange)
            {
                var (fuel, capacity) = GetWelderFuelAndCapacity(uid, welder);

                args.PushMarkup(Loc.GetString("welder-component-on-examine-detailed-message",
                    ("colorName", fuel < capacity / FixedPoint2.New(4f) ? "darkorange" : "orange"),
                    ("fuelLeft", fuel),
                    ("fuelCapacity", capacity),
                    ("status", string.Empty))); // Lit status is handled above
            }
        }

        private void OnWelderSolutionChange(EntityUid uid, WelderComponent welder, SolutionChangedEvent args)
        {
            welder.Dirty();
        }

        private void OnWelderActivate(EntityUid uid, WelderComponent welder, ActivateInWorldEvent args)
        {
            args.Handled = TryToggleWelder(uid, args.User, welder);
        }

        private void OnWelderAfterInteract(EntityUid uid, WelderComponent welder, AfterInteractEvent args)
        {
            if (args.Handled)
                return;

            if (args.Target is not {Valid: true} target || !args.CanReach)
                return;

            // TODO: Clean up this inherited oldcode.

            if (EntityManager.TryGetComponent(target, out ReagentTankComponent? tank)
                && tank.TankType == ReagentTankType.Fuel
                && _solutionContainerSystem.TryGetDrainableSolution(target, out var targetSolution)
                && _solutionContainerSystem.TryGetSolution(uid, welder.FuelSolution, out var welderSolution))
            {
                var trans = FixedPoint2.Min(welderSolution.AvailableVolume, targetSolution.DrainAvailable);
                if (trans > 0)
                {
                    var drained = _solutionContainerSystem.Drain(target, targetSolution,  trans);
                    _solutionContainerSystem.TryAddSolution(uid, welderSolution, drained);
                    SoundSystem.Play(welder.WelderRefill.GetSound(), Filter.Pvs(uid), uid);
                    target.PopupMessage(args.User, Loc.GetString("welder-component-after-interact-refueled-message"));
                }
                else
                {
                    target.PopupMessage(args.User, Loc.GetString("welder-component-no-fuel-in-tank", ("owner", args.Target)));
                }
            }

            args.Handled = true;
        }

        private void OnWelderToolUseAttempt(EntityUid uid, WelderComponent welder, ToolUseAttemptEvent args)
        {
            if (args.Cancelled)
                return;

            if (!welder.Lit)
            {
                _popupSystem.PopupEntity(Loc.GetString("welder-component-welder-not-lit-message"), uid, Filter.Entities(args.User));
                args.Cancel();
                return;
            }

            var (fuel, _) = GetWelderFuelAndCapacity(uid, welder);

            if (FixedPoint2.New(args.Fuel) > fuel)
            {
                _popupSystem.PopupEntity(Loc.GetString("welder-component-cannot-weld-message"), uid, Filter.Entities(args.User));
                args.Cancel();
                return;
            }
        }

        private void OnWelderToolUseFinishAttempt(EntityUid uid, WelderComponent welder, ToolUseFinishAttemptEvent args)
        {
            if (args.Cancelled)
                return;

            if (!welder.Lit)
            {
                _popupSystem.PopupEntity(Loc.GetString("welder-component-welder-not-lit-message"), uid, Filter.Entities(args.User));
                args.Cancel();
                return;
            }

            var (fuel, _) = GetWelderFuelAndCapacity(uid, welder);

            var neededFuel = FixedPoint2.New(args.Fuel);

            if (neededFuel > fuel)
            {
                args.Cancel();
            }

            if (!_solutionContainerSystem.TryGetSolution(uid, welder.FuelSolution, out var solution))
            {
                args.Cancel();
                return;
            }

            solution.RemoveReagent(welder.FuelReagent, neededFuel);
            welder.Dirty();
        }

        private void OnWelderShutdown(EntityUid uid, WelderComponent welder, ComponentShutdown args)
        {
            _activeWelders.Remove(uid);
        }

        private void OnWelderGetState(EntityUid uid, WelderComponent welder, ref ComponentGetState args)
        {
            var (fuel, capacity) = GetWelderFuelAndCapacity(uid, welder);
            args.State = new WelderComponentState(capacity.Float(), fuel.Float(), welder.Lit);
        }

        private void UpdateWelders(float frameTime)
        {
            _welderTimer += frameTime;

            if (_welderTimer < WelderUpdateTimer)
                return;

            foreach (var tool in _activeWelders.ToArray())
            {
                if (!EntityManager.TryGetComponent(tool, out WelderComponent? welder)
                    || !EntityManager.TryGetComponent(tool, out SolutionContainerManagerComponent? solutionContainer))
                    continue;

                if (!_solutionContainerSystem.TryGetSolution(tool, welder.FuelSolution, out var solution, solutionContainer))
                    continue;

                // TODO: Use TransformComponent directly.
                _atmosphereSystem.HotspotExpose(EntityManager.GetComponent<TransformComponent>(welder.Owner).Coordinates, 700, 50, true);

                solution.RemoveReagent(welder.FuelReagent, welder.FuelConsumption * _welderTimer);

                if (solution.GetReagentQuantity(welder.FuelReagent) <= FixedPoint2.Zero)
                    TryTurnWelderOff(tool, null, welder);

                welder.Dirty();
            }

            _welderTimer -= WelderUpdateTimer;
        }
    }
}
