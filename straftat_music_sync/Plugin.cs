using BepInEx;
using BepInEx.Logging;
using ComputerysModdingUtilities;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: StraftatMod(true)]
namespace straftat_music_sync;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger;
    private float nextControllerEnsureAt;
    internal const string LogColorInfo = "#E5E7EB";
    internal const string LogColorWarning = "#FACC15";
    internal const string LogColorError = "#EF4444";
    internal const string LogColorSuccess = "#22C55E";
    internal const string LogColorAccent = "#22D3EE";

    public static void LogInfo(string message, bool writeOffline = false, string color = LogColorInfo)
    {
        Logger.LogInfo(message);
        if (writeOffline && PauseManager.Instance != null)
        {
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>{message}</b></color>");
        }
    }

    public static void LogWarning(string message, bool writeOffline = false, string color = LogColorWarning)
    {
        Logger.LogWarning(message);
        if (writeOffline && PauseManager.Instance != null)
        {
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>{message}</b></color>");
        }
    }

    public static void LogError(string message, bool writeOffline = false, string color = LogColorError)
    {
        Logger.LogError(message);
        if (writeOffline && PauseManager.Instance != null)
        {
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>ERROR: {message}</b></color>");
        }
    }

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded.");

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
        MusicSyncBootstrap.Initialize();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        EnsureController("Plugin.Start");
    }

    private void Update()
    {
        if (Time.unscaledTime < nextControllerEnsureAt || MusicSyncController.Instance != null)
        {
            return;
        }

        EnsureController("Plugin.Update");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureController($"SceneLoaded:{scene.name}");
    }

    private void EnsureController(string source)
    {
        nextControllerEnsureAt = Time.unscaledTime + 2f;
        MusicSyncController.EnsureCreated();
    }
}
