using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace CaiWorkerGuard
{
    /// <summary>RimWorld entry point: installs the guard once CAI's assembly is loaded.</summary>
    public sealed class Entry : Mod
    {
        public const string ReportFileName = "CAI_WorkerGuard.log";

        public Entry(ModContentPack content) : base(content)
        {
            try
            {
                WorkerGuard.LogError = text => Log.Error(text);
                WorkerGuard.LogInfo = text => Log.Message("[CAI Worker Guard] " + text);
                try
                {
                    WorkerGuard.ReportFile = Path.Combine(GenFilePaths.SaveDataFolderPath, ReportFileName);
                }
                catch
                {
                    // No report file; Player.log still gets everything.
                }

                Assembly cai = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(IsCombatAI);
                if (cai == null)
                {
                    Log.Message("[CAI Worker Guard] CAI 5000 is not loaded; nothing to guard.");
                    return;
                }
                WorkerGuard.Install(cai);
                Log.Message("[CAI Worker Guard] Active (" + WorkerGuard.Mode + "). Errors on CAI background threads are logged instead of closing the game.");
            }
            catch (Exception e)
            {
                Log.Error("[CAI Worker Guard] Setup failed; CAI is running unguarded: " + e);
            }
        }

        private static bool IsCombatAI(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name == "CombatAI";
            }
            catch
            {
                return false;
            }
        }
    }
}
