# The crash this mod cannot catch, and the config change that has held so far

This mod catches ordinary errors on CAI 5000's background threads. On 2026-11-19 the
game crashed again with the mod loaded and active. That crash happened inside the .NET
runtime itself, where no mod can catch it.

What has held so far is one extra line in a game config file. It is described below,
together with what is known and what is not.

## The change

With the game closed:

1. In the game folder, make a copy of `RimWorld.runtimeconfig.json` as a backup.
2. Open `RimWorld.runtimeconfig.json` and add this as the last entry of
   `configProperties`:

   ```json
   "System.Runtime.TieredCompilation.QuickJitForLoops": false
   ```

3. Put a comma at the end of the line above it, so the file stays valid JSON.

The end of the block then looks like this:

```json
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": true,
      "System.Runtime.TieredCompilation.QuickJitForLoops": false
    }
```

## Setup this was seen on

- RimWorld 1.7.5000, the non-Unity ("Waylith") build, runtime `.NET 10.0.15-waylithclr.dev`
- CAI 5000 continued (Workshop 3673768803)
- CAI 5000 Performance Fix (1.7) (Workshop 3827953715)
- Harmony 3.0.0-rc.3 (Workshop 2009463077)
- A large modpack (about 1500 mods)

## The crashes

| Date | What happened |
| --- | --- |
| 2026-11-18, twice | Unhandled `NullReferenceException` in `CombatAI.AsyncActions.OffMainThreadActionLoop`. The game wrote a crash report. This mod did not exist yet. |
| 2026-11-19 | Silent crash with this mod active: access violation inside `coreclr.dll`, exit code `0x80131506`. No game crash report, nothing in `Player.log`. |

All three happened during an autosave, 10 to 25 minutes after loading the map.

## Cause

This is a reading of one Windows crash dump (the 2026-11-19 crash). It has not been
confirmed by the authors of the runtime, Harmony or CAI.

- A CAI worker thread was running CAI's original, unpatched `OffMainThreadActionLoop`.
- The .NET runtime swaps a long-running loop for optimised code while the loop is still
  running. This is called on-stack replacement (OSR).
- For this method, the swap led into the Harmony-patched version of the method. That
  version expects to be called normally. It received a garbage `this` pointer instead.
- On 2026-11-18 the garbage pointer surfaced as a `NullReferenceException`. On
  2026-11-19 this mod's own prefix passed it on to `FieldInfo.GetValue`, which crashed
  inside the runtime.

The same dump showed two of CAI's eight worker threads running the unpatched loop while
five ran the guarded one. So Harmony's redirection of this method was not reliable on
this runtime. The reason is unknown.

With `QuickJitForLoops` off, methods that contain loops are compiled fully optimised the
first time they run. They never go through the quick first version, so the runtime has
nothing to swap mid-loop.

## Status

- Applied 2026-11-19.
- One play session since then, on the same save: about two hours, 14 saves (11 of them
  autosaves), no crash, normal exit. This mod was active and caught nothing.
- One session is encouraging. It is not proof.

## Is this mod still needed?

With the config change in place, the mod is a second line of defence. It still catches
ordinary errors on CAI's threads and logs them with a full stack trace. It did not stop
the crash described here and cannot.

## How to tell whether it worked

- Play past the point where the game used to crash.
- If the game closes with no error, look for a new
  `%LOCALAPPDATA%\CrashDumps\RimWorld.exe.<pid>.dmp` and for ".NET Runtime" or
  "Application Error" entries in the Windows Application event log.
- If it closes with an error, look for a new report in
  `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Crashes`.

## Side effects

- Startup and the first map load may take a little longer, because more code is compiled
  with full optimisation up front.
- If the game does not start at all after the change, revert it.

## Reverting

With the game closed, copy the backup over `RimWorld.runtimeconfig.json`, or delete the
`QuickJitForLoops` line and the comma at the end of the line above it.

A game update or reinstall may replace `RimWorld.runtimeconfig.json` and undo the change
without notice. If the crashes return after an update, check that the line is still there.

## Not tried

Harmony was left at 3.0.0-rc.3. Newer release candidates exist (rc.7 as of 2026-11-18).
Their release notes for rc.4 to rc.7 do not mention OSR or tiered compilation. Updating
Harmony is the next thing to try if the crashes return.
