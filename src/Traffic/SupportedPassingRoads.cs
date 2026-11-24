using System.Collections.Generic;
using Game.Common;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace Overtake
{
    // Eligibility belongs to the current simulation observation, never road history.
    internal sealed class SupportedPassingRoads
    {
        private readonly Dictionary<Entity, bool> lanes = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, bool> owners = new Dictionary<Entity, bool>();
        private uint frame;
        private bool hasFrame;

        internal void BeginFrame(uint currentFrame)
        {
            if (hasFrame && frame == currentFrame)
                return;
            Clear();
            frame = currentFrame;
            hasFrame = true;
        }

        internal bool TryVehicle(EntityManager em, Entity vehicle)
        {
            if (!PassingCorridor.Live(em, vehicle) || !em.HasComponent<CarCurrentLane>(vehicle))
                return false;
            var current = em.GetComponentData<CarCurrentLane>(vehicle);
            if (!math.all(math.isfinite(current.m_CurvePosition))
                || current.m_CurvePosition.x >= current.m_CurvePosition.z
                || !TryLane(em, current.m_Lane))
                return false;
            // A native lane transition is harmless only while both lanes remain supported.
            if (current.m_ChangeLane != Entity.Null && !TryLane(em, current.m_ChangeLane))
            {
                return false;
            }
            return true;
        }

        internal bool TryLane(EntityManager em, Entity lane)
        {
            if (lanes.TryGetValue(lane, out bool eligible))
                return eligible;
            if (QueueLane(em, lane))
            {
                Entity owner = em.GetComponentData<Owner>(lane).m_Owner;
                if (!owners.TryGetValue(owner, out bool supported))
                {
                    supported = PassingCorridor.SupportedOwner(em, owner);
                    owners.Add(owner, supported);
                }
                if (supported)
                {
                    var curve = em.GetComponentData<Game.Net.Curve>(lane).m_Bezier;
                    var direction = curve.d - curve.a;
                    foreach (var sibling in em.GetBuffer<Game.Net.SubLane>(owner, true))
                    {
                        var opposite = sibling.m_SubLane;
                        if (opposite == lane || !QueueLane(em, opposite)) continue;
                        var other = em.GetComponentData<Game.Net.Curve>(opposite).m_Bezier;
                        if (math.dot(direction, other.d - other.a) < 0) { eligible = true; break; }
                    }
                }
            }
            lanes.Add(lane, eligible);
            return eligible;
        }

        // Passing restrictions close the gate; they do not erase an existing queue.
        internal static bool QueueLane(EntityManager em, Entity lane)
        {
            if (!PassingCorridor.Live(em, lane) || !em.HasComponent<Game.Net.CarLane>(lane)
                || !em.HasComponent<Game.Net.Curve>(lane) || !em.HasComponent<Game.Net.Lane>(lane)
                || !em.HasComponent<Owner>(lane) || em.HasComponent<Game.Net.MasterLane>(lane)
                || em.HasComponent<Game.Net.SlaveLane>(lane)) return false;
            const Game.Net.CarLaneFlags excluded = Game.Net.CarLaneFlags.Twoway | Game.Net.CarLaneFlags.Runway
                | Game.Net.CarLaneFlags.ParkingLeft | Game.Net.CarLaneFlags.ParkingRight | Game.Net.CarLaneFlags.SideConnection
                | Game.Net.CarLaneFlags.UTurnLeft | Game.Net.CarLaneFlags.UTurnRight;
            return (em.GetComponentData<Game.Net.CarLane>(lane).m_Flags & excluded) == 0;
        }

        internal void Clear()
        {
            lanes.Clear();
            owners.Clear();
            hasFrame = false;
        }
    }
}
