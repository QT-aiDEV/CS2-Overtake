# Architecture

This is a guide to the source for anyone reading or changing it. [README.md](README.md) covers what the mod does for players.

## Design rules

1. **The game drives.** Native navigation, following, braking, lane reservations and movement stay in charge of every car. The mod never moves a vehicle. It edits the car's own navigation buffer so that native driving takes it through the oncoming lane, then gives the original route back.
2. **Write as little native state as possible**, and only for a car the mod currently owns (a *lease*). Before every write, check that the data still looks the way the mod left it. If anything else changed it, back off without writing.
3. **One decision per queue.** A slow vehicle and everything queued behind it share one *pool*: one road model, one traffic observation and one gate. Each follower then checks only its own pass against that shared view.
4. **No speculation is trusted for long.** Road support, queue chains and traffic are re-read from live game data on a fixed cadence. Every later decision re-checks what an earlier cache suggested.

## Life of a pass

1. **Discovery.** After native movement, `DiscoveryScanSystem` runs a job over the vehicles that just moved. It keeps cars on supported lanes that are either natively blocked or held below their own free-drive speed. On the next tick, `SlowLeaderDiscoverySystem` follows each candidate's native blocker chain (`NativeFollowingQueue`) to the slow vehicle at its front. That vehicle becomes the pool's leader.
2. **Pool.** `PassingPoolSystem` owns the pools. Every sixteen ticks, in its own slot, a pool:
   - merges with any pool its chain now leads into, and releases followers that left the queue;
   - extends its road model ahead of the leader (`PassingCorridor`);
   - observes every vehicle on that road (`CorridorTraffic`) and evaluates the shared `PassingGate`.
3. **Commit.** On GREEN, `PassingWaveSystem` offers each follower, front to back, to `NativePassingTraversal.TryCommit`. A follower commits only if all of these hold:
   - it gains enough speed on the leader;
   - its route home is proven: it can merge back on its own route before that route turns off;
   - the oncoming lane is clear for the whole pass, with a second to spare (`PassCommitment`);
   - no car between it and the leader is about to turn across the oncoming lane.

   On commit, the car's navigation buffer is rewritten up to a departure point, followed by short intervals of the opposing lane driven in reverse parameter order. Up to five passes per pool can be in progress.
4. **Pass.** Each native update in the car's own shard, `PassingTraversalSystem` advances the lease. It tops up the opposing intervals as native consumes them and re-runs the fit with fresh traffic:
   - **still in its lane:** a failed fit restores the original buffer and cancels the pass;
   - **in the oncoming lane:** a failed fit behind a moving leader makes the car ease off below the leader's speed and take the first safe gap behind it.

   `PassingSpeedSystem` raises the native speed command during a clean pass and applies the easing-off cap.
5. **Return.** Once the car is clear of the leader, or the leader has left the road, `TryReturn` checks the live gap on the home lane and the native reservations. It then splices the car back onto its original continuation at a concrete landing point, with the same path cursor.
6. **Release.** `PassingObservationSystem` releases the lease only when every body of the vehicle sits wholly inside its own lane and no reversed interval is left in its buffer.

If the road ends before a gap opens, the car slows inside a short guarded tail and retries the merge behind the leader. If that fails too, it asks native route recovery for a new path (`PathFlags.Obsolete`).

## Systems and order

All gameplay systems run in `SystemUpdatePhase.GameSimulation`, ordered against the native car systems in `Integration/Mod.cs`.

| Order | System | Does |
| --- | --- | --- |
| 1 | `PassingObservationSystem` | Observes leases in the shard that moved last tick and releases those that are physically clear. |
| 2 | `SlowLeaderDiscoverySystem` | Turns last tick's scan candidates into pools and members. |
| 3 | `PassingPoolSystem` | Normalizes and evaluates the pools in this tick's slot. Owns the lease registry and save data. |
| 4 | `PassingWaveSystem` | Commits followers of pools observed this tick. |
| 5 | `PassingTraversalSystem` | Advances, cancels, refills and returns leases. Runs before native navigation. |
| — | `CarNavigationSystem` | Native, or a mod's replacement navigator. |
| 6 | `PassingSpeedSystem` | Job: overtake speed and easing-off cap, applied after navigation. |
| — | `CarMoveSystem` | Native. |
| 7 | `DiscoveryScanSystem` | Job: scans the shard that just moved. |

A mod system whose type name ends in `CarNavigationSystem` is treated as a replacement navigator. After all mods load, `PassingTraversalSystem` orders itself before it and `PassingSpeedSystem` after it, as with the native one. Presentation runs separately: `PassingDebugOverlay` in `ToolUpdate` and `VehicleControlsUISystem` after the game's selected-info panel.

## Threading

- **Jobs:** the citywide scan (`DiscoveryScanSystem`, an `IJobChunk`) and the per-lease speed commands (`PassingSpeedSystem`, an `IJobParallelFor`). Both are written to be Burst-compatible. They are Burst-compiled when the mod is built with the official modding toolchain, and run as managed jobs otherwise.
- **Main thread:** per-pool and per-lease decisions only.
- **No fences:** the mod never calls `CompleteAllTrackedJobs` during simulation. Main-thread `EntityManager` reads complete only the jobs that write the types they touch. The scan's lane-support cache is filled on the main thread between jobs, cleared every 256 ticks and treated as a hint only.

## What the mod writes

Only for a vehicle it holds a lease on, and only after checking that the data is still its own:

| Native data | When |
| --- | --- |
| `CarNavigationLane` buffer | Commit (detour), refill, cancel (original restored), return (original continuation spliced back). |
| `CarCurrentLane.m_CurvePosition.z` and `FixedLane` flag | Commit and cancel, to end the current lane at the departure point and later restore it. |
| `CarNavigation.m_MaxSpeed` | During a pass, after native navigation: a raise bounded by one native braking step, or a cap while easing off or stopping in the guarded tail. |
| `PathOwner.m_State` (`Obsolete`) | Only when asking native recovery for a new path. |

The mod adds no components and creates no entities. Destination, path elements and path cursor are never edited. A lease aborts without writing when it finds any of these changed:
- the car's target or path;
- the buffer contents;
- the vehicle's trailer layout.

## Road model

`PassingCorridor` pairs each forward lane with the opposing lane on the same road piece. It joins connected pieces into one longitudinal *station* coordinate in metres along the forward lane. Opposing positions and speeds are mapped into that coordinate through the same cross-road correspondence, so bends and grades do not distort distances. The corridor grows ahead of the leader and drops geometry behind once no lease needs it. A lease keeps its own reference to the geometry it was committed on. It rebinds to a pool's newer corridor only when the native geometry matches exactly.

`SupportedPassingRoads` and `PassingCorridor.UsableLane` decide eligibility from native lane data:
- exactly one through lane each way;
- no `ForbidPassing` flag;
- none of the stop, yield, signal, crossing, roundabout or parking flags;
- no blockage.

## Gate and fit

`PassingGate.EvaluateShared` decides for the whole queue. Its inputs are the verified passing distance ahead of the leader, the nearest oncoming vehicle, a conservative closing speed and the leader's speed, compared against fixed reaction margins. Hysteresis stops it flickering at a boundary.

Each follower's own fit lives in `TryCommit`. It covers:
- **Motion:** a motion estimate from the native acceleration curve (`PassingMotion`), capped by any passer ahead of it in a convoy.
- **Clearance:** the observation distance that motion needs, and the route-home proof.
- **Commitment:** the `PassCommitment` check, time to clear and merge against time until the nearest oncoming arrival.

The commitment check is repeated during the pass with zero spare time, so an admitted pass is never cancelled by the check that admitted it.

## Saves

`PassingPoolSystem` implements the game's system serializer. It saves each live lease with its route bounds, plus a deduplicated table of the corridors they use. Entity references go through the game's remapping reader and writer. Queues, gates and traffic observations are not saved; they are rebuilt.

- **Format 4 and later:** written as `format` followed by a sized block. A build that meets a newer format, or a damaged block, skips it whole, so the city still loads.
- **On load:** each lease is re-validated against live data. One that cannot be restored or owned gives the original route back, or asks native recovery, rather than being dropped.

## Compatibility guard

`NativeCompatibility.TryVerify` runs before any system is registered. It checks, by reflection, every native field, method and enum value the mod relies on. If any is missing or changed, the mod logs which one and registers nothing. The game build the mod was last validated against is logged for information only.

## Units

- **World geometry:** native metres.
- **Distances along the road:** station metres.
- **Physical speeds:** native units per second, kept separate from station rates.
- **Displayed km/h:** native speed × 1.8. This conversion is used only by the UI.
- **Native vehicle step:** 4/15 s (`NativeTiming.StepSeconds`). Each car updates on one of sixteen `UpdateFrame` shards.

## Source map

| Folder | Contents |
| --- | --- |
| `Domain/` | Pure decision code with no game dependencies, compiled directly into the test project: the gate, motion estimates, departure planning, commitment fit, return clearance, terminal recovery, overtake speed and pool membership. |
| `Integration/` | Registration and the compatibility guard. |
| `Traffic/` | Discovery, queues, pools, road model and traffic observation. |
| `Traversal/` | Leases and everything that touches a passing car's native data. |
| `Presentation/` | Vehicle panel bindings and the road radar. |
| `../ui/` | The panel's TypeScript front end. |

## Tests

`tests/` compiles `Domain/` and checks it directly. `tests/NativeInputs/` loads the built DLL with the game's real assemblies and runs the production geometry, route-building and return code on constructed inputs. `test-range/` replays recorded traffic through the production decisions. None of them start the game: ECS execution, physical movement and save/load still need in-game testing. See [tests/README.md](tests/README.md).
