using System.IO;
using Colossal.Serialization.Entities;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Entities;

namespace Overtake
{
    internal sealed partial class NativePassingTraversal
    {
        private NativePassingTraversal() { }

        internal void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Vehicle);
            writer.Write(passedLeader);
            writer.Write(destination);
            writer.Write((int)State);
            writer.Write((int)FailureReason);
            writer.Write((int)ReturnBlock);
            writer.Write(BufferedEndMetres);
            writer.Write(MaximumPassingSpeedMetresPerSecond);
            writer.Write(returnIssued);
            writer.Write(enteredOpposing);
            writer.Write(hasPathCursor);
            if (hasPathCursor) pathCursor.Serialize(writer);
            writer.Write(parts.Length);
            foreach (var part in parts) writer.Write(part);
            writer.Write(continuation.Length);
            foreach (var lane in continuation) lane.Serialize(writer);
            writer.Write(owned.Length);
            foreach (var lane in owned) lane.Serialize(writer);
            writer.Write(homeCount);
            writer.Write(originalPathIndex);
            writer.Write(homeEnd);
            writer.Write(opposingLimit);
            writer.Write(terminalGuard);
            writer.Write(holdingReturn);
            writer.Write(yielding);
            writer.Write(plannedCruise);
        }

        private bool inconsistentSave;

        internal static NativePassingTraversal Deserialize<TReader>(TReader reader, int format) where TReader : IReader
        {
            var saved = new NativePassingTraversal();
            reader.Read(out Entity vehicle); saved.Vehicle = vehicle;
            reader.Read(out saved.passedLeader);
            reader.Read(out saved.destination);
            reader.Read(out int state); saved.State = (TraversalState)state;
            reader.Read(out int failure); saved.FailureReason = (TraversalFailureReason)failure;
            reader.Read(out int block); saved.ReturnBlock = (NativeReturnBlock)block;
            reader.Read(out float end); saved.BufferedEndMetres = end;
            reader.Read(out float speed); saved.MaximumPassingSpeedMetresPerSecond = speed;
            reader.Read(out saved.returnIssued);
            reader.Read(out saved.enteredOpposing);
            reader.Read(out saved.hasPathCursor);
            if (saved.hasPathCursor) saved.pathCursor.Deserialize(reader);
            reader.Read(out int count);
            if (count < 1 || count > NativeVehicle.MaximumParts) throw new InvalidDataException("Invalid saved vehicle layout.");
            saved.parts = new Entity[count];
            for (int i = 0; i < count; i++) reader.Read(out saved.parts[i]);
            saved.continuation = ReadNavigation(reader, NativeVehicle.MaximumNavigationEntries + 1);
            saved.owned = ReadNavigation(reader, NativeVehicle.MaximumNavigationEntries);
            saved.opposingLimit = saved.BufferedEndMetres;
            if (format >= 2)
            {
                reader.Read(out saved.homeCount);
                reader.Read(out saved.originalPathIndex);
                reader.Read(out saved.homeEnd);
                reader.Read(out saved.opposingLimit);
                if (saved.homeCount < 0 || saved.homeCount > saved.continuation.Length
                    || (saved.homeCount > 0 && (saved.originalPathIndex < 0
                        || !(saved.BufferedEndMetres <= saved.opposingLimit && saved.opposingLimit < saved.homeEnd)
                        || float.IsInfinity(saved.homeEnd))))
                    saved.inconsistentSave = true;
            }
            if (format >= 3)
            {
                reader.Read(out saved.terminalGuard);
                reader.Read(out saved.holdingReturn);
                if (saved.terminalGuard && (saved.homeCount == 0 || saved.BufferedEndMetres != saved.opposingLimit)
                    || saved.holdingReturn && !saved.terminalGuard)
                    saved.inconsistentSave = true;
            }
            if (format >= 4)
            {
                reader.Read(out saved.yielding);
                reader.Read(out saved.plannedCruise);
                if (saved.yielding && saved.State != TraversalState.Passing || !(saved.plannedCruise >= 0))
                    saved.inconsistentSave = true;
            }
            if (saved.inconsistentSave)
            {
                // The whole record was read, so the save stays readable. Never
                // trust its route bounds: fall back to the plain detour that
                // returns through the live native path.
                saved.homeCount = 0;
                saved.opposingLimit = saved.BufferedEndMetres;
                saved.terminalGuard = saved.holdingReturn = saved.yielding = false;
                saved.plannedCruise = 0;
            }
            return saved;
        }

        private static CarNavigationLane[] ReadNavigation<TReader>(TReader reader, int maximum) where TReader : IReader
        {
            reader.Read(out int count);
            if (count < 1 || count > maximum) throw new InvalidDataException("Invalid saved navigation length.");
            var result = new CarNavigationLane[count];
            for (int i = 0; i < count; i++) result[i].Deserialize(reader);
            return result;
        }

        internal bool Restore(EntityManager em, PassingCorridor road)
        {
            if (!PassingCorridor.Live(em, Vehicle)) return false;
            corridor = road;
            if (road == null)
            {
                // Without its road the lease cannot steer a return. Before the car
                // has left its lane, simply give the native route back.
                if (TryRestoreRoute(em)) Abort(TraversalFailureReason.PassCancelled);
                else Abort(TraversalFailureReason.RoadChanged);
            }
            else if (!Ready(em, Vehicle) || !RoutePreserved(em) || !PartsUnchanged(em)
                || (!returnIssued && !NavigationOwned(em.GetBuffer<CarNavigationLane>(Vehicle, true))))
                Abort(TraversalFailureReason.RouteChanged);
            return true;
        }
    }
}
