using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Linq.Expressions;

// Loads legitimate local game value types; never creates an EntityManager or game process.
internal sealed class NativeGeometry
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Assembly mod, game, math, unity, entities;
    private object? measuredPiece, measuredRoad;
    internal NativeGeometry(string assemblyPath, string managed)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(managed, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, name.Name + ".dll")) : null;
        mod = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        game = Assembly.LoadFrom(Path.Combine(managed, "Game.dll"));
        math = Assembly.LoadFrom(Path.Combine(managed, "Colossal.Mathematics.dll"));
        unity = Assembly.LoadFrom(Path.Combine(managed, "Unity.Mathematics.dll"));
        entities = Assembly.LoadFrom(Path.Combine(managed, "Unity.Entities.dll"));
        object?[] probe = { null };
        if (!(bool)M("NativeCompatibility").GetMethod("TryVerify", All)!.Invoke(null, probe)!)
            throw new InvalidDataException("Native geometry preflight: production registration rejects this Game module: " + probe[0]);
    }
    private Type M(string name) => mod.GetType("Overtake." + name, true)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, All)!.SetValue(target, value);
    private static object Get(object target, string field) => target.GetType().GetField(field, All)!.GetValue(target)!;
    private object Call(string type, string method, object? target, params object[] args) => M(type).GetMethod(method, All)!.Invoke(target, args)!;
    private object Entity(int label)
    {
        var value = Activator.CreateInstance(entities.GetType("Unity.Entities.Entity", true)!)!;
        Set(value, "Index", label); Set(value, "Version", 1); return value;
    }
    private object Curve(float start, float length, bool opposing, string profile)
    {
        var curve = Activator.CreateInstance(math.GetType("Colossal.Mathematics.Bezier4x3", true)!)!;
        for (int i = 0; i < 4; i++)
        {
            int t = opposing ? 3 - i : i;
            float x = (profile == "gentle-curve" && (t == 1 || t == 2) ? length * .05f : 0) + (opposing ? 3.5f : 0);
            float y = profile == "hill" && (t == 1 || t == 2) ? length * .025f : 0;
            var p = Activator.CreateInstance(unity.GetType("Unity.Mathematics.float3", true)!, x, y, start + length * t / 3)!;
            Set(curve, new[] { "a", "b", "c", "d" }[i], p);
        }
        return curve;
    }
    internal GeometryResult Evaluate(string profile, float configuredMetres)
    {
        var inputs = new { profile, configuredMetres, laneLabels = "inert fixture-local values, not persistent actors" };
        try
        {
            int count = profile == "segments" ? 2 : 1;
            var pieces = new List<object>(); float station = 0;
            for (int i = 0; i < count; i++)
            {
                float length = configuredMetres / count;
                var piece = Activator.CreateInstance(M("CorridorLane"), true)!;
                Set(piece, "ForwardLane", Entity(100 + i * 2)); Set(piece, "OpposingLane", Entity(101 + i * 2));
                Set(piece, "ForwardCurve", Curve(i * length, length, false, profile));
                Set(piece, "OpposingCurve", Curve(i * length, length, true, profile));
                Set(piece, "ForwardStart", 0f); Set(piece, "ForwardEnd", 1f); Set(piece, "OpposingStart", 1f); Set(piece, "OpposingEnd", 0f);
                Set(piece, "StartMetres", station); Set(piece, "SharedEdgeParameter", true); Set(piece, "AllowsPassing", profile != "no-passing");
                if (!(bool)Call("CorridorLane", "PrepareGeometry", piece))
                    return new(false, 0, inputs, new { failed = "CorridorLane.PrepareGeometry", piece = i });
                station += (float)Get(piece, "LengthMetres"); pieces.Add(piece);
            }
            var road = Activator.CreateInstance(M("PassingCorridor"), All, null, new[] { Get(pieces[0], "ForwardCurve") }, null)!;
            var lanes = (IList)Get(road, "lanes"); foreach (var piece in pieces) lanes.Add(piece);
            M("PassingCorridor").GetProperty("LengthMetres", All)!.SetValue(road, station);
            measuredPiece = pieces[0]; measuredRoad = road;
            float covered = (float)Call("PassingCorridor", "PassingEndFrom", road, 0f);
            float start = profile == "segments" ? (float)Get(pieces[0], "LengthMetres") - 20 : 20;
            float end = Math.Min(station, start + 80);
            var nav = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("Game.Vehicles.CarNavigationLane", true)!))!;
            bool appended = (bool)Call("NativePassingTraversal", "AppendOpposingIntervals", null, road, start, end, nav, 10f);
            bool reverse = true;
            foreach (var entry in nav)
            {
                var interval = Get(entry!, "m_CurvePosition");
                reverse &= (float)Get(interval, "x") > (float)Get(interval, "y");
            }
            bool passed = profile == "no-passing" ? covered == 0 && !appended : covered > 0 && appended && reverse;
            return new(passed, covered, inputs, new { pieceCount = count, geometricLengthMetres = station, passingCoverageMetres = covered,
                nativeRouteAppendAccepted = appended, nativeNavigationEntries = nav.Count, opposingIntervalsReverse = reverse,
                contract = "Actual compiled CorridorLane.PrepareGeometry -> PassingCorridor.PassingEndFrom -> NativePassingTraversal.AppendOpposing",
                limit = "Constructed curve value types only. No live topology, lane ownership, reservation, movement or steering proof." });
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return new(false, 0, inputs, new { failed = "native-geometry-value-contract", error = error.ToString() });
        }
    }
    internal IEnumerable<(string Stage, Func<int> Operation)> MeasurementWorkloads()
    {
        if (!Evaluate("gentle-curve", 2000).Passed) throw new InvalidDataException("Native profiling geometry prerequisite failed");
        object piece = measuredPiece!, road = measuredRoad!;
        var input = Expression.Parameter(typeof(object), "input");
        var prepare = Expression.Lambda<Func<object, bool>>(Expression.Call(Expression.Convert(input, M("CorridorLane")),
            M("CorridorLane").GetMethod("PrepareGeometry", All)!), input).Compile();
        yield return ("native-CorridorLane.PrepareGeometry", () => prepare(piece) ? 1 : 0);

        var scale = Expression.Lambda<Func<object, float>>(Expression.Property(
            Expression.Convert(input, M("PassingCorridor")),
            M("PassingCorridor").GetProperty("MinimumOpposingStationScale", All)!), input).Compile();
        if (!Evaluate("segments", 2000).Passed) throw new InvalidDataException("Scale measurement geometry prerequisite failed");
        object scaleRoad = measuredRoad!;
        var scalePieces = (IList)Get(scaleRoad, "lanes");
        // Supply the same 64 geometry bounds to each binary. The getter uses
        // only these bounds and is warmed before the measurement.
        while (scalePieces.Count < 64) scalePieces.Add(scalePieces[scalePieces.Count % 2]);
        float expectedScale = scalePieces.Cast<object>().Min(p => (float)Get(p, "OpposingMinimumStationScale"));
        if (scale(scaleRoad) != expectedScale) throw new InvalidDataException("Cached scale differs from the direct geometry minimum");
        yield return ("native-PassingCorridor.MinimumOpposingStationScale-64-pieces", () => BitConverter.SingleToInt32Bits(scale(scaleRoad)));

        Type vector = unity.GetType("Unity.Mathematics.float3", true)!;
        var station = Expression.Variable(typeof(float), "station");
        var position = Expression.New(vector.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) })!,
            Expression.Constant(0f), Expression.Constant(0f), Expression.Constant(100f));
        var projection = M("PassingCorridor").GetMethod("TryStation", All, null,
            new[] { vector, typeof(float).MakeByRefType() }, null)!;
        var project = Expression.Lambda<Func<object, int>>(Expression.Block(new[] { station },
            Expression.Call(Expression.Convert(input, M("PassingCorridor")), projection, position, station),
            Expression.Convert(station, typeof(int))), input).Compile();
        yield return ("native-PassingCorridor.TryStation", () => project(road));

        var navigation = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("Game.Vehicles.CarNavigationLane", true)!))!;
        var output = Expression.Parameter(typeof(object), "output");
        var append = Expression.Lambda<Func<object, object, bool>>(Expression.Call(M("NativePassingTraversal").GetMethod("AppendOpposingIntervals", All)!,
            Expression.Convert(input, M("PassingCorridor")), Expression.Constant(20f), Expression.Constant(100f),
            Expression.Convert(output, navigation.GetType()), Expression.Constant(10f)), input, output).Compile();
        yield return ("native-NativePassingTraversal.AppendOpposing-with-list-clear", () =>
        { navigation.Clear(); return append(road, navigation) ? navigation.Count : 0; });
    }
}
internal sealed record GeometryResult(bool Passed, float VerifiedMetres, object Inputs, object Details);
