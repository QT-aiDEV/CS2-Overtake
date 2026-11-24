using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

// Standalone execution of native managed value kernels. No Unity world is created.
internal sealed class QueueNative
{
    internal const float Step = 4f / 15f;
    private readonly string managed;
    private readonly Type carType;
    private readonly Type float2Type;
    private readonly MethodInfo drivingStyle;
    private readonly Dictionary<int, object> driverSeeds = new();
    private readonly FieldInfo seedValue;
    private readonly Dictionary<(float, float, float), object> cars = new();
    private readonly Func<object, float, float, (float Min, float Max)> range;
    private readonly Func<object, float, float, float> stationary, brakingDistance;
    private readonly Func<object, float, float, float, float> moving;

    internal QueueNative(string managedPath)
    {
        managed = Path.GetFullPath(managedPath);
        AssemblyLoadContext.Default.Resolving += (_, name) => Dependency(name);
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Game.dll"));
        NavigationModule = game.GetType("Game.Simulation.CarNavigationSystem", true)!.Module.ModuleVersionId;
        carType = game.GetType("Game.Prefabs.CarData", true)!;
        var utils = game.GetType("Game.Vehicles.VehicleUtils", true)!;
        float2Type = carType.GetField("m_Turning")!.FieldType;
        drivingStyle = utils.GetMethod("GetDrivingStyle", BindingFlags.Public | BindingFlags.Static)!;
        var seedType = drivingStyle.GetParameters()[1].ParameterType;
        seedValue = seedType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(ushort));
        var roadCondition = game.GetType("Game.Net.LaneCondition", true)!;
        var condition = Activator.CreateInstance(roadCondition)!;
        var wear = roadCondition.GetField("m_Wear")!;
        wear.SetValue(condition, Convert.ChangeType(10, wear.FieldType));
        object[] drive = { Method("GetMaxDriveSpeed", 3).Invoke(null, new object[] { Car(95.55556f), 111.111115f, 0f })!, condition };
        utils.GetMethod("ModifyDriveSpeed", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, drive);
        RoadSpeed = (float)drive[0];
        MethodInfo Method(string name, int count) => utils.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == name && m.GetParameters().Length == count && m.GetParameters()[0].ParameterType == carType);
        var car = Expression.Parameter(typeof(object));
        var x = Expression.Parameter(typeof(float));
        var y = Expression.Parameter(typeof(float));
        var z = Expression.Parameter(typeof(float));
        var nativeCar = Expression.Convert(car, carType);
        stationary = Expression.Lambda<Func<object, float, float, float>>(
            Expression.Call(Method("GetMaxBrakingSpeed", 3), nativeCar, x, y), car, x, y).Compile();

        brakingDistance = Expression.Lambda<Func<object, float, float, float>>(
            Expression.Call(Method("GetBrakingDistance", 3), nativeCar, x, y), car, x, y).Compile();
        moving = Expression.Lambda<Func<object, float, float, float, float>>(
            Expression.Call(Method("GetMaxBrakingSpeed", 4), nativeCar, x, y, z), car, x, y, z).Compile();
        var speedRange = Method("CalculateSpeedRange", 3);
        var limits = Expression.Variable(speedRange.ReturnType);
        var pair = typeof(ValueTuple<float, float>).GetConstructor(new[] { typeof(float), typeof(float) })!;
        range = Expression.Lambda<Func<object, float, float, (float, float)>>(Expression.Block(new[] { limits },
            Expression.Assign(limits, Expression.Call(speedRange, nativeCar, x, y)),
            Expression.New(pair, Expression.Field(limits, "min"), Expression.Field(limits, "max"))), car, x, y).Compile();
    }

    internal Guid NavigationModule { get; }
    internal float RoadSpeed { get; }

    internal float Safety(uint frame, int index)
    {
        if (!driverSeeds.TryGetValue(index, out var seed))
        {
            seed = Activator.CreateInstance(seedValue.DeclaringType!)!;
            seedValue.SetValue(seed, (ushort)((20261003 + index) % ushort.MaxValue));
            driverSeeds.Add(index, seed);
        }
        object[] args = { frame, seed, false, 0f };
        drivingStyle.Invoke(null, args);
        return (float)args[3];
    }

    internal string GameHash => Hash(Path.Combine(managed, "Game.dll"));
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private Assembly? Dependency(AssemblyName name)
    {
        var path = Path.Combine(managed, name.Name + ".dll");
        return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
    }

    internal Assembly LoadMod(string path)
    {
        var context = new AssemblyLoadContext("queue-" + Path.GetFileName(Path.GetDirectoryName(path)), isCollectible: true);
        context.Resolving += (_, name) => Dependency(name);
        return context.LoadFromAssemblyPath(Path.GetFullPath(path));
    }

    internal object Car(float cap, float acceleration = 7, float braking = 13)
    {
        var key = (cap, acceleration, braking);
        if (cars.TryGetValue(key, out var existing)) return existing;
        var value = Activator.CreateInstance(carType)!;
        carType.GetField("m_MaxSpeed")!.SetValue(value, cap);
        carType.GetField("m_Acceleration")!.SetValue(value, acceleration);
        carType.GetField("m_Braking")!.SetValue(value, braking);
        carType.GetField("m_Turning")!.SetValue(value, Activator.CreateInstance(float2Type, 2.443461f, .3490658f));
        cars.Add(key, value);
        return value;
    }

    // Replay models a bounded observed frontier with a supplied 2 m pivot.
    // Native target reach and brake reservation are separate inputs.
    internal float FrontierReach(float speed, float observedMaximum)
        => Math.Min(observedMaximum, Math.Max(Math.Max(1, speed * Step) + 2,
            brakingDistance(Car(95.55556f), speed, Step)));

    internal float AccelerationFloor(float cap, float cruise)
        => Math.Max(0, (range(Car(cap), cruise, Step).Max - cruise) / Step);

    internal float Reachable(float cap, float speed, float wanted)
    {
        var limits = range(Car(cap), speed, Step);
        return Math.Clamp(wanted, Math.Max(0, limits.Min), Math.Max(0, limits.Max));
    }

    internal float Following(float cap, float aheadSpeed, float gap, float safeStep)
    {
        var car = Car(cap);
        if (aheadSpeed <= .001f) return stationary(car, gap, safeStep);
        float afterBrake = Math.Max(0, aheadSpeed - 13 * Step * 2);
        return moving(car, gap + afterBrake * safeStep, afterBrake, safeStep);
    }


}
