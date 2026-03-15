using FishySteamworks;
using FishySteamworks.Server;
using HarmonyLib;
using Steamworks;

namespace straftat_music_sync.Patches;

[HarmonyPatch(typeof(CommonSocket), "Send")]
internal static class FishySteamworksSendIssuePatches
{
    [HarmonyPostfix]
    private static void SendPostfix(CommonSocket __instance, EResult __result)
    {
        if (__result != EResult.k_EResultLimitExceeded)
        {
            return;
        }

        MusicSyncController.NotifyTransportLimitExceeded(__instance is ServerSocket);
    }
}
