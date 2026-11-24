using System;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Serialization;
using Game.Simulation;
using Game.UI.InGame;
namespace Overtake
{
    public sealed class Mod : IMod
    {
        // The game build this release was last physically validated against. A
        // different build is reported, not refused: every patch changes this id.
        internal static readonly Guid TestedNavigationModule = new Guid("e15551e9-3dea-4855-8dde-1da3cea7d7ef");
        public void OnLoad(UpdateSystem update)
        {
            var log = LogManager.GetLogger(nameof(Overtake));
            if (!NativeCompatibility.TryVerify(out string problem))
            {
                log.Warn("Passing is disabled: the game's native vehicle contract changed (" + problem + ").");
                return;
            }
            if (typeof(CarNavigationSystem).Module.ModuleVersionId != TestedNavigationModule)
                log.Info("Game build differs from the last validated build; native vehicle contract verified.");
            // Release leases observed after the previous tick's movement, then
            // consume the worker-thread scan that followed that movement.
            update.UpdateBefore<PassingObservationSystem, SlowLeaderDiscoverySystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateBefore<SlowLeaderDiscoverySystem, PassingPoolSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateBefore<PassingPoolSystem, PassingWaveSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateBefore<PassingWaveSystem, PassingTraversalSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateBefore<PassingTraversalSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            update.World.GetOrCreateSystemManaged<PassingTraversalSystem>().ConfigureNavigationOrder(update);
            update.UpdateAfter<PassingSpeedSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateBefore<PassingSpeedSystem, CarMoveSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateAfter<DiscoveryScanSystem, CarMoveSystem>(SystemUpdatePhase.GameSimulation);
            update.UpdateAt<PassingDebugOverlay>(SystemUpdatePhase.ToolUpdate);
            update.UpdateAfter<VehicleControlsUISystem, SelectedInfoUISystem>(SystemUpdatePhase.UIUpdate);
            update.World.GetOrCreateSystemManaged<SerializerSystem>().SetDirty();
            log.Info("Overtake loaded.");
        }
        public void OnDispose()
        {
        }
    }
}
