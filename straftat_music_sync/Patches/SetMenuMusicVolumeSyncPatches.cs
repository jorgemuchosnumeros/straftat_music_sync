using HarmonyLib;

namespace straftat_music_sync.Patches;

[HarmonyPatch(typeof(SetMenuMusicVolume))]
internal static class SetMenuMusicVolumeSyncPatches
{
    [HarmonyPatch(nameof(SetMenuMusicVolume.NextTrack))]
    [HarmonyPrefix]
    private static bool NextTrackPrefix()
    {
        return !MusicSyncController.ShouldBlockLocalTransport();
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.NextTrack))]
    [HarmonyPostfix]
    private static void NextTrackPostfix(SetMenuMusicVolume __instance)
    {
        if (__instance.MusicTracks.Count == 0 || MusicSyncController.IsApplyingRemoteState())
        {
            return;
        }

        MusicSyncController.PublishHostState(0f, false);
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.PreviousTrack))]
    [HarmonyPrefix]
    private static bool PreviousTrackPrefix()
    {
        return !MusicSyncController.ShouldBlockLocalTransport();
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.PreviousTrack))]
    [HarmonyPostfix]
    private static void PreviousTrackPostfix(SetMenuMusicVolume __instance)
    {
        if (__instance.MusicTracks.Count == 0 || MusicSyncController.IsApplyingRemoteState())
        {
            return;
        }

        MusicSyncController.PublishHostState(0f, false);
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.Pause))]
    [HarmonyPrefix]
    private static bool PausePrefix()
    {
        return !MusicSyncController.ShouldBlockLocalTransport();
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.Pause))]
    [HarmonyPostfix]
    private static void PausePostfix()
    {
        if (!MusicSyncController.IsApplyingRemoteState())
        {
            MusicSyncController.PublishHostState();
        }
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.Play))]
    [HarmonyPrefix]
    private static bool PlayPrefix()
    {
        return !MusicSyncController.ShouldBlockLocalTransport();
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.Play))]
    [HarmonyPostfix]
    private static void PlayPostfix()
    {
        if (!MusicSyncController.IsApplyingRemoteState())
        {
            MusicSyncController.PublishHostState();
        }
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.SetAudioPosition))]
    [HarmonyPrefix]
    private static bool SetAudioPositionPrefix()
    {
        return !MusicSyncController.ShouldBlockLocalTransport();
    }

    [HarmonyPatch(nameof(SetMenuMusicVolume.SetAudioPosition))]
    [HarmonyPostfix]
    private static void SetAudioPositionPostfix()
    {
        if (!MusicSyncController.IsApplyingRemoteState())
        {
            MusicSyncController.PublishHostState();
        }
    }

    [HarmonyPatch("UpdateMusic")]
    [HarmonyPrefix]
    private static void UpdateMusicPrefix(ref bool __state)
    {
        __state = false;
        if (!MusicSyncController.ShouldOverrideInGameMusic() || Settings.Instance == null)
        {
            return;
        }

        __state = Settings.Instance.inGameMusic;
        Settings.Instance.inGameMusic = true;
    }

    [HarmonyPatch("UpdateMusic")]
    [HarmonyPostfix]
    private static void UpdateMusicPostfix(bool __state)
    {
        if (MusicSyncController.ShouldOverrideInGameMusic() && Settings.Instance != null)
        {
            Settings.Instance.inGameMusic = __state;
        }
    }
}

#pragma warning disable Harmony003
[HarmonyPatch(typeof(ChatBroadcast), "OnMessageReceived")]
internal static class ChatBroadcastMessageFilterPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ChatBroadcast.Message msg)
    {
        return msg.username != MusicSyncController.BroadcastUserName;
    }
}
#pragma warning restore Harmony003
