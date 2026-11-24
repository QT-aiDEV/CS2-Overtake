using System;
using System.Reflection;
using Game.Pathfind;
using Game.Simulation;
using Game.Vehicles;
using LaneFlags = Game.Vehicles.CarLaneFlags;

namespace Overtake
{
    // A game patch recompiles Game.dll and changes its module identity even when
    // nothing the mod touches changed. Verify the native contract instead: the
    // members this assembly binds to must exist, and enum constants compiled into
    // this assembly must still carry the same values in the running game.
    internal static class NativeCompatibility
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal static bool TryVerify(out string problem)
        {
            try
            {
                problem = Fields(typeof(CarNavigationLane), "m_Lane", "m_CurvePosition", "m_Flags")
                    ?? Fields(typeof(CarCurrentLane), "m_Lane", "m_ChangeLane", "m_CurvePosition", "m_ChangeProgress", "m_LaneFlags")
                    ?? Fields(typeof(CarNavigation), "m_TargetPosition", "m_MaxSpeed")
                    ?? Fields(typeof(CarTrailerLane), "m_Lane", "m_NextLane", "m_CurvePosition")
                    ?? Fields(typeof(Blocker), "m_Blocker", "m_Type")
                    ?? Fields(typeof(PathOwner), "m_ElementIndex", "m_State")
                    ?? Fields(typeof(PathElement), "m_Target", "m_TargetDelta", "m_Flags")
                    ?? Enum(LaneFlags.FixedLane, nameof(LaneFlags.FixedLane))
                    ?? Enum(LaneFlags.Reserved, nameof(LaneFlags.Reserved))
                    ?? Enum(LaneFlags.UpdateOptimalLane, nameof(LaneFlags.UpdateOptimalLane))
                    ?? Enum(LaneFlags.Checked, nameof(LaneFlags.Checked))
                    ?? Enum(LaneFlags.Validated, nameof(LaneFlags.Validated))
                    ?? Enum(LaneFlags.HighBeams, nameof(LaneFlags.HighBeams))
                    ?? Enum(LaneFlags.FixedStart, nameof(LaneFlags.FixedStart))
                    ?? Enum(BlockerType.Continuing, nameof(BlockerType.Continuing))
                    ?? Enum(PathFlags.Debug, nameof(PathFlags.Debug))
                    ?? Enum(PathFlags.Obsolete, nameof(PathFlags.Obsolete))
                    ?? Methods(typeof(VehicleUtils), "GetMaxDriveSpeed", "ModifyDriveSpeed", "CalculateSpeedRange",
                        "GetBrakingDistance", "GetMaxBrakingSpeed", "GetSpeedLimitFactor", "GetDrivingStyle")
                    ?? (typeof(CarNavigationSystem).IsSubclassOf(typeof(Game.GameSystemBase))
                        && typeof(CarMoveSystem).IsSubclassOf(typeof(Game.GameSystemBase)) ? null : "native car systems changed");
            }
            catch (Exception error)
            {
                problem = error.GetType().Name + ": " + error.Message;
            }
            return problem == null;
        }

        private static string Fields(Type type, params string[] names)
        {
            foreach (var name in names)
                if (type.GetField(name, Members) == null) return type.FullName + "." + name + " is missing";
            return null;
        }

        private static string Methods(Type type, params string[] names)
        {
            var methods = type.GetMethods(Members);
            foreach (var name in names)
                if (Array.Find(methods, method => method.Name == name) == null)
                    return type.FullName + "." + name + " is missing";
            return null;
        }

        // The compiled operand is the value this build was written against;
        // Enum.Parse reads the value the running game actually uses.
        private static string Enum<T>(T compiled, string name) where T : struct, IConvertible
        {
            if (!System.Enum.IsDefined(typeof(T), name)) return typeof(T).FullName + "." + name + " is missing";
            var live = (T)System.Enum.Parse(typeof(T), name);
            return Convert.ToUInt64(live) == Convert.ToUInt64(compiled) ? null
                : typeof(T).FullName + "." + name + " changed value";
        }
    }
}
