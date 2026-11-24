using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Serialization.Entities;
using Game;
using Unity.Entities;

namespace Overtake
{
    public partial class PassingPoolSystem : IDefaultSerializable, ISerializable
    {
        private sealed class SavedPool
        {
            internal Entity Leader;
            internal bool Dissolving;
            internal int Corridor;
            internal NativePassingTraversal[] Traversals;
            internal int[] Roads;
        }

        private sealed class SavedCorridor
        {
            internal float StartMetres;
            internal CorridorLane[] Lanes;
            internal PassingCorridor Restored;
        }

        private SavedPool[] savedPools;
        private SavedCorridor[] savedCorridors;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var active = pools.FindAll(pool => pool.Traversals.Exists(traversal => traversal.LeaseHeld));
            var roads = new List<PassingCorridor>();
            foreach (var pool in active)
            {
                if (pool.Corridor != null && !roads.Contains(pool.Corridor)) roads.Add(pool.Corridor);
                foreach (var traversal in pool.Traversals)
                    if (traversal.LeaseHeld && traversal.Road != null && !roads.Contains(traversal.Road)) roads.Add(traversal.Road);
            }
            // Format 4 and later wrap the payload in a sized block, so an older
            // build that meets a newer save skips it instead of failing the load.
            writer.Write(CurrentFormat);
            var payload = writer.Begin();
            writer.Write(roads.Count);
            foreach (var road in roads)
            {
                writer.Write(road.RetainedStartMetres);
                writer.Write(road.Lanes.Count);
                foreach (var lane in road.Lanes)
                {
                    writer.Write(lane.ForwardLane);
                    writer.Write(lane.OpposingLane);
                    writer.Write(lane.ForwardStart);
                    writer.Write(lane.ForwardEnd);
                    writer.Write(lane.ForwardCurve);
                    writer.Write(lane.OpposingCurve);
                }
            }
            writer.Write(active.Count);
            foreach (var pool in active)
            {
                writer.Write(pool.Leader);
                writer.Write(pool.Dissolving);
                writer.Write(roads.IndexOf(pool.Corridor));
                var held = pool.Traversals.FindAll(traversal => traversal.LeaseHeld);
                writer.Write(held.Count);
                foreach (var traversal in held)
                {
                    writer.Write(roads.IndexOf(traversal.Road));
                    traversal.Serialize(writer);
                }
            }
            writer.End(payload);
        }

        private const int CurrentFormat = 4;

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Native deserialization owns an exclusive entity transaction here.
            // Keep remapped inputs only; read live ECS state after loading completes.
            savedPools = null;
            savedCorridors = null;
            reader.Read(out int format);
            if (format < 1) throw new InvalidDataException("Unsupported passing save format.");
            if (format < 4)
            {
                ReadPayload(reader, format);
                return;
            }
            var payload = reader.Begin();
            if (format == CurrentFormat)
            {
                try { ReadPayload(reader, format); }
                catch (Exception) { savedPools = null; savedCorridors = null; }
            }
            // A newer or damaged payload is skipped whole. Cars mid-pass then
            // finish on their saved native route.
            if (!reader.End(payload)) { savedPools = null; savedCorridors = null; }
        }

        private void ReadPayload<TReader>(TReader reader, int format) where TReader : IReader
        {
            reader.Read(out int roadCount);
            savedCorridors = new SavedCorridor[roadCount];
            for (int i = 0; i < roadCount; i++)
            {
                var road = new SavedCorridor();
                reader.Read(out road.StartMetres);
                reader.Read(out int laneCount);
                road.Lanes = new CorridorLane[laneCount];
                for (int j = 0; j < laneCount; j++)
                {
                    var lane = new CorridorLane();
                    reader.Read(out lane.ForwardLane);
                    reader.Read(out lane.OpposingLane);
                    reader.Read(out lane.ForwardStart);
                    reader.Read(out lane.ForwardEnd);
                    reader.Read(out lane.ForwardCurve);
                    reader.Read(out lane.OpposingCurve);
                    road.Lanes[j] = lane;
                }
                savedCorridors[i] = road;
            }
            reader.Read(out int poolCount);
            savedPools = new SavedPool[poolCount];
            for (int i = 0; i < poolCount; i++)
            {
                var pool = new SavedPool();
                reader.Read(out pool.Leader);
                reader.Read(out pool.Dissolving);
                reader.Read(out pool.Corridor);
                reader.Read(out int count);
                pool.Traversals = new NativePassingTraversal[count];
                pool.Roads = new int[count];
                for (int j = 0; j < count; j++)
                {
                    reader.Read(out pool.Roads[j]);
                    pool.Traversals[j] = NativePassingTraversal.Deserialize(reader, format);
                }
                savedPools[i] = pool;
            }
        }

        public void SetDefaults(Context context)
        {
            savedPools = null;
            savedCorridors = null;
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (savedPools == null) return;
            EntityManager.CompleteAllTrackedJobs();
            foreach (var road in savedCorridors)
                PassingCorridor.TryRestore(EntityManager, road.StartMetres, road.Lanes, out road.Restored);
            foreach (var saved in savedPools)
            {
                Entity leader = saved.Leader;
                bool missingLeader = !PassingCorridor.Live(EntityManager, leader);
                if (missingLeader)
                {
                    leader = Entity.Null;
                    foreach (var traversal in saved.Traversals)
                        if (PassingCorridor.Live(EntityManager, traversal.Vehicle)) { leader = traversal.Vehicle; break; }
                }
                PassingPool pool = null;
                if (leader != Entity.Null && !TryCreate(leader, out pool) && !TryGet(leader, out pool)) pool = null;
                if (pool != null)
                {
                    pool.Corridor = RestoredRoad(saved.Corridor);
                    pool.Dissolving = saved.Dissolving || missingLeader || pool.Corridor == null;
                }
                for (int i = 0; i < saved.Traversals.Length; i++)
                {
                    var traversal = saved.Traversals[i];
                    // A duplicate record never touches the route the first one owns.
                    if (leases.ContainsKey(traversal.Vehicle) || !traversal.Restore(EntityManager, RestoredRoad(saved.Roads[i]))) continue;
                    if (pool != null && Membership.TryClaim(leader, traversal.Vehicle))
                        RegisterTraversal(pool, traversal);
                    // Never leave a rewritten route without an owner.
                    else traversal.Detach(EntityManager);
                }
            }
            savedPools = null;
            savedCorridors = null;
        }

        private PassingCorridor RestoredRoad(int index)
        {
            if (index == -1) return null;
            if (index < 0 || index >= savedCorridors.Length) return null;
            return savedCorridors[index].Restored;
        }
    }
}
