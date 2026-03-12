using UnityEngine;
using UnityEngine.SceneManagement;

namespace straftat_music_sync;

internal static class MusicSyncBootstrap
{
    private static bool initialized;

    internal static void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    internal static void EnsureController(string source)
    {
        MusicSyncController.EnsureCreated();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureController($"Bootstrap.SceneLoaded:{scene.name}");
    }
}
