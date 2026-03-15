using FishNet.Managing.Client;
using FishNet.Managing.Server;
using FishNet.Transporting;
using HarmonyLib;

namespace straftat_music_sync.Patches;

[HarmonyPatch(typeof(ClientManager), "Transport_OnClientReceivedData")]
internal static class FishNetClientRawTransferPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ClientReceivedDataArgs args)
    {
        return !MusicSyncController.TryHandleRawClientTransportData(args);
    }
}

[HarmonyPatch(typeof(ServerManager), "Transport_OnServerReceivedData")]
internal static class FishNetServerRawTransferPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ServerReceivedDataArgs args)
    {
        return !MusicSyncController.TryHandleRawServerTransportData(args);
    }
}
