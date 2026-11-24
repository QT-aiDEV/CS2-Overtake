using System;
using System.Reflection;
using Game;
using Game.Simulation;
using System.Diagnostics;
using System.Collections.Generic;
using Colossal.Logging;

namespace Overtake
{
    public partial class PassingTraversalSystem : GameSystemBase
    {
        private PassingPoolSystem pools;
        private UpdateSystem updateOrder;
        internal void ConfigureNavigationOrder(UpdateSystem update) => updateOrder = update;
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (updateOrder == null) return;
            // Native ModManager loads and initializes each mod sequentially. Resolve
            // replacement car navigators only after every mod has created its systems.
            // Any mod system named like the native navigator is ordered exactly as the
            // native one: detours are written before it, speed is raised after it.
            var nativeAssembly = typeof(Game.Simulation.CarNavigationSystem).Assembly;
            foreach (var system in World.Systems)
            {
                var type = system.GetType();
                if (type.Assembly == nativeAssembly || type.Assembly == typeof(PassingTraversalSystem).Assembly
                    || !type.Name.EndsWith("CarNavigationSystem", StringComparison.Ordinal) || !ordered.Add(type)) continue;
                Order("UpdateBefore", typeof(PassingTraversalSystem), type);
                Order("UpdateAfter", typeof(PassingSpeedSystem), type);
                LogManager.GetLogger(nameof(Overtake)).Info("Ordered passing around replacement navigator " + type.FullName + ".");
            }
        }

        private readonly HashSet<Type> ordered = new HashSet<Type>();

        private void Order(string relation, Type system, Type navigator)
        {
            foreach (var method in typeof(UpdateSystem).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (method.Name == relation && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 2 && method.GetParameters().Length == 1)
                {
                    method.MakeGenericMethod(system, navigator).Invoke(updateOrder, new object[] { SystemUpdatePhase.GameSimulation });
                    return;
                }
        }

        private uint lastFrame = uint.MaxValue;
        private uint validationEpoch = uint.MaxValue;
        private readonly Dictionary<PassingCorridor, CorridorTraffic> returnTraffic = new Dictionary<PassingCorridor, CorridorTraffic>();
        private readonly List<PassingCorridor> expiredRoads = new List<PassingCorridor>();
        private readonly Dictionary<PassingCorridor, bool> roadValidity = new Dictionary<PassingCorridor, bool>();

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            lastFrame = uint.MaxValue;
            validationEpoch = uint.MaxValue;
            roadValidity.Clear();
            returnTraffic.Clear();
            expiredRoads.Clear();
            base.OnGamePreload(purpose, mode);
        }

        protected override void OnUpdate()
        {
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            if (frame == lastFrame) return;
            if (pools.HeldTraversalCount == 0)
            {
                // Retained corridors are only for live leases.
                if (returnTraffic.Count != 0) { returnTraffic.Clear(); roadValidity.Clear(); }
                return;
            }
            lastFrame = frame;
            long started = Stopwatch.GetTimestamp();
            uint epoch = frame >> 4;
            if (epoch != validationEpoch)
            {
                roadValidity.Clear();
                validationEpoch = epoch;
                expiredRoads.Clear();
                foreach (var item in returnTraffic)
                    if (frame - item.Value.Frame >= 32) expiredRoads.Add(item.Key);
                foreach (var road in expiredRoads) returnTraffic.Remove(road);
            }
            foreach (var pool in pools.Pools)
            {
                foreach (var traversal in pool.Traversals)
                {
                    var vehicle = traversal.Vehicle;
                    if (!traversal.LeaseHeld || traversal.State == TraversalState.Aborted || traversal.Road == null
                        || !EntityManager.Exists(vehicle)
                        || !EntityManager.HasComponent<UpdateFrame>(vehicle)
                        || EntityManager.GetSharedComponent<UpdateFrame>(vehicle).m_Index != (frame & 15)) continue;
                    var road = traversal.Road;
                    bool poolObserved = pool.Traffic.Corridor == road && frame - pool.Traffic.Frame < 16;
                    if (!roadValidity.TryGetValue(road, out bool roadValid))
                    {
                        roadValid = poolObserved || road.IsValid(EntityManager);
                        roadValidity.Add(road, roadValid);
                    }
                    CorridorTraffic traffic = null;
                    // Committed cars watch traffic too, so a pass that stopped
                    // fitting is cancelled before the car leaves its lane.
                    if (roadValid && (traversal.State == TraversalState.Passing || traversal.State == TraversalState.Committed))
                    {
                        if (poolObserved) traffic = pool.Traffic;
                        else
                        {
                            // A lease retains its traffic feed even after the pool
                            // changes roads or loses the leader observation.
                            if (!returnTraffic.TryGetValue(road, out traffic))
                                returnTraffic.Add(road, traffic = new CorridorTraffic());
                            if (traffic.Corridor == null || frame - traffic.Frame >= 16)
                            {
                                traffic.Refresh(EntityManager, road, frame, road.RetainedStartMetres);
                            }
                        }
                        if (!traffic.Complete) traffic = null;
                    }
                    traversal.Advance(EntityManager, traffic, roadValid, frame);
                }
            }
            pools.Metrics.Add(frame, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
        }
    }
}
