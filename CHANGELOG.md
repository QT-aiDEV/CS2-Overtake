# Changelog

## 0.1.7

- Renamed from Two Lane Passing to Overtake. The mod now lives in an `Overtake` folder; delete the old `TwoLanePassing` folder so both are not loaded. Passes in progress in older saves are not carried over and finish on the game's own routing.
- A pass is re-checked against live oncoming traffic after it starts. Before the car leaves its lane, a pass that no longer fits is cancelled and the original route restored. In the oncoming lane behind a moving leader, the car eases off and tucks back in behind it.
- Nobody overtakes a car whose route turns across the oncoming lane. A car turning into its own side can be passed, and passers return as soon as it has left the road.
- A leader that turns off or parks no longer keeps a passer in the oncoming lane until the end of the passing zone.
- Return gap checks ignore cars that have left the road since the last traffic snapshot.
- Leases that cannot be restored on load give the car its original route back instead of being dropped. A vehicle whose trailer layout changes mid-pass is handed back to native driving.
- Save data is written in a sized block, so a save from a newer version loads with the mod's data skipped. Because of the rename, a save made with 0.1.7 also loads under the old Two Lane Passing builds, which simply find no passing data in it.
- Any mod that replaces the game's car navigation system is ordered around automatically.

## 0.1.6

- The citywide discovery scan runs as a job on worker threads after native movement.
- Overtake speed and recovery caps are applied in a job between native navigation and movement. Main-thread job fences are gone.
- The overtake speed is bounded by one native braking step, so native braking for a curve or a slower lane is never hidden.
- Leases are released when a car parks or despawns mid-pass, retiring pools no longer accept new members, and one inconsistent saved lease no longer fails the load.
- Pool membership uses a per-leader index, and the panel looks up the selected car directly.
- New "Mod cost" row in the vehicle panel.

## 0.1.5

- Game patches no longer disable the mod. It verifies the native members it uses instead of pinning one game build.
- Pools are evaluated in sixteen staggered slots instead of all on one tick.
- Discovery skips free-flowing cars.
- A pass needs a real speed advantage over the leader, and passers may run slightly above the posted limit while in the oncoming lane.
- Followers can enter behind a pass already in progress (convoys), but never in front of a car already passing behind them.

## 0.1.0 – 0.1.4

Early alpha builds: shared passing pools, the native wave, the road radar and vehicle panel, continuous road horizons across road pieces, and proven routes home.
