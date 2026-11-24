using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly mod, game, math, unity, entities;
    private static int checks;
    private static Type M(string name) => mod.GetType("Overtake." + name, true);
    private static object Field(object value, string name) => value.GetType().GetField(name, All).GetValue(value);
    private static void Set(object value, string name, object input) => value.GetType().GetField(name, All).SetValue(value, input);
    private static object Call(Type type, string method, object target, params object[] args)
        => (type.GetMethod(method, All) ?? throw new MissingMethodException(type.FullName, method)).Invoke(target, args);
    private static void Check(bool value, string stage)
    {
        if (!value) throw new InvalidOperationException(stage);
        Console.WriteLine("PASS " + stage); checks++;
    }
    private static object EnumValue(string name, string value) => Enum.Parse(M(name), value);
    private static float Number(object value, string field) => (float)Field(value, field);
    private static object Entity(int index)
    {
        var value = Activator.CreateInstance(entities.GetType("Unity.Entities.Entity", true));
        Set(value, "Index", index); Set(value, "Version", 1); return value;
    }
    private static object Curve(float start, float length, bool opposing)
    {
        var type = math.GetType("Colossal.Mathematics.Bezier4x3", true);
        var vector = unity.GetType("Unity.Mathematics.float3", true);
        var value = Activator.CreateInstance(type);
        for (int i = 0; i < 4; i++)
            Set(value, new[] { "a", "b", "c", "d" }[i], Activator.CreateInstance(vector,
                opposing ? 3.5f : 0f, 0f, start + length * (opposing ? 3 - i : i) / 3));
        return value;
    }
    private static object Piece(float station, float length, int lane, bool allowed = true)
    {
        var value = Activator.CreateInstance(M("CorridorLane"), true);
        Set(value, "ForwardLane", Entity(lane)); Set(value, "OpposingLane", Entity(lane + 1));
        Set(value, "ForwardCurve", Curve(station, length, false));
        Set(value, "OpposingCurve", Curve(station, length, true));
        Set(value, "StartMetres", station); Set(value, "ForwardStart", 0f); Set(value, "ForwardEnd", 1f);
        Set(value, "OpposingStart", 1f); Set(value, "OpposingEnd", 0f);
        Set(value, "AllowsPassing", allowed); Set(value, "SharedEdgeParameter", true);
        Check((bool)Call(M("CorridorLane"), "PrepareGeometry", value), "prepare canonical road piece at " + station);
        return value;
    }
    private static object Road(params object[] pieces)
    {
        var value = Activator.CreateInstance(M("PassingCorridor"), All, null,
            new[] { Field(pieces[0], "ForwardCurve") }, null);
        var lanes = (IList)Field(value, "lanes");
        foreach (var piece in pieces) lanes.Add(piece);
        return value;
    }
    private static IList Navigation()
        => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("Game.Vehicles.CarNavigationLane", true)));
    private static bool Append(object road, float start, float end, IList result)
        => (bool)Call(M("NativePassingTraversal"), "AppendOpposingIntervals", null, road, start, end, result, 10f);
    private static void ClosedGateAdmission()
    {
        var pool = Activator.CreateInstance(M("PassingPool"), All, null, new[] { Entity(1) }, null);
        foreach (string state in new[] { "Red", "Yellow", "Purple" })
        {
            var gate = Activator.CreateInstance(M("GateDecision"), All, null,
                new object[] { EnumValue("GateState", state), EnumValue("GateReason", "IncompleteTraffic"),
                    0f, 0f, 0f, 0f, 0f, 0f }, null);
            Set(pool, "Gate", gate);
            object[] inputs = { Activator.CreateInstance(entities.GetType("Unity.Entities.EntityManager", true)),
                Entity(2), null, pool, 0u, null };
            var result = Call(M("NativePassingTraversal"), "TryCommit", null, inputs);
            Check(result.ToString() == "WindowClosed" && inputs[5] == null,
                state + " admission returns before any ECS access, fit or route allocation");
        }
    }
    private static void Routes()
    {
        var road = Road(Piece(0, 100, 10), Piece(100, 200, 20));
        var output = Navigation();
        Check(Append(road, 20, 180, output) && output.Count == 16, "route construction spans native pieces in 10 m steps");
        var float2 = unity.GetType("Unity.Mathematics.float2", true);
        float previousStation = 20;
        for (int i = 0; i < output.Count; i++)
        {
            var entry = output[i]; var interval = Field(entry, "m_CurvePosition");
            float from = (float)float2.GetField("x").GetValue(interval), to = (float)float2.GetField("y").GetValue(interval);
            float origin = i < 8 ? 0 : 100, length = i < 8 ? 100 : 200;
            Check(from > to && Math.Abs(origin + (1 - from) * length - previousStation) < .01f
                && Math.Abs(origin + (1 - to) * length - (previousStation + 10)) < .01f,
                "opposing interval is reversed and contiguous " + i);
            Check(Field(entry, "m_Flags").ToString() == "FixedLane", "route has only FixedLane flags " + i);
            previousStation += 10;
        }
        Check(!Append(road, 20, 20, Navigation()), "empty route interval rejected");
        Check(!Append(road, 250, 350, Navigation()), "unverified route suffix rejected");
        Check(!Append(Road(Piece(0, 100, 30, false)), 0, 50, Navigation()), "restricted lane rejected");
        var full = Navigation(); var entryType = game.GetType("Game.Vehicles.CarNavigationLane", true);
        int limit = (int)M("NativeVehicle").GetField("MaximumNavigationEntries", All).GetRawConstantValue();
        for (int i = 0; i < limit; i++) full.Add(Activator.CreateInstance(entryType));
        Check(!Append(road, 20, 30, full) && full.Count == limit, "exhausted navigation capacity rejected without overflow");
    }
    private static void RouteHome()
    {
        var road = Road(Piece(0, 100, 10), Piece(100, 100, 20), Piece(200, 100, 30));
        var entryType = game.GetType("Game.Vehicles.CarNavigationLane", true);
        var float2 = unity.GetType("Unity.Mathematics.float2", true);
        object Entry(int lane, float start, float end)
        {
            var entry = Activator.CreateInstance(entryType);
            Set(entry, "m_Lane", Entity(lane));
            Set(entry, "m_CurvePosition", Activator.CreateInstance(float2, start, end));
            return entry;
        }
        var route = Array.CreateInstance(entryType, 4);
        route.SetValue(Entry(10, .2f, 1), 0);
        route.SetValue(Entry(20, 0, 1), 1);
        route.SetValue(Entry(99, 0, 1), 2); // Original route turns off.
        route.SetValue(Entry(30, 0, 1), 3); // A later return cannot justify skipping the turn.
        var traversal = M("NativePassingTraversal");
        var lease = Activator.CreateInstance(traversal, true);
        Set(lease, "corridor", road);
        Set(lease, "continuation", route);
        traversal.GetField("homeCount", All)?.SetValue(lease, 2);
        var em = Activator.CreateInstance(entities.GetType("Unity.Entities.EntityManager", true));
        var owner = Activator.CreateInstance(game.GetType("Game.Pathfind.PathOwner", true));
        Set(owner, "m_ElementIndex", 12);
        var suffix = Navigation();
        Check(!(bool)Call(traversal, "AppendHome", lease, em, 230f, suffix, owner)
            && suffix.Count == 0, "the actual home splice refuses to skip the vehicle's turn");
        object[] splice = { em, 160f, suffix, owner };
        Check((bool)Call(traversal, "AppendHome", lease, splice) && suffix.Count == 3
            && suffix[1].Equals(route.GetValue(2)) && suffix[2].Equals(route.GetValue(3))
            && (int)Field(splice[3], "m_ElementIndex") == 12,
            "actual home splice preserves the turn, downstream suffix and native path cursor");
        object[] proof = { road, 0, route, route.Length, 50f, 0, 0f };
        Check((bool)Call(traversal, "TryRouteHome", null, proof) && (int)proof[5] == 2
            && Math.Abs((float)proof[6] - 200) < .01f, "verified home prefix ends at original route turn");
        object[] landing = { road, route, proof[5], 160f, -1, 0f };
        Check((bool)Call(traversal, "TryFindHomeEntry", null, landing)
            && (int)landing[4] == 1 && Math.Abs((float)landing[5] - .6f) < .001f,
            "verified landing selects a concrete native lane interval before the turn");
        landing[3] = 230f;
        Check(!(bool)Call(traversal, "TryFindHomeEntry", null, landing),
            "matching a lane after route divergence cannot prove home");
        proof[4] = 205f;
        Check(!(bool)Call(traversal, "TryRouteHome", null, proof), "departure past the route boundary is rejected");
        route.SetValue(Entry(20, .1f, 1), 1);
        proof[4] = 50f;
        Check((bool)Call(traversal, "TryRouteHome", null, proof) && (int)proof[5] == 1,
            "noncontiguous native interval does not extend return authority");
        route.SetValue(Entry(10, .2f, .8f), 0);
        route.SetValue(Entry(20, 0, 1), 1);
        Check((bool)Call(traversal, "TryRouteHome", null, proof) && (int)proof[5] == 1
            && Math.Abs((float)proof[6] - 80) < .01f, "partial native lane ends the return horizon");
        var guarded = Navigation();
        Check((bool)Call(traversal, "AppendDetour", null, road, 80f, 200f, guarded, 10f, true)
            && guarded.Count >= 26, "terminal refill guard uses real opposing intervals inside the proved limit");
        for (int i = guarded.Count - 18; i < guarded.Count; i++)
        {
            var interval = Field(guarded[i], "m_CurvePosition");
            float from = (float)Field(interval, "x"), to = (float)Field(interval, "y");
            if (!(from > to)) throw new InvalidOperationException("terminal guard interval must be nonzero and reversed");
        }
        Check(Math.Abs((float)Field(Field(guarded[guarded.Count - 1], "m_CurvePosition"), "y")) < .001f,
            "protected tail ends at the proved opposing limit");
        var compact = Navigation();
        Check((bool)Call(traversal, "AppendOpposingIntervals", null, road, 40f, 100f, compact, 60f / 14)
            && compact.Count >= 13 && compact.Count <= 15,
            "a 60 metre opposing interval can meet the native refill entry count");
    }

    private static object Admission()
    {
        // Recorded native observations: focused Sprinter/truck run, frame 14647936.
        float start = 49.7967949f, leader = 49.99945f, span = 438.726654f - 384.2204f;
        // Cruise/window are configured preflight conditions, not captured traffic.
        float cruise = 60;
        var carType = game.GetType("Game.Prefabs.CarData", true);
        var car = Activator.CreateInstance(carType);
        Set(car, "m_MaxSpeed", 83.3333359f); Set(car, "m_Acceleration", 7f); Set(car, "m_Braking", 13f);
        Set(car, "m_Turning", Activator.CreateInstance(unity.GetType("Unity.Mathematics.float2", true), 2.094395f, .2617994f));
        var speedRange = game.GetType("Game.Vehicles.VehicleUtils", true).GetMethods(All)
            .Single(method => method.Name == "CalculateSpeedRange" && method.GetParameters().Length == 3
                && method.GetParameters()[0].ParameterType == carType);
        var range = speedRange.Invoke(null, new object[] { car, cruise, 4f / 15f });
        float floor = Math.Max(0, (Number(range, "max") - cruise) / (4f / 15f));
        Check(floor > 0, "native recorded CarData permits acceleration at preflight cruise");
        object[] estimate = { start, leader, cruise, floor, span, null, start };
        Check((bool)Call(M("PassingMotion"), "TryEstimate", null, estimate), "admission motion estimate");
        var motion = estimate[5];
        // The production caller aliases its receiver and out argument. Reflection's
        // argument array cannot express that storage alias; emit the exact call shape.
        var method = new DynamicMethod("AliasedApproach", typeof(bool),
            new[] { typeof(object), typeof(float), typeof(float), typeof(float) }, typeof(Program).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, M("PassingMotion"));
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, M("PassingMotion"));
        il.Emit(OpCodes.Call, M("PassingMotion").GetMethod("TryWithApproachDistance", All)); il.Emit(OpCodes.Ret);
        var approach = (Func<object, float, float, float, bool>)method.CreateDelegate(typeof(Func<object, float, float, float, bool>));
        Check(approach(motion, 80, leader, span) && Number(motion, "CompletionSeconds") > 0,
            "admission approach assignment preserves aliased motion estimate");
        var gate = Call(M("PassingGate"), "EvaluateShared", null, 4000f, float.PositiveInfinity,
            60f, leader, true, EnumValue("GateState", "Red"), EnumValue("RoadCoverageState", "Complete"));
        Check(Field(gate, "State").ToString() == "Green", "admission gate opens in configured clear corridor");
        var nav = Navigation();
        Check(Append(Road(Piece(0, 1000, 100)), 480, 880, nav) && nav.Count >= 13,
            "admission reaches executable opposing-route frontier");
        return motion;
    }
    private static void Returns()
    {
        var type = M("ReturnClearance");
        Check(!(bool)Call(type, "HasClearedLeader", null, 384.2204f, 438.726654f), "recorded alongside vehicle cannot return ahead of truck");
        Check((bool)Call(type, "HasClearedLeader", null, 439f, 438.726654f), "whole package clears leader before return");
        object[] duration = { 460f, 384.2204f, 0f, 0f };
        Check(!(bool)Call(type, "TryMergeDuration", null, duration), "stationary passer has no invented merge time");
        duration = new object[] { 460f, 384.2204f, 49.7967949f, 0f };
        Check((bool)Call(type, "TryMergeDuration", null, duration) && (float)duration[3] > 1.5f,
            "merge duration uses package rear and observed progress");
        Check((float)Call(type, "MinimumGapDuringMerge", null, -5f, 10f, 0f, 4f) == -5f,
            "future open gap cannot erase occupied gap at start of merge");
        Check(!(bool)Call(type, "IsWithinLane", null, 1f, 1f, 1.75f), "partially merged body cannot release lease");
    }
    private static void ReturnStationUnits()
    {
        // A configured bend with the home lane inside its opposing neighbour.
        // This is a coordinate-contract case, not a recorded road or a mock world.
        object Arc(float radius, bool reverse)
        {
            var curve = Activator.CreateInstance(math.GetType("Colossal.Mathematics.Bezier4x3", true));
            var vector = unity.GetType("Unity.Mathematics.float3", true);
            float k = .55228475f;
            var points = new[] { new[] { radius, 0f }, new[] { radius, k * radius },
                new[] { k * radius, radius }, new[] { 0f, radius } };
            for (int i = 0; i < 4; i++)
            {
                var p = points[reverse ? 3 - i : i];
                Set(curve, new[] { "a", "b", "c", "d" }[i], Activator.CreateInstance(vector, p[0], 0f, p[1]));
            }
            return curve;
        }
        var piece = Activator.CreateInstance(M("CorridorLane"), true);
        var opposing = Arc(103.5f, true);
        Set(piece, "ForwardCurve", Arc(100, false)); Set(piece, "OpposingCurve", opposing);
        Set(piece, "ForwardStart", 0f); Set(piece, "ForwardEnd", 1f);
        Set(piece, "OpposingStart", 1f); Set(piece, "OpposingEnd", 0f);
        Set(piece, "SharedEdgeParameter", true);
        Check((bool)Call(M("CorridorLane"), "PrepareGeometry", piece), "native bend geometry has valid opposing correspondence");
        float homeTravel = (float)Call(M("CorridorLane"), "DistanceAt", piece, .51f)
            - (float)Call(M("CorridorLane"), "DistanceAt", piece, .5f);
        var boundsType = math.GetType("Colossal.Mathematics.Bounds1", true);
        var length = math.GetType("Colossal.Mathematics.MathUtils", true).GetMethod("Length", All,
            new[] { opposing.GetType(), boundsType });
        float opposingTravel = (float)length.Invoke(null, new[] { opposing, Activator.CreateInstance(boundsType, .49f, .5f) });
        float physicalSpeed = 10, stationSpeed = physicalSpeed * homeTravel / opposingTravel;
        Check(stationSpeed > 0 && stationSpeed < physicalSpeed, "native opposing arc advances fewer home-station metres");
        var vectorType = unity.GetType("Unity.Mathematics.float3", true);
        var velocity = Activator.CreateInstance(vectorType, -7.071068f, 0f, 7.071068f);
        float mappedSpeed = (float)Call(M("CorridorLane"), "OpposingStationSpeed", piece, .5f, velocity);
        Check(Math.Abs(mappedSpeed - physicalSpeed * 100f / 103.5f) < .001f,
            FormattableString.Invariant($"observed velocity uses the analytic concentric-arc ratio: {mappedSpeed:R}m/s"));
        var self = Activator.CreateInstance(M("VehicleObservation"));
        Set(self, "SpeedMetresPerSecond", physicalSpeed);
        Set(self, "StationSpeedMetresPerSecond", mappedSpeed);
        Set(self, "MaximumStationProgressMetresPerSecond", mappedSpeed);
        Set(self, "MinimumStationProgressMetresPerSecond", Math.Min(mappedSpeed, physicalSpeed * Number(piece, "OpposingMinimumStationScale")));
        Set(self, "RearMetres", 0f); Set(self, "FrontMetres", 5f);
        object[] timing = { 20f, 0f };
        Check((bool)Call(M("VehicleObservation"), "TryMergeDuration", self, timing), "return timing accepts observed station progress");
        Check((float)timing[1] >= 20f / stationSpeed, "return time covers physical travel through the mapped bend");
        var rear = Activator.CreateInstance(M("VehicleObservation"));
        Set(rear, "SpeedMetresPerSecond", 9.8f); Set(rear, "StationSpeedMetresPerSecond", 9.8f);
        Set(rear, "MinimumStationProgressMetresPerSecond", 9.8f);
        Set(rear, "MaximumStationProgressMetresPerSecond", 9.8f);
        Set(rear, "FrontMetres", -15f); Set(rear, "RearMetres", -20f);
        object[] gaps = { rear, 2f, 0f, 0f };
        Call(M("VehicleObservation"), "MinimumMergeGaps", self, gaps);
        Check((float)gaps[3] < 15f, "a physically slower home-lane follower can still close the mapped rear gap");
        Set(rear, "RearMetres", 20f); Set(rear, "FrontMetres", 25f);
        gaps[0] = rear;
        Call(M("VehicleObservation"), "MinimumMergeGaps", self, gaps);
        Check((float)gaps[2] == 15f, "an opening mapped front gap is bounded by its current value");
        // Configured linked rear: tractor 10, rear 8, landing follower 9 station m/s.
        // The root opens a gap while the complete package closes it.
        Set(self, "StationSpeedMetresPerSecond", 10f);
        Set(self, "MaximumStationProgressMetresPerSecond", 10f);
        Set(self, "MinimumStationProgressMetresPerSecond", 8f);
        Set(rear, "StationSpeedMetresPerSecond", 9f);
        Set(rear, "MinimumStationProgressMetresPerSecond", 9f);
        Set(rear, "MaximumStationProgressMetresPerSecond", 9f);
        Set(rear, "FrontMetres", -2f); Set(rear, "RearMetres", -7f);
        gaps = new object[] { rear, 2.5f, 0f, 0f };
        Call(M("VehicleObservation"), "MinimumMergeGaps", self, gaps);
        Check(Math.Abs((float)gaps[3] - (-.5f)) < .001f,
            "slower linked rear closes a return gap despite faster tractor progress");
        Set(rear, "RearMetres", 7f); Set(rear, "FrontMetres", 12f);
        Set(rear, "MinimumStationProgressMetresPerSecond", 8f);
        gaps[0] = rear;
        Call(M("VehicleObservation"), "MinimumMergeGaps", self, gaps);
        Check(Math.Abs((float)gaps[2] - (-3f)) < .001f,
            "slower linked rear of the landing vehicle closes the front gap");
        Set(self, "MinimumStationProgressMetresPerSecond", 0f);
        Check(!(bool)Call(M("VehicleObservation"), "TryMergeDuration", self, timing), "stationary linked rear still prevents a finite merge time");
        Set(piece, "OpposingLane", Entity(101)); Set(piece, "ForwardLane", Entity(100));
        var road = Road(piece);
        var positionMethod = math.GetType("Colossal.Mathematics.MathUtils", true).GetMethod("Position", All,
            new[] { opposing.GetType(), typeof(float) });
        var position = positionMethod.Invoke(null, new[] { opposing, (object).5f });
        var homeDirection = Activator.CreateInstance(vectorType, -.7071068f, 0f, .7071068f);
        float observed = (float)Call(M("PassingCorridor"), "StationSpeed", road, Entity(101), position, velocity, homeDirection);
        Set(position, "y", 1.26025391f);
        Check(Math.Abs(observed - mappedSpeed) < .001f
            && Math.Abs((float)Call(M("PassingCorridor"), "StationSpeed", road, Entity(101), position, velocity, homeDirection) - observed) < .001f,
            "recorded body-centre height does not change road progress on a level bend");
        float homeObserved = (float)Call(M("PassingCorridor"), "StationSpeed", road, Entity(100), position, velocity, homeDirection);
        Check(Math.Abs(homeObserved - 10) < .001f, "home-lane native speed remains in home-station units");
        var outer = Activator.CreateInstance(M("CorridorLane"), true);
        Set(outer, "ForwardCurve", Arc(103.5f, false)); Set(outer, "OpposingCurve", Arc(100, true));
        Set(outer, "ForwardStart", 0f); Set(outer, "ForwardEnd", 1f);
        Set(outer, "OpposingStart", 1f); Set(outer, "OpposingEnd", 0f); Set(outer, "SharedEdgeParameter", true);
        Check((bool)Call(M("CorridorLane"), "PrepareGeometry", outer)
            && Math.Abs((float)Call(M("CorridorLane"), "OpposingStationSpeed", outer, .5f, velocity) - 10.35f) < .001f,
            "opposite bend direction increases station progress without changing physical speed");
        var connector = Activator.CreateInstance(M("CorridorLane"), true);
        Set(connector, "ForwardCurve", Arc(100, false)); Set(connector, "OpposingCurve", Arc(103.5f, true));
        Set(connector, "ForwardStart", 0f); Set(connector, "ForwardEnd", 1f);
        Set(connector, "OpposingStart", 1f); Set(connector, "OpposingEnd", 0f);
        Check((bool)Call(M("CorridorLane"), "PrepareGeometry", connector)
            && Math.Abs((float)Call(M("CorridorLane"), "OpposingStationSpeed", connector, .5f, velocity) - stationSpeed) < .01f,
            "node arc correspondence converts velocity through its native parameter LUT");
    }
    private static void NativeDriveSpeed()
    {
        var car = Activator.CreateInstance(game.GetType("Game.Prefabs.CarData", true));
        Set(car, "m_MaxSpeed", 83.3333359f);
        Set(car, "m_Acceleration", 7f); Set(car, "m_Braking", 13f);
        Set(car, "m_Turning", Activator.CreateInstance(unity.GetType("Unity.Mathematics.float2", true), 2.094395f, .2617994f));
        var lane = Activator.CreateInstance(game.GetType("Game.Net.CarLane", true));
        Set(lane, "m_SpeedLimit", 111.111115f); Set(lane, "m_Curviness", 0f);
        var condition = Activator.CreateInstance(game.GetType("Game.Net.LaneCondition", true));
        Set(condition, "m_Wear", 10f);
        float speed = (float)Call(M("NativeVehicle"), "DriveSpeed", null, car, lane, true, condition);
        Check(Math.Abs(speed - 47.61905f) < .001f,
            "worn-road drive speed applies native wear before the vehicle maximum");
        Check((float)Call(M("NativeVehicle"), "DriveSpeed", null, car, lane, false, condition) == 83.3333359f,
            "unworn fast road respects the native vehicle maximum");
        float floor = (float)Call(M("NativeVehicle"), "StationAccelerationFloor", null, car, 60f, 1f);
        Check(floor > 0 && Math.Abs((float)Call(M("NativeVehicle"), "StationAccelerationFloor", null, car, 58.2f, .97f) - floor * .97f) < .001f,
            "admission acceleration uses native cruise before conversion to station units");
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("Usage: NativeInputs <production-dll> [game-managed-directory]; CS2_MANAGED_PATH is also accepted.");
            string dll = Path.GetFullPath(args[0]);
            string managed = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("CS2_MANAGED_PATH");
            if (string.IsNullOrWhiteSpace(managed) || !Directory.Exists(managed)) throw new ArgumentException("Native game managed directory is required.");
            AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(managed, name.Name + ".dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, name.Name + ".dll")) : null;
            mod = Assembly.LoadFrom(dll); game = Assembly.LoadFrom(Path.Combine(managed, "Game.dll"));
            math = Assembly.LoadFrom(Path.Combine(managed, "Colossal.Mathematics.dll"));
            unity = Assembly.LoadFrom(Path.Combine(managed, "Unity.Mathematics.dll"));
            entities = Assembly.LoadFrom(Path.Combine(managed, "Unity.Entities.dll"));
            object[] probe = { null };
            bool compatible = (bool)Call(M("NativeCompatibility"), "TryVerify", null, probe);
            Check(compatible, "installed game satisfies the production native-contract guard" + (compatible ? "" : ": " + probe[0]));
            Console.WriteLine("Production DLL: " + dll);
            ClosedGateAdmission(); Admission(); Routes(); RouteHome(); NativeDriveSpeed(); Returns(); ReturnStationUnits();
            Console.WriteLine($"{checks} actual-assembly preflight checks passed. No ECS, reservations, movement or save/load execution tested.");
            return 0;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Console.Error.WriteLine("FAIL first prerequisite: " + error.Message); return 1;
        }
    }
}
