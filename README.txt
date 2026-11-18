CAI 5000 Worker Crash Guard (local)
===================================

Why this exists
---------------
On 2026-11-18 the game closed twice with the same error: a NullReferenceException
escaped a background thread of CAI 5000 (CombatAI.AsyncActions.OffMainThreadActionLoop).
This build of RimWorld runs on .NET, where an uncaught error on any thread ends the
whole game. The crash reports could not show where the error really came from.

What it does
------------
- Runs CAI's worker loop inside a guard, so an error there is caught and logged
  instead of closing the game, and the worker keeps going.
- If CAI's fog-of-war loop is the thing that failed, it is started again
  (it gives up after 10 failures in a row).
- If an error still escapes the loop, it is caught as a last resort. That one
  worker stops until the save is reloaded, but the game stays open.
- If CAI is updated and its worker loop no longer looks like the known one, only
  the last-resort catch is used. If CAI is not loaded, the mod does nothing.

Where to look
-------------
Player.log, at startup, should contain:
    [CAI Worker Guard] Active (guarded loop). ...
Every caught error is written, with its full stack trace, to Player.log and to
    %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\CAI_WorkerGuard.log
That file only exists once something has been caught. Its stack trace is what
identifies the real bug.

Load order
----------
After Harmony, CAI 5000 continued and CAI 5000 Performance Fix.

Removing it
-----------
Disable it in the mod list, or delete this folder. It stores nothing in saves.

Rebuilding
----------
    cd Source
    dotnet build -c Release
The DLL is copied to ..\Assemblies automatically. Needs the .NET 10 SDK.
