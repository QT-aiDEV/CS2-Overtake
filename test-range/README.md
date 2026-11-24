# Decision range

Runs the production gate, membership, motion and return-clearance code over a set of scenarios without starting the game. `Test-Production.ps1` runs it automatically. To run it on its own:

```powershell
./test-range/Run-TestRange.ps1 -NativeAssembly ./src/bin/Release/net472/Overtake.dll -ManagedPath '<Cities2_Data/Managed>'
```

It needs the .NET 8 SDK. Results go to `test-range/bin/range-results.json`. A failure exits nonzero and names the first failing check with its inputs.

## Scenarios

| Fixture | Contents |
| --- | --- |
| `constructed-scenarios.json` | Queues of 0, 1, 2, 5, 10 and 20 followers on a 2 km road. Clear, single and continuous oncoming traffic. Restricted and pending road coverage. Varied vehicle capability and length. |
| `recorded-return-trace.json` | Selected snapshots of a recorded pass, replayed through the return checks. |
| `historical-queue-release.json` | Recorded queue release positions used by the queue replay. |

With `-NativeAssembly` and `-ManagedPath`, the scenarios also run the compiled road geometry and opposing-route construction on constructed curves, hills and piece seams. The report also includes small benchmarks of the decision kernels: batch medians, P95 and allocations. They measure function cost, not game frame time.

Optional comparisons:

- `-BaselineAssembly` runs an older build's scalar gate on the same inputs.
- `-RegressionBaselineAssembly` repeats a fixed regression case against an older build.

## Queue replay

```powershell
./test-range/Run-QueueReplay.ps1 -ControlArchive <older release zip> -ManagedPath '<Cities2_Data/Managed>'
```

The replay compares the current build with an older release on recorded queue releases. It runs a simplified straight-road follower model that uses the game's own speed, braking and driving-style functions. It reports entries, returns and refusals for each build. The model is a comparison tool, not a simulation of native driving.
