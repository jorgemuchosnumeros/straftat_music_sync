using HarmonyLib;

namespace straftat_music_sync.Patches;

[HarmonyPatch]
internal static class MusicSyncLifecyclePatches
{
    [HarmonyPatch(typeof(SteamLobby), "OnLobbyCreated")]
    [HarmonyPostfix]
    private static void OnLobbyCreatedPostfix()
    {
        MusicSyncBootstrap.EnsureController("SteamLobby.OnLobbyCreated");
        MusicSyncController.NotifyLobbyEvent("SteamLobby.OnLobbyCreated");
    }

    [HarmonyPatch(typeof(SteamLobby), "OnLobbyEntered")]
    [HarmonyPostfix]
    private static void OnLobbyEnteredPostfix()
    {
        MusicSyncBootstrap.EnsureController("SteamLobby.OnLobbyEntered");
        MusicSyncController.NotifyLobbyEvent("SteamLobby.OnLobbyEntered");
    }

    [HarmonyPatch(typeof(SetMenuMusicVolume), "Awake")]
    [HarmonyPostfix]
    private static void OnMusicControllerAwakePostfix()
    {
        MusicSyncBootstrap.EnsureController("SetMenuMusicVolume.Awake");
        MusicSyncController.NotifyLobbyEvent("SetMenuMusicVolume.Awake");
    }

    [HarmonyPatch(typeof(ClientInstance), nameof(ClientInstance.OnStartClient))]
    [HarmonyPostfix]
    private static void OnStartClientPostfix(ClientInstance __instance)
    {
        MusicSyncBootstrap.EnsureController("ClientInstance.OnStartClient");
        MusicSyncController.NotifyLocalClientStarted(__instance, "ClientInstance.OnStartClient");
    }
}
