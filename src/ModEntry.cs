using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SneckoEye;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static Harmony? HarmonyInstance { get; private set; }

    public static void Initialize()
    {
        try
        {
            Log("SneckoEye v0.1.0 initializing...");

            HttpServer.CaptureMainThread();

            HarmonyInstance = new Harmony("com.hollings.sneckoeye");
            HarmonyInstance.PatchAll(typeof(ModEntry).Assembly);

            HttpServer.Start();
            // NOTE: ApiCardSelector.Activate() removed -- it intercepted ALL card
            // selections globally (including internal game flows) and caused hangs.
            // Card selection is now handled by detecting the UI screen and simulating
            // clicks, same pattern as all other actions.
            StatusOverlay.Create();
            EventLog.Create();

            Log("SneckoEye initialized. API at http://localhost:9000/");
        }
        catch (Exception ex)
        {
            Log("ERROR initializing SneckoEye: " + ex);
        }
    }

    public static void Log(string message)
    {
        Godot.GD.Print("[SneckoEye] " + message);
    }
}
