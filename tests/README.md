# Offline checks

Run `./Test-Production.ps1` from the project root with `CS2_MANAGED_PATH` set to the game's `Cities2_Data/Managed` folder. It stops at the first failing stage. It never starts the game or installs anything.

1. **Logic checks** (`tests/`). This project compiles `src/Domain/` directly and checks:
   - the gate;
   - motion estimates, departure planning and the commitment fit;
   - return clearance, terminal recovery and overtake speed;
   - discovery eligibility and pool membership.
2. **Release build** of the mod DLL.
3. **Native-input checks** (`tests/NativeInputs/`). These load the built DLL with the game's real assemblies. They run the production geometry, route-building, admission and return code on constructed lane curves and recorded vehicle values, including the compatibility guard. See [NativeInputs/README.md](NativeInputs/README.md).
4. **Decision range** (`test-range/`). This replays recorded and constructed traffic through the production decisions. See [../test-range/README.md](../test-range/README.md).

## What this does not cover

There is no Unity world offline. None of the checks run the ECS systems themselves, native driving, lane reservations, physical lane changes or save and load. Those need the game.

Useful in-game checks after a change:
- a queue behind a slow truck on a long straight;
- a pass cancelled or abandoned when oncoming traffic appears;
- a car turning off ahead of a queue;
- a save and load in the middle of a pass;
- the "Mod cost" row in a large city.
