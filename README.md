# Overtake

Overtaking on rural two-lane roads for Cities: Skylines II.

When a truck, bus or slow car holds up a queue on an undivided two-lane road, the cars behind it pull into the oncoming lane, pass and tuck back in, the way drivers do on a real highway. When the road ahead is clear, the whole queue goes, one after another, instead of one car at a time.

The game still does all the driving. The mod only hands each passing car a short detour through the oncoming lane and gives it back its own route afterwards, so braking, following distance and lane reservations stay vanilla.

**Status:** alpha, version `0.1.7`.

## What it does

- **Group passes.** Behind a slow vehicle, followers leave in a string while the window stays open, with up to five passes in progress per queue.
- **Decisive passes.** A car only starts a pass it can finish with a real speed advantage. While it is in the oncoming lane it may run slightly above the posted limit, never by more than it can brake off in one game update.
- **Bailing out.** A pass is re-checked after it starts. If oncoming traffic appears before the car has left its lane, it simply stays in line. If the car is already out behind a moving leader, it eases off and tucks back in behind.
- **Turning traffic.** Nobody overtakes a car about to turn across the oncoming lane. A car slowing to turn into its own side of the road can be passed when the road is clear.
- **Road rules.** The game's own no-passing markings are respected. Passing stays off at junctions, traffic lights, stop and yield signs, level crossings, roundabouts and roads with parking lanes.

## Where it works

Undivided roads with exactly one through lane in each direction, straight or gently bending, across ordinary road pieces and nodes. It does nothing on four-lane roads, divided highways, one-way roads or through intersections.

## Vehicle panel

Select a road vehicle and open the car icon tab.

- **Leader view:** the queue, cars passing and returning, and passes completed behind this vehicle.
- **Follower view:** its state, the leader's speed and the shared gate, plus why it is waiting if it is.
- **Road details** (collapsed): verified passing distance, gate boundaries, nearest oncoming car and the mod's own main-thread cost per tick ("Mod cost").

The gate is shared by the whole queue:

| Gate | Meaning |
| --- | --- |
| GREEN | The window is open. Each follower still checks its own pass before it goes. |
| YELLOW | The window is closing. No new passes start. |
| RED | Oncoming traffic, or traffic that cannot be fully observed, closes the window. |
| PURPLE | The road ahead does not allow passing (a junction, a no-passing zone or the end of the road). |

**Automatic passing** switches the mod on and off. Turning it off stops new passes and lets the ones in progress finish. **Road radar** draws the selected queue's window on the road ahead of the leader, shading from green to red, with a white crest for approaching traffic. Both controls reset when the game restarts.

## Compatibility

- **No Harmony patches.** The mod adds its own systems and writes only native vehicle navigation data while a car is passing. See [ARCHITECTURE.md](ARCHITECTURE.md#what-the-mod-writes).
- **Game updates.** On load the mod checks, by reflection, every game field, enum value and method it relies on. If a patch changes one of them, the mod disables itself and logs which one. Other patches load normally.
- **Other navigation mods.** Mods that replace the game's car navigation system are detected and ordered around automatically. A car whose route another mod changes mid-pass is released to that mod untouched.
- **Saves.** The mod adds no components to entities. It stores the passes in progress in its own save block. A save from a newer version of the mod loads with that block skipped. Queues and traffic are rebuilt after loading.

To remove the mod cleanly, turn **Automatic passing** off, wait a few seconds for the passes in progress to finish, then save. A save taken mid-pass and loaded without the mod still loads, and those cars finish using the game's own route recovery.

## Performance

The city-wide scan runs as a job on worker threads, right after the game moves vehicles. The main thread only handles the queues it finds, and each queue is evaluated in its own one-in-sixteen tick slot. On roads where nobody is queued, the mod does almost nothing. The "Mod cost" row shows the real main-thread cost in your city.

## Install

Close the game and copy `Overtake.dll`, `Overtake.mjs` and `Overtake.css` into a `Overtake` folder under:

```text
%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods
```

The log at `Logs\Overtake.log` reads `Overtake loaded.` when the mod starts.

## Build from source

You need a .NET SDK that targets `net472` and `net8.0`, Node.js with npm, and an installed copy of the game. Game assemblies are referenced from your installation and never distributed.

```powershell
$env:CS2_MANAGED_PATH = '<game folder>\Cities2_Data\Managed'
./Test-Production.ps1
```

```powershell
Push-Location ui; npm ci; npm run typecheck; npm run check; npm run build; Pop-Location
```

`Test-Production.ps1` runs the logic checks, builds `src/bin/Release/net472/Overtake.dll` and runs the production code against the game's real assemblies. The UI build writes `ui/dist/Overtake.mjs` and `.css`. Neither step installs anything. See [tests/README.md](tests/README.md) for what is and is not covered offline.

The project builds without the official modding toolchain. Without it, the jobs run as ordinary managed jobs on worker threads. Built with the toolchain, Burst compiles them.

## Source

Start with [ARCHITECTURE.md](ARCHITECTURE.md). It walks through a pass from discovery to release, the system order, the threading and everything the mod writes into game data. Version history is in [CHANGELOG.md](CHANGELOG.md).

## Known limits

- The behaviour has been tuned against the game's own driving rules but has had limited in-game testing across different cities and playsets.
- If a car's return is blocked at the very end of a passing zone and no gap opens, the game's own route recovery takes over. That works, but it is not graceful.
- A vehicle whose trailer layout changes mid-pass is handed back to the game's own driving instead of being tracked.

Bug reports with the log and a description of the road are welcome.

## License

GPL-3.0. See [LICENSE](LICENSE). [NOTICE-MIT.txt](NOTICE-MIT.txt) keeps the notices of the MIT-licensed projects this one started from.
