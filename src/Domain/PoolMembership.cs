using System;
using System.Collections.Generic;

namespace Overtake
{
    // Current pool affiliation is independent of native traversal leases.
    // Each leader keeps its own member set, so transfer and dissolution cost
    // only that pool's members, never every claimed vehicle in the city.
    internal sealed class PoolMembership<T> where T : IEquatable<T>
    {
        private readonly Dictionary<T, T> owners = new Dictionary<T, T>();
        private readonly Dictionary<T, HashSet<T>> members = new Dictionary<T, HashSet<T>>();
        private readonly Stack<HashSet<T>> spare = new Stack<HashSet<T>>();
        internal int Count => owners.Count;
        internal bool TryOwner(T vehicle, out T leader) => owners.TryGetValue(vehicle, out leader);

        internal bool Transfer(T previousLeader, T canonicalLeader)
        {
            if (previousLeader.Equals(canonicalLeader) || !IsLeader(previousLeader) || !IsLeader(canonicalLeader))
                return false;
            var moving = members[previousLeader];
            var target = members[canonicalLeader];
            foreach (var vehicle in moving)
            {
                owners[vehicle] = canonicalLeader;
                target.Add(vehicle);
            }
            Recycle(previousLeader);
            return true;
        }

        internal bool TryClaim(T leader, T vehicle)
        {
            if (owners.TryGetValue(leader, out T upstream) && !upstream.Equals(leader))
                return false;
            if (owners.TryGetValue(vehicle, out T owner))
                return owner.Equals(leader);
            if (!members.TryGetValue(leader, out var set))
            {
                set = spare.Count > 0 ? spare.Pop() : new HashSet<T>();
                members.Add(leader, set);
            }
            owners[leader] = leader;
            owners[vehicle] = leader;
            set.Add(leader);
            set.Add(vehicle);
            return true;
        }

        internal void Release(T leader, T vehicle)
        {
            if (vehicle.Equals(leader) || !owners.TryGetValue(vehicle, out T owner) || !owner.Equals(leader)) return;
            owners.Remove(vehicle);
            members[leader].Remove(vehicle);
        }

        internal void Dissolve(T leader)
        {
            if (!members.TryGetValue(leader, out var set)) return;
            foreach (var vehicle in set) owners.Remove(vehicle);
            Recycle(leader);
        }

        internal void Reset()
        {
            owners.Clear();
            foreach (var set in members.Values) { set.Clear(); spare.Push(set); }
            members.Clear();
        }

        private bool IsLeader(T leader) => owners.TryGetValue(leader, out T owner) && owner.Equals(leader);

        private void Recycle(T leader)
        {
            var set = members[leader];
            members.Remove(leader);
            set.Clear();
            spare.Push(set);
        }
    }
}
