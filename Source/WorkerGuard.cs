using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace CaiWorkerGuard
{
    /// <summary>
    /// Keeps an error on a CAI 5000 background thread from closing the whole game.
    ///
    /// CAI runs its fog/sight work on its own threads (CombatAI.AsyncActions). On this
    /// .NET build of RimWorld an exception that leaves such a thread ends the process.
    /// This guard catches it, records the real stack trace, and keeps the thread going.
    ///
    /// No game (Verse) types are used here so the logic can be tested outside the game.
    /// </summary>
    public static class WorkerGuard
    {
        public const string HarmonyId = "local.cai.workerguard";

        /// <summary>Full stack traces written for one repeating error before it is throttled.</summary>
        private const int FullReportsPerError = 3;

        /// <summary>A worker thread that actually stopped is rarer and matters more: report more of them in full.</summary>
        private const int FullReportsPerStoppedWorker = 20;

        /// <summary>A restarted loop that fails again this quickly counts as a fast failure.</summary>
        private const int HealthyRunMs = 5000;

        /// <summary>Give up restarting a loop after this many fast failures in a row.</summary>
        private const int MaxFastFailures = 10;

        /// <summary>Idle wake-up so a stopped worker always notices and exits.</summary>
        private const int IdleWaitMs = 500;

        public static Action<string> LogError = _ => { };
        public static Action<string> LogInfo = _ => { };

        /// <summary>Optional file that every report is appended to.</summary>
        public static string ReportFile;

        public static bool Installed { get; private set; }

        /// <summary>"guarded loop" or "last-resort catch only".</summary>
        public static string Mode { get; private set; } = "not installed";

        public static int FaultCount => Volatile.Read(ref faults);

        private static readonly Harmony harmony = new Harmony(HarmonyId);
        private static readonly object fileGate = new object();
        private static readonly Dictionary<string, int> occurrences = new Dictionary<string, int>();
        private static int faults;

        private static FieldInfo aliveField;
        private static FieldInfo gateField;
        private static FieldInfo queueField;
        private static FieldInfo eventField;

        /// <param name="cai">The loaded CombatAI assembly.</param>
        /// <param name="allowLoopReplacement">False forces the last-resort mode (used by tests).</param>
        public static void Install(Assembly cai, bool allowLoopReplacement = true)
        {
            if (Installed)
            {
                return;
            }
            Type worker = cai.GetType("CombatAI.AsyncActions", throwOnError: false)
                          ?? throw new InvalidOperationException("CombatAI.AsyncActions was not found in " + cai.GetName().Name + ".");
            MethodInfo loop = AccessTools.DeclaredMethod(worker, "OffMainThreadActionLoop")
                              ?? throw new MissingMethodException(worker.FullName, "OffMainThreadActionLoop");

            // Always: if anything still gets out of the worker loop, swallow and record it.
            harmony.Patch(loop, finalizer: new HarmonyMethod(typeof(WorkerGuard), nameof(LoopFinalizer)) { priority = Priority.Last });
            Mode = "last-resort catch only";

            // Preferred: run our own copy of the loop that cannot let an error out and keeps
            // working afterwards. Only done when CAI's loop is exactly the one we know.
            string mismatch = allowLoopReplacement ? DescribeMismatch(worker, loop) : "disabled by caller";
            if (mismatch == null)
            {
                harmony.Patch(loop, prefix: new HarmonyMethod(typeof(WorkerGuard), nameof(LoopPrefix)) { priority = Priority.Last });
                Mode = "guarded loop";
            }
            else
            {
                SafeInfo("CAI's worker loop is not the known one (" + mismatch + "); using the last-resort catch only.");
            }
            Installed = true;
        }

        /// <summary>Returns null when CAI's worker matches the layout the guarded loop was written for.</summary>
        private static string DescribeMismatch(Type worker, MethodInfo loop)
        {
            aliveField = AccessTools.DeclaredField(worker, "<Alive>k__BackingField");
            gateField = AccessTools.DeclaredField(worker, "locker_offMain");
            queueField = AccessTools.DeclaredField(worker, "queuedOffThreadActions");
            eventField = AccessTools.DeclaredField(worker, "waitHandle");

            if (aliveField == null || aliveField.IsStatic || aliveField.FieldType != typeof(bool))
            {
                return "Alive flag";
            }
            if (gateField == null || gateField.IsStatic || gateField.FieldType.IsValueType)
            {
                return "queue lock";
            }
            if (queueField == null || queueField.IsStatic || queueField.FieldType != typeof(List<Action>))
            {
                return "action queue";
            }
            if (eventField == null || eventField.IsStatic || !typeof(WaitHandle).IsAssignableFrom(eventField.FieldType))
            {
                return "wait handle";
            }

            // The original loop may only do what ours does: check Alive, take an action,
            // run it, log its error, wait. Anything else means CAI changed; do not replace it.
            var allowed = new HashSet<string>
            {
                "AsyncActions.get_Alive",
                "AsyncActions.DequeueOffThreadAction",
                "Action.Invoke",
                "WaitHandle.WaitOne",
                "Object.ToString",
                "Log.Error"
            };
            var required = new HashSet<string> { "AsyncActions.DequeueOffThreadAction", "Action.Invoke", "WaitHandle.WaitOne" };
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(loop))
            {
                if (instruction.operand is MethodBase called)
                {
                    string name = (called.DeclaringType != null ? called.DeclaringType.Name : "?") + "." + called.Name;
                    if (!allowed.Contains(name))
                    {
                        return "unexpected call to " + name;
                    }
                    required.Remove(name);
                }
            }
            return required.Count == 0 ? null : "missing call to " + required.First();
        }

        // ---- Harmony patches -------------------------------------------------------------

        /// <summary>Runs the guarded loop instead of CAI's. Returns true (run CAI's own loop) only if ours could not start.</summary>
        private static bool LoopPrefix(object __instance)
        {
            return !RunGuardedLoop(__instance);
        }

        private static Exception LoopFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Report("a CAI worker thread, which has now stopped (reload the save to restart it)", __exception, null, FullReportsPerStoppedWorker);
            }
            return null;
        }

        // ---- The guarded loop ------------------------------------------------------------

        /// <summary>
        /// Same job as CombatAI.AsyncActions.OffMainThreadActionLoop, but nothing thrown inside
        /// can leave it. Returns false without doing anything if the worker cannot be read.
        /// </summary>
        public static bool RunGuardedLoop(object worker)
        {
            object gate;
            List<Action> queue;
            WaitHandle wake;
            try
            {
                gate = gateField.GetValue(worker);
                queue = (List<Action>)queueField.GetValue(worker);
                wake = (WaitHandle)eventField.GetValue(worker);
            }
            catch (Exception e)
            {
                Report("reading a CAI worker's state (CAI's own loop is used instead)", e, null);
                return false;
            }
            if (gate == null || queue == null || wake == null)
            {
                return false;
            }

            Action retry = null;
            int fastFailures = 0;
            while (IsAlive(worker))
            {
                Action action = retry;
                retry = null;
                if (action == null)
                {
                    try
                    {
                        lock (gate)
                        {
                            if (queue.Count > 0)
                            {
                                action = queue[0];
                                queue.RemoveAt(0);
                            }
                        }
                        if (action == null)
                        {
                            wake.WaitOne(IdleWaitMs);
                            continue;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // the wait handle was closed: this worker has been shut down
                    }
                    catch (Exception e)
                    {
                        Report("a CAI worker's queue handling", e, null);
                        Thread.Sleep(50); // never spin on a fault that keeps happening
                        continue;
                    }
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Report("a CAI background task", e, action);

                    // CAI's fog loop is one task that runs for the whole life of the map and is
                    // queued only once. If it dies, fog of war freezes, so start it again - unless
                    // it keeps failing straight away.
                    if (IsEndlessLoop(action) && IsAlive(worker))
                    {
                        bool ranWell = Stopwatch.GetElapsedTime(started).TotalMilliseconds >= HealthyRunMs;
                        fastFailures = ranWell ? 0 : fastFailures + 1;
                        if (fastFailures <= MaxFastFailures)
                        {
                            Thread.Sleep(250);
                            retry = action;
                        }
                        else
                        {
                            SafeError("[CAI Worker Guard] " + Describe(action) + " failed " + fastFailures +
                                      " times in a row and was not restarted. Fog of war on that map will stop updating until the save is reloaded.");
                        }
                    }
                }
            }
            return true;
        }

        private static bool IsAlive(object worker)
        {
            try
            {
                return (bool)aliveField.GetValue(worker);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEndlessLoop(Action action)
        {
            try
            {
                return action.Method.Name == "OffThreadLoop";
            }
            catch
            {
                return false;
            }
        }

        // ---- Reporting (must never throw) -------------------------------------------------

        /// <summary>
        /// Records one caught error. The same error (same place, task and exception type) gets a
        /// full stack trace the first <paramref name="fullReports"/> times, then one line per 200,
        /// so a task that fails over and over cannot flood the log or hide a different error.
        /// </summary>
        private static void Report(string where, Exception error, Action action, int fullReports = FullReportsPerError)
        {
            int n = Interlocked.Increment(ref faults);
            string text;
            try
            {
                string task = action != null ? Describe(action) : null;
                int seen = CountOccurrence(where + "|" + task + "|" + error.GetType().FullName);
                if (seen > fullReports && seen % 200 != 0)
                {
                    return;
                }
                var sb = new StringBuilder();
                sb.Append("[CAI Worker Guard] Caught error #").Append(n).Append(" in ").Append(where);
                if (task != null)
                {
                    sb.Append(" [").Append(task).Append(']');
                }
                sb.Append(" on thread ").Append(Environment.CurrentManagedThreadId).Append(". The game keeps running.");
                if (seen > 1)
                {
                    sb.Append(" This same error has now happened ").Append(seen).Append(" times.");
                }
                if (seen == fullReports)
                {
                    sb.Append(" From now on it is reported once per 200.");
                }
                if (seen <= fullReports)
                {
                    sb.AppendLine().Append(Details(error));
                }
                else
                {
                    sb.Append(' ').Append(error.GetType().FullName);
                }
                text = sb.ToString();
            }
            catch
            {
                text = "[CAI Worker Guard] Caught error #" + n + " in " + where + " (details unavailable). The game keeps running.";
            }
            AppendToFile(text);
            SafeError(text);
        }

        private static int CountOccurrence(string key)
        {
            lock (occurrences)
            {
                if (!occurrences.TryGetValue(key, out int seen) && occurrences.Count >= 200)
                {
                    key = "(other)"; // bound the table if errors are endlessly varied
                    occurrences.TryGetValue(key, out seen);
                }
                occurrences[key] = ++seen;
                return seen;
            }
        }

        private static string Details(Exception error)
        {
            try
            {
                return error.ToString();
            }
            catch
            {
                // Some runtimes decorate ToString(); fall back to the raw pieces.
                var sb = new StringBuilder();
                try { sb.Append(error.GetType().FullName); } catch { }
                try { sb.Append(": ").Append(error.Message); } catch { }
                try { sb.AppendLine().Append(error.StackTrace); } catch { }
                return sb.ToString();
            }
        }

        private static string Describe(Action action)
        {
            try
            {
                MethodInfo method = action.Method;
                return (method.DeclaringType != null ? method.DeclaringType.FullName + "." : "") + method.Name;
            }
            catch
            {
                return "unknown task";
            }
        }

        private static void SafeError(string text)
        {
            try { LogError(text); } catch { }
        }

        private static void SafeInfo(string text)
        {
            try { LogInfo(text); } catch { }
        }

        private static void AppendToFile(string text)
        {
            string path = ReportFile;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                lock (fileGate)
                {
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
