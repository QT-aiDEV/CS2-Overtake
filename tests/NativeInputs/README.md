# Native-input checks

This console project loads the built mod DLL together with the game's managed assemblies. It calls the production code through reflection, using real native value types. It does not create a Unity world or simulate traffic.

```powershell
dotnet run --project tests/NativeInputs/Overtake.NativeInputs.csproj -c Release -- <Overtake.dll> <Cities2_Data/Managed>
```

The managed folder can come from `CS2_MANAGED_PATH` instead. The first failure exits with code 1.

## What it runs

- **Compatibility guard:** `NativeCompatibility.TryVerify` against the installed game.
- **Admission chain:** the native acceleration range, the production motion estimate, the approach adjustment, the shared gate and the opposing-route construction, in that order. Each step must succeed before the next runs.
- **Route building:**
  - 10 m opposing intervals in reverse parameter order;
  - seams between road pieces;
  - unsupported or no-passing suffixes;
  - the navigation buffer limit;
  - the guarded terminal tail.
- **Route home:** a landing must come before the original route turns, and the remaining buffer and path cursor must be kept.
- **Return clearance:**
  - whole-body clearance of the leader;
  - merge timing at observed speeds;
  - a gap that closes during the merge;
  - lane containment;
  - bends in both directions, using station rates for timing and physical speeds for braking.

## Inputs

- The vehicle values come from a recorded pass of a van overtaking a truck: native stations and speeds, plus the van's native `CarData` limits.
- Road curves are constructed parallel lanes and concentric arcs.
- Entity values are local labels, not saved actors.

The clear observation windows, cruise speeds and gaps are configured test conditions, not captured traffic.
