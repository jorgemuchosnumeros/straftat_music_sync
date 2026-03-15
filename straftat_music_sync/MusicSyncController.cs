using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using HarmonyLib;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace straftat_music_sync;

internal sealed class MusicSyncController : MonoBehaviour
{
    internal const string BroadcastUserName = "__straftat_music_sync__";
    private const string ControllerObjectName = "StraftatMusicSyncController";
    private const string LobbyStateDataKey = "sms_state";

    private const float PeriodicSyncIntervalSeconds = 3f;
    private const float RequestSyncIntervalSeconds = 2f;
    private const float LobbyPollIntervalSeconds = 0.75f;
    private const float AllowedTimeDriftSeconds = 0.75f;
    private const float TrackTransferRequestRetrySeconds = 3f;
    private const float TransferProgressLogIntervalSeconds = 2f;
    private const float DuplicatePublishWindowSeconds = 0.2f;
    private const float DuplicatePublishPositionToleranceSeconds = 0.2f;
    private const int TransferProgressBarWidth = 24;
    private const int TransferChunksPerFrame = 4;
    private const int TransferChunkSizeBytes = 640;
    private const int TransferWindowChunks = 24;
    private const int TransferAckEveryChunks = 8;
    private const int MaxTransferSizeBytes = 32 * 1024 * 1024;
    private const int TransferTrackNumberStart = 1_000_000;

    private static readonly MethodInfo LoadTrackMethod = AccessTools.Method(typeof(SetMenuMusicVolume), "LoadTrack");
    private static readonly FieldInfo TrackNumberToIndexField = AccessTools.Field(typeof(SetMenuMusicVolume), "TrackNumberToIndex");
    private static readonly Regex LooseTokenRegex = new("[^\\p{L}\\p{Nd}]+", RegexOptions.Compiled);
    private static readonly Regex LeadingTrackNumberRegex = new("^\\s*\\d+\\s*-\\s*", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> TrackAudioHashByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TrackAudioStemByPath = new(StringComparer.OrdinalIgnoreCase);
    private static MethodInfo steamMatchmakingGetLobbyDataMethod;
    private static bool steamMatchmakingLoggedUnavailable;
    private static MethodInfo steamMatchmakingRequestLobbyDataMethod;
    private static MethodInfo steamMatchmakingSetLobbyDataMethod;

    private bool broadcastHandlersRegistered;
    private bool hasRemoteSync;
    private bool isApplyingRemoteState;
    private bool wasSyncContextActive;
    private float lastStateReceivedAt;
    private float nextClientRequestAt;
    private float nextHostSyncAt;
    private float nextLobbyPollAt;
    private float lastPublishedAt = float.NegativeInfinity;
    private float lastPublishedPositionSeconds = float.NegativeInfinity;
    private string lastMissingTrackIdentity = string.Empty;
    private string lastAnnouncedTrackIdentity = string.Empty;
    private string lastLobbyStatePayload = string.Empty;
    private string lastPublishedStateFingerprint = string.Empty;
    private bool? lastAnnouncedPaused;
    private long outboundSequence;
    private long lastAppliedSequence;
    private MusicSyncWireMessage pendingState;
    private readonly Dictionary<string, IncomingTrackTransfer> incomingTrackTransfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OutgoingTrackTransfer> outgoingTrackTransfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeHostTransferKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> requestedTrackTransferKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> requestedTrackTransferAtByKey = new(StringComparer.OrdinalIgnoreCase);

    internal static MusicSyncController Instance { get; private set; }

    internal static bool EnsureCreated()
    {
        if (Instance != null)
        {
            return false;
        }

        var existing = FindFirstObjectByType<MusicSyncController>();
        if (existing != null)
        {
            Instance = existing;
            return false;
        }

        var controllerObject = new GameObject(ControllerObjectName);
        controllerObject.AddComponent<MusicSyncController>();
        return true;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Update()
    {
        EnsureBroadcastHandlersRegistered();

        var syncContextActive = IsSyncContextActive();
        if (syncContextActive && !wasSyncContextActive)
        {
            OnSyncContextActivated();
        }

        wasSyncContextActive = syncContextActive;

        if (!syncContextActive)
        {
            ResetClientSyncState();
            return;
        }

        if (IsHost())
        {
            if (HasActiveOutgoingTransfers())
            {
                if (Time.unscaledTime >= nextHostSyncAt)
                {
                    nextHostSyncAt = Time.unscaledTime + PeriodicSyncIntervalSeconds;
                }

                return;
            }

            if (Time.unscaledTime >= nextHostSyncAt)
            {
                PublishHostState();
            }

            return;
        }

        if (Time.unscaledTime >= nextLobbyPollAt)
        {
            TryQueueStateFromLobbyMetadata();
            nextLobbyPollAt = Time.unscaledTime + LobbyPollIntervalSeconds;
        }

        TryApplyPendingState();

        if (Time.unscaledTime >= nextClientRequestAt &&
            (!hasRemoteSync || Time.unscaledTime - lastStateReceivedAt >= PeriodicSyncIntervalSeconds * 2f))
        {
            if (!TryQueueStateFromLobbyMetadata(force: true))
            {
                RequestHostSync();
            }
        }
    }

    private void OnSyncContextActivated()
    {
        if (IsHost())
        {
            PublishHostState();
            return;
        }

        RequestHostSync();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        HandleLifecycleEvent($"SceneLoaded:{scene.name}");
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Plugin.LogWarning("[MusicSync] Controller destroyed.", true);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CleanupIncomingTransfers();
            CleanupOutgoingTransfers();
            TryUnregisterBroadcastHandlers();
            Instance = null;
        }
    }

    internal static bool ShouldBlockLocalTransport()
    {
        return Instance != null && Instance.IsFollowerClient();
    }

    internal static bool ShouldOverrideInGameMusic()
    {
        return Instance != null && Instance.IsFollowerClient();
    }

    internal static bool IsApplyingRemoteState()
    {
        return Instance != null && Instance.isApplyingRemoteState;
    }

    internal static void NotifyLobbyEvent(string source)
    {
        Instance?.HandleLifecycleEvent(source);
    }

    internal static void NotifyLocalClientStarted(NetworkBehaviour behaviour, string source)
    {
        if (Instance == null || behaviour == null || !behaviour.IsOwner)
        {
            return;
        }

        Instance.HandleLifecycleEvent(source);
    }

    internal static void PublishHostState(float? positionSeconds = null, bool? paused = null)
    {
        Instance?.PublishHostStateInternal(positionSeconds, paused);
    }

    private void PublishHostStateInternal(float? positionSeconds = null, bool? paused = null)
    {
        if (!TryCreateSnapshot(positionSeconds, paused, out var state))
        {
            return;
        }

        if (ShouldSkipDuplicatePublish(state))
        {
            return;
        }

        BroadcastToClients(state);
        TryWriteLobbyMetadata(state);
        RememberPublishedState(state);
        nextHostSyncAt = Time.unscaledTime + PeriodicSyncIntervalSeconds;
    }

    private void RequestHostSync()
    {
        if (!InstanceFinder.IsClient || InstanceFinder.IsServer || InstanceFinder.ClientManager == null)
        {
            return;
        }

        BroadcastFromClient(new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.Request
        });

        nextClientRequestAt = Time.unscaledTime + RequestSyncIntervalSeconds;
    }

    private bool TryCreateSnapshot(float? positionSeconds, bool? paused, out MusicSyncWireMessage state)
    {
        state = null;
        if (!IsHost())
        {
            return false;
        }

        var music = SetMenuMusicVolume.Instance;
        if (music == null || music.MusicTracks.Count == 0 || music.currentTrackIndex < 0 || music.currentTrackIndex >= music.MusicTracks.Count)
        {
            return false;
        }

        var track = music.MusicTracks[music.currentTrackIndex];
        state = new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.State,
            Sequence = ++outboundSequence,
            TrackNumber = track.TrackNumber,
            TrackName = track.TrackName,
            ArtistName = track.ArtistName,
            AudioFileHash = ResolveAudioFileHash(track),
            AudioFileStem = ResolveAudioFileStem(track),
            PositionSeconds = ResolvePositionSeconds(music, positionSeconds),
            Paused = paused ?? music.audio == null || !music.audio.isPlaying
        };

        return true;
    }

    private void HandleLifecycleEvent(string source)
    {
        var lobby = ResolveSteamLobby();
        var musicController = ResolveMusicController();

        EnsureBroadcastHandlersRegistered();

        if (lobby == null || !lobby.inSteamLobby)
        {
            return;
        }

        if (musicController == null)
        {
            return;
        }

        if (IsHost())
        {
            PublishHostState();
            return;
        }

        if (InstanceFinder.IsClient)
        {
            if (!TryQueueStateFromLobbyMetadata(force: true))
            {
                RequestHostSync();
            }
        }
    }

    private static float ResolvePositionSeconds(SetMenuMusicVolume music, float? positionSeconds)
    {
        if (positionSeconds.HasValue)
        {
            return Mathf.Max(0f, positionSeconds.Value);
        }

        if (music.audio == null || music.audio.clip == null)
        {
            return 0f;
        }

        return Mathf.Max(0f, music.audio.time);
    }

    private void OnClientBroadcastReceived(ChatBroadcast.Message message)
    {
        if (!TryParseWireMessage(message, out var wireMessage))
        {
            return;
        }

        switch (wireMessage.MessageType)
        {
            case MusicSyncMessageType.State:
                if (IsHost())
                {
                    return;
                }

                hasRemoteSync = true;
                lastStateReceivedAt = Time.unscaledTime;
                nextClientRequestAt = Time.unscaledTime + RequestSyncIntervalSeconds;
                pendingState = wireMessage;
                TryApplyPendingState();
                return;
            case MusicSyncMessageType.TrackTransferStart:
                HandleTrackTransferStart(wireMessage);
                return;
            case MusicSyncMessageType.TrackTransferChunk:
                HandleTrackTransferChunk(wireMessage);
                return;
            case MusicSyncMessageType.TrackTransferFailed:
                HandleTrackTransferFailed(wireMessage);
                return;
        }
    }

    private void OnServerBroadcastReceived(NetworkConnection connection, ChatBroadcast.Message message)
    {
        if (!TryParseWireMessage(message, out var wireMessage))
        {
            return;
        }

        if (!IsHost())
        {
            return;
        }

        switch (wireMessage.MessageType)
        {
            case MusicSyncMessageType.Request:
                PublishHostStateInternal();
                return;
            case MusicSyncMessageType.RequestTrackTransfer:
                StartTrackTransferToClient(connection, wireMessage);
                return;
            case MusicSyncMessageType.RequestTrackTransferChunk:
                SendRequestedTransferChunk(connection, wireMessage);
                return;
            case MusicSyncMessageType.TrackTransferProgress:
                HandleClientTrackTransferProgress(wireMessage);
                return;
        }
    }

    private void TryApplyPendingState()
    {
        if (pendingState == null || pendingState.Sequence < lastAppliedSequence)
        {
            return;
        }

        var music = SetMenuMusicVolume.Instance;
        if (music == null || music.MusicTracks.Count == 0)
        {
            return;
        }

        var targetIndex = FindTrackIndex(music, pendingState);
        if (targetIndex < 0)
        {
            var missingTrackIdentity = GetTrackIdentity(pendingState);
            if (lastMissingTrackIdentity != missingTrackIdentity)
            {
                lastMissingTrackIdentity = missingTrackIdentity;
                Plugin.LogWarning($"[MusicSync] Missing local track {missingTrackIdentity}; no local metadata, filename, or file-hash match was found, so host-only audio cannot be played on this client.", true);
            }

            RequestTrackTransfer(pendingState);
            return;
        }

        lastMissingTrackIdentity = string.Empty;
        var state = pendingState;
        pendingState = null;
        lastAppliedSequence = state.Sequence;

        ApplyState(music, state, targetIndex);
    }

    private void ApplyState(SetMenuMusicVolume music, MusicSyncWireMessage state, int targetIndex)
    {
        isApplyingRemoteState = true;

        try
        {
            var shouldReloadTrack = music.audio == null || music.audio.clip == null || music.currentTrackIndex != targetIndex;
            if (shouldReloadTrack)
            {
                AnnounceAppliedState(state, forceOffline: true);
                BeginRemoteTrackLoad(music, targetIndex, state.PositionSeconds, state.Paused);
                return;
            }

            var targetTime = ClampPlaybackTime(music.audio.clip.length, state.PositionSeconds);
            if (Mathf.Abs(music.audio.time - targetTime) > AllowedTimeDriftSeconds)
            {
                music.audio.time = targetTime;
            }

            if (state.Paused)
            {
                if (music.audio.isPlaying)
                {
                    music.audio.Pause();
                }
            }
            else if (!music.audio.isPlaying)
            {
                music.audio.Play();
            }

            AnnounceAppliedState(state);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[MusicSync] Failed to apply host state: {ex}", true);
        }
        finally
        {
            isApplyingRemoteState = false;
        }
    }

    private static void BeginRemoteTrackLoad(SetMenuMusicVolume music, int targetIndex, float positionSeconds, bool paused)
    {
        if (LoadTrackMethod == null)
        {
            Plugin.LogError("[MusicSync] Could not resolve SetMenuMusicVolume.LoadTrack.");
            return;
        }

        var clampedPosition = positionSeconds;
        music.currentTrackIndex = targetIndex;
        music.StopAllCoroutines();

        if (LoadTrackMethod.Invoke(music, new object[] { targetIndex, clampedPosition, paused }) is IEnumerator routine)
        {
            music.StartCoroutine(routine);
        }
    }

    private static float ClampPlaybackTime(float clipLength, float positionSeconds)
    {
        return Mathf.Clamp(positionSeconds, 0f, Mathf.Max(0f, clipLength - 0.05f));
    }

    private static int FindTrackIndex(SetMenuMusicVolume music, MusicSyncWireMessage state)
    {
        var normalizedTrackName = NormalizeTrackToken(state.TrackName);
        var normalizedArtistName = NormalizeTrackToken(state.ArtistName);
        var normalizedAudioFileStem = NormalizeAudioFileStem(state.AudioFileStem);
        var looseIdentityKey = BuildLooseIdentityKey(state.TrackName, state.ArtistName);
        var byTrackIdentityAndNumber = -1;
        var byTrackIdentity = -1;
        var byAudioFileStem = -1;
        var byLooseIdentity = -1;
        var byAudioHash = -1;
        var byTrackName = -1;

        for (var i = 0; i < music.MusicTracks.Count; i++)
        {
            var localTrack = music.MusicTracks[i];
            var localTrackName = NormalizeTrackToken(localTrack.TrackName);
            var localArtistName = NormalizeTrackToken(localTrack.ArtistName);

            if (byTrackIdentityAndNumber < 0 &&
                localTrack.TrackNumber == state.TrackNumber &&
                localTrackName == normalizedTrackName &&
                localArtistName == normalizedArtistName)
            {
                byTrackIdentityAndNumber = i;
            }

            if (byTrackIdentity < 0 &&
                localTrackName == normalizedTrackName &&
                localArtistName == normalizedArtistName)
            {
                byTrackIdentity = i;
            }

            if (byAudioFileStem < 0 &&
                !string.IsNullOrWhiteSpace(normalizedAudioFileStem) &&
                ResolveAudioFileStem(localTrack) == normalizedAudioFileStem)
            {
                byAudioFileStem = i;
            }

            if (byLooseIdentity < 0 &&
                !string.IsNullOrWhiteSpace(looseIdentityKey) &&
                BuildLooseIdentityKey(localTrack.TrackName, localTrack.ArtistName) == looseIdentityKey)
            {
                byLooseIdentity = i;
            }

            if (byAudioHash < 0 &&
                !string.IsNullOrWhiteSpace(state.AudioFileHash) &&
                ResolveAudioFileHash(localTrack) == state.AudioFileHash)
            {
                byAudioHash = i;
            }

            if (byTrackName < 0 && localTrackName == normalizedTrackName)
            {
                byTrackName = i;
            }
        }

        if (byAudioHash >= 0)
        {
            return byAudioHash;
        }

        if (byAudioFileStem >= 0)
        {
            return byAudioFileStem;
        }

        if (byTrackIdentityAndNumber >= 0)
        {
            return byTrackIdentityAndNumber;
        }

        if (byTrackIdentity >= 0)
        {
            return byTrackIdentity;
        }

        if (byLooseIdentity >= 0)
        {
            return byLooseIdentity;
        }

        if (byTrackName >= 0)
        {
            return byTrackName;
        }

        return -1;
    }

    private static string NormalizeTrackToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return MultiWhitespaceRegex.Replace(value.Trim(), " ").ToLowerInvariant();
    }

    private static string BuildLooseIdentityKey(string trackName, string artistName)
    {
        var tokens = new List<string>();
        AddLooseIdentityTokens(tokens, trackName);
        AddLooseIdentityTokens(tokens, artistName);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        tokens.Sort(StringComparer.Ordinal);
        return string.Join("|", tokens);
    }

    private static void AddLooseIdentityTokens(List<string> tokens, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var token in LooseTokenRegex.Split(value.ToLowerInvariant()))
        {
            var normalizedToken = NormalizeTrackToken(token);
            if (!string.IsNullOrWhiteSpace(normalizedToken))
            {
                tokens.Add(normalizedToken);
            }
        }
    }

    private static string ResolveAudioFileHash(MusicTrack track)
    {
        var localPath = ResolveLocalAudioPath(track.AudioPath);
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return string.Empty;
        }

        if (TrackAudioHashByPath.TryGetValue(localPath, out var cachedHash))
        {
            return cachedHash;
        }

        try
        {
            using (var fileStream = File.OpenRead(localPath))
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(fileStream);
                cachedHash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
            }
        }
        catch
        {
            cachedHash = string.Empty;
        }

        TrackAudioHashByPath[localPath] = cachedHash;
        return cachedHash;
    }

    private static string ResolveAudioFileStem(MusicTrack track)
    {
        var localPath = ResolveLocalAudioPath(track.AudioPath);
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return string.Empty;
        }

        if (TrackAudioStemByPath.TryGetValue(localPath, out var cachedStem))
        {
            return cachedStem;
        }

        cachedStem = NormalizeAudioFileStem(Path.GetFileNameWithoutExtension(localPath));
        TrackAudioStemByPath[localPath] = cachedStem;
        return cachedStem;
    }

    private static string ResolveLocalAudioPath(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(audioPath, UriKind.Absolute, out var audioUri) && audioUri.IsFile)
        {
            return audioUri.LocalPath;
        }

        return audioPath;
    }

    private static string NormalizeAudioFileStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return NormalizeTrackToken(LeadingTrackNumberRegex.Replace(value, string.Empty));
    }

    private static string GetTrackIdentity(MusicSyncWireMessage state)
    {
        var trackName = string.IsNullOrWhiteSpace(state.TrackName) ? "<unknown track>" : state.TrackName.Trim();
        var artistName = string.IsNullOrWhiteSpace(state.ArtistName) ? "<unknown artist>" : state.ArtistName.Trim();
        return $"\"{trackName}\" by \"{artistName}\" (#{state.TrackNumber})";
    }

    private bool ShouldSkipDuplicatePublish(MusicSyncWireMessage state)
    {
        if (Time.unscaledTime - lastPublishedAt > DuplicatePublishWindowSeconds)
        {
            return false;
        }

        var stateFingerprint = BuildStateFingerprint(state);
        if (!string.Equals(stateFingerprint, lastPublishedStateFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return Mathf.Abs(state.PositionSeconds - lastPublishedPositionSeconds) <= DuplicatePublishPositionToleranceSeconds;
    }

    private void RememberPublishedState(MusicSyncWireMessage state)
    {
        lastPublishedAt = Time.unscaledTime;
        lastPublishedPositionSeconds = state.PositionSeconds;
        lastPublishedStateFingerprint = BuildStateFingerprint(state);
    }

    private static string BuildStateFingerprint(MusicSyncWireMessage state)
    {
        return string.Join("|", new[]
        {
            state.AudioFileHash?.Trim() ?? string.Empty,
            NormalizeAudioFileStem(state.AudioFileStem),
            NormalizeTrackToken(state.TrackName),
            NormalizeTrackToken(state.ArtistName),
            state.TrackNumber.ToString(),
            state.Paused ? "1" : "0"
        });
    }

    private void AnnounceAppliedState(MusicSyncWireMessage state, bool forceOffline = false)
    {
        var trackIdentity = GetTrackIdentity(state);
        if (trackIdentity == lastAnnouncedTrackIdentity && lastAnnouncedPaused == state.Paused)
        {
            return;
        }

        lastAnnouncedTrackIdentity = trackIdentity;
        lastAnnouncedPaused = state.Paused;

        var status = state.Paused ? "paused" : "playing";
        Plugin.LogInfo(
            $"[MusicSync] Following host track {trackIdentity} ({status}).",
            forceOffline || !state.Paused,
            state.Paused ? Plugin.LogColorWarning : Plugin.LogColorSuccess);
    }

    private bool IsFollowerClient()
    {
        return hasRemoteSync && IsSyncContextActive() && !IsHost();
    }

    private static bool IsHost()
    {
        return InstanceFinder.NetworkManager != null && InstanceFinder.NetworkManager.IsServer;
    }

    private static bool IsSyncContextActive()
    {
        var lobby = ResolveSteamLobby();
        var musicController = ResolveMusicController();
        return lobby != null &&
               lobby.inSteamLobby &&
               musicController != null &&
               (InstanceFinder.IsClient || InstanceFinder.IsServer);
    }

    private static SteamLobby ResolveSteamLobby()
    {
        return SteamLobby.Instance != null ? SteamLobby.Instance : FindFirstObjectByType<SteamLobby>();
    }

    private static SetMenuMusicVolume ResolveMusicController()
    {
        return SetMenuMusicVolume.Instance != null ? SetMenuMusicVolume.Instance : FindFirstObjectByType<SetMenuMusicVolume>();
    }

    private static string GetLocalSteamId()
    {
        try
        {
            if (TryGetOwnedClientSteamId(out var ownedClientSteamId))
            {
                return ownedClientSteamId;
            }

            var lobby = ResolveSteamLobby();
            if (lobby == null)
            {
                return string.Empty;
            }

            var localSteamUser = AccessTools.Field(typeof(SteamLobby), "localSteamUser")?.GetValue(lobby);
            if (localSteamUser == null)
            {
                return string.Empty;
            }

            var userType = localSteamUser.GetType();
            var friendId =
                AccessTools.Property(userType, "FriendId")?.GetValue(localSteamUser, null) ??
                AccessTools.Field(userType, "FriendId")?.GetValue(localSteamUser);
            return friendId?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetOwnedClientSteamId(out string steamId)
    {
        if (ClientInstance.Instance != null && ClientInstance.Instance.IsOwner && ClientInstance.Instance.PlayerSteamID != 0UL)
        {
            steamId = ClientInstance.Instance.PlayerSteamID.ToString();
            return true;
        }

        if (LobbyController.Instance != null &&
            LobbyController.Instance.LocalPlayerController != null &&
            LobbyController.Instance.LocalPlayerController.IsOwner &&
            LobbyController.Instance.LocalPlayerController.PlayerSteamID != 0UL)
        {
            steamId = LobbyController.Instance.LocalPlayerController.PlayerSteamID.ToString();
            return true;
        }

        var clientInstances = FindObjectsByType<ClientInstance>(FindObjectsSortMode.None);
        for (var i = 0; i < clientInstances.Length; i++)
        {
            var clientInstance = clientInstances[i];
            if (clientInstance != null && clientInstance.IsOwner && clientInstance.PlayerSteamID != 0UL)
            {
                steamId = clientInstance.PlayerSteamID.ToString();
                return true;
            }
        }

        steamId = string.Empty;
        return false;
    }

    private void RequestTrackTransfer(MusicSyncWireMessage state)
    {
        if (IsHost())
        {
            return;
        }

        if (!TryGetOwnedClientSteamId(out var localSteamId))
        {
            return;
        }

        var transferKey = GetTransferKey(state);
        if (string.IsNullOrWhiteSpace(localSteamId) || string.IsNullOrWhiteSpace(transferKey))
        {
            return;
        }

        if (incomingTrackTransfers.ContainsKey(transferKey))
        {
            return;
        }

        if (requestedTrackTransferKeys.Contains(transferKey))
        {
            if (requestedTrackTransferAtByKey.TryGetValue(transferKey, out var requestedAt) &&
                Time.unscaledTime - requestedAt < TrackTransferRequestRetrySeconds)
            {
                return;
            }

            ClearRequestedTrackTransfer(transferKey);
        }

        MarkTrackTransferRequested(transferKey);
        BroadcastFromClient(new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.RequestTrackTransfer,
            SourceSteamId = localSteamId,
            TrackNumber = state.TrackNumber,
            TrackName = state.TrackName,
            ArtistName = state.ArtistName,
            AudioFileHash = state.AudioFileHash,
            AudioFileStem = state.AudioFileStem
        });

        Plugin.LogInfo($"[MusicSync] Requesting host file transfer for {GetTrackIdentity(state)}.", true, Plugin.LogColorAccent);
    }

    private void StartTrackTransferToClient(NetworkConnection connection, MusicSyncWireMessage request)
    {
        var targetSteamId = request.SourceSteamId;
        var transferKey = GetTransferKey(request);
        if (string.IsNullOrWhiteSpace(targetSteamId) || string.IsNullOrWhiteSpace(transferKey))
        {
            return;
        }

        connection = ResolveTargetConnection(connection, targetSteamId, $"starting host track transfer for {GetTrackIdentity(request)}");
        if (connection == null)
        {
            return;
        }

        var activeTransferKey = $"{targetSteamId}|{transferKey}";
        if (!activeHostTransferKeys.Add(activeTransferKey))
        {
            return;
        }

        if (!TryResolveTrackForTransfer(request, out var track, out var localPath))
        {
            SendTrackTransferFailure(connection, targetSteamId, request, "Host track was not found.");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        if (!File.Exists(localPath))
        {
            SendTrackTransferFailure(connection, targetSteamId, request, "Host audio file is missing on disk.");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        var fileSize = new FileInfo(localPath).Length;
        if (fileSize <= 0L)
        {
            SendTrackTransferFailure(connection, targetSteamId, request, "Host audio file is empty.");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        if (fileSize > MaxTransferSizeBytes)
        {
            SendTrackTransferFailure(connection, targetSteamId, request, $"Host audio file is too large to quick-transfer ({fileSize / (1024f * 1024f):0.0} MB).");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(localPath);
        }
        catch (Exception ex)
        {
            SendTrackTransferFailure(connection, targetSteamId, new MusicSyncWireMessage
            {
                TrackNumber = track.TrackNumber,
                TrackName = track.TrackName,
                ArtistName = track.ArtistName,
                AudioFileHash = ResolveAudioFileHash(track),
                AudioFileStem = ResolveAudioFileStem(track)
            }, $"Host failed to read audio file: {ex.Message}");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        var transfer = new OutgoingTrackTransfer
        {
            TransferKey = activeTransferKey,
            ClientTransferKey = transferKey,
            Connection = connection,
            TargetSteamId = targetSteamId,
            TrackNumber = track.TrackNumber,
            TrackName = track.TrackName,
            ArtistName = track.ArtistName,
            AudioFileHash = ResolveAudioFileHash(track),
            AudioFileStem = ResolveAudioFileStem(track),
            AudioFileExtension = Path.GetExtension(localPath).ToLowerInvariant(),
            FileBytes = fileBytes,
            ChunkCount = Mathf.CeilToInt(fileBytes.Length / (float)TransferChunkSizeBytes),
            NextClientProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds,
            NextProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds
        };
        outgoingTrackTransfers[activeTransferKey] = transfer;
        var localSteamId = GetLocalSteamId();

        BroadcastToClient(connection, new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.TrackTransferStart,
            TransferToken = transfer.ClientTransferKey,
            TargetSteamId = targetSteamId,
            SourceSteamId = localSteamId,
            TrackNumber = transfer.TrackNumber,
            TrackName = transfer.TrackName,
            ArtistName = transfer.ArtistName,
            AudioFileHash = transfer.AudioFileHash,
            AudioFileStem = transfer.AudioFileStem,
            AudioFileExtension = transfer.AudioFileExtension,
            FileSizeBytes = fileBytes.Length,
            ChunkCount = transfer.ChunkCount
        });

        Plugin.LogInfo($"[MusicSync] Streaming host track {GetTrackIdentity(transfer.ToWireMessage())} to client {targetSteamId}.", true, Plugin.LogColorAccent);
        StartCoroutine(StreamTrackTransferToClient(activeTransferKey));
    }

    private void SendRequestedTransferChunk(NetworkConnection connection, MusicSyncWireMessage request)
    {
        var targetSteamId = request.SourceSteamId;
        var transferKey = GetTransferKey(request);
        if (string.IsNullOrWhiteSpace(targetSteamId) || string.IsNullOrWhiteSpace(transferKey))
        {
            return;
        }

        connection = ResolveTargetConnection(connection, targetSteamId, $"sending chunk {request.ChunkIndex} for {GetTrackIdentity(request)}");
        if (connection == null)
        {
            return;
        }

        var activeTransferKey = $"{targetSteamId}|{transferKey}";
        if (!outgoingTrackTransfers.TryGetValue(activeTransferKey, out var transfer))
        {
            SendTrackTransferFailure(connection, targetSteamId, request, "Host transfer session is no longer available.");
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        var chunkIndex = request.ChunkIndex;
        if (chunkIndex < 0 || chunkIndex >= transfer.ChunkCount)
        {
            SendTrackTransferFailure(connection, targetSteamId, request, "Requested transfer chunk is out of range.");
            outgoingTrackTransfers.Remove(activeTransferKey);
            activeHostTransferKeys.Remove(activeTransferKey);
            return;
        }

        var offset = chunkIndex * TransferChunkSizeBytes;
        var count = Mathf.Min(TransferChunkSizeBytes, transfer.FileBytes.Length - offset);
        BroadcastToClient(connection, new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.TrackTransferChunk,
            TargetSteamId = targetSteamId,
            SourceSteamId = GetLocalSteamId(),
            TrackNumber = transfer.TrackNumber,
            TrackName = transfer.TrackName,
            ArtistName = transfer.ArtistName,
            AudioFileHash = transfer.AudioFileHash,
            AudioFileStem = transfer.AudioFileStem,
            AudioFileExtension = transfer.AudioFileExtension,
            FileSizeBytes = transfer.FileBytes.Length,
            ChunkIndex = chunkIndex,
            ChunkCount = transfer.ChunkCount,
            ChunkDataBase64 = Convert.ToBase64String(transfer.FileBytes, offset, count)
        });

        if (chunkIndex + 1 >= transfer.ChunkCount)
        {
            Plugin.LogInfo($"[MusicSync] Completed host track transfer for {GetTrackIdentity(transfer.ToWireMessage())} to client {targetSteamId}.", true, Plugin.LogColorSuccess);
            outgoingTrackTransfers.Remove(activeTransferKey);
            activeHostTransferKeys.Remove(activeTransferKey);
        }
    }

    private static bool TryResolveTrackForTransfer(MusicSyncWireMessage request, out MusicTrack track, out string localPath)
    {
        track = default;
        localPath = string.Empty;

        var music = ResolveMusicController();
        if (music == null || music.MusicTracks.Count == 0)
        {
            return false;
        }

        var targetIndex = FindTrackIndex(music, request);
        if (targetIndex < 0)
        {
            return false;
        }

        track = music.MusicTracks[targetIndex];
        localPath = ResolveLocalAudioPath(track.AudioPath);
        return !string.IsNullOrWhiteSpace(localPath);
    }

    private void HandleTrackTransferStart(MusicSyncWireMessage wireMessage)
    {
        var transferKey = GetTransferKey(wireMessage);
        if (string.IsNullOrWhiteSpace(transferKey))
        {
            return;
        }

        CleanupIncomingTransfer(transferKey);
        Directory.CreateDirectory(GetMusicFolderPath());
        var tempPath = Path.Combine(GetMusicFolderPath(), $".sms_{SanitizeTransferKey(transferKey)}.part");

        try
        {
            incomingTrackTransfers[transferKey] = new IncomingTrackTransfer
            {
                TransferKey = transferKey,
                SourceSteamId = wireMessage.SourceSteamId,
                RequesterSteamId = string.IsNullOrWhiteSpace(wireMessage.TargetSteamId) ? GetLocalSteamId() : wireMessage.TargetSteamId,
                AudioFileHash = wireMessage.AudioFileHash,
                AudioFileStem = wireMessage.AudioFileStem,
                AudioFileExtension = string.IsNullOrWhiteSpace(wireMessage.AudioFileExtension) ? ".mp3" : wireMessage.AudioFileExtension,
                ChunkCount = wireMessage.ChunkCount,
                FileSizeBytes = wireMessage.FileSizeBytes,
                TempPath = tempPath,
                TrackName = wireMessage.TrackName,
                ArtistName = wireMessage.ArtistName,
                TrackNumber = wireMessage.TrackNumber,
                NextProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds,
                Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            };

            Plugin.LogInfo($"[MusicSync] Receiving host track {GetTrackIdentity(wireMessage)} ({wireMessage.FileSizeBytes / 1024f:0.0} KB).", true, Plugin.LogColorAccent);
        }
        catch (Exception ex)
        {
            ClearRequestedTrackTransfer(transferKey);
            Plugin.LogWarning($"[MusicSync] Failed to prepare incoming host track transfer: {ex.Message}", true);
        }
    }

    private void HandleTrackTransferChunk(MusicSyncWireMessage wireMessage)
    {
        var transferKey = GetTransferKey(wireMessage);
        if (string.IsNullOrWhiteSpace(transferKey) || !incomingTrackTransfers.TryGetValue(transferKey, out var transfer))
        {
            return;
        }

        if (wireMessage.ChunkIndex != transfer.NextChunkIndex)
        {
            Plugin.LogWarning($"[MusicSync] Host track transfer chunk arrived out of order for {GetTrackIdentity(new MusicSyncWireMessage { TrackNumber = transfer.TrackNumber, TrackName = transfer.TrackName, ArtistName = transfer.ArtistName })}. Restarting request on the next sync.", true);
            ClearRequestedTrackTransfer(transferKey);
            CleanupIncomingTransfer(transferKey);
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(wireMessage.ChunkDataBase64 ?? string.Empty);
            transfer.Stream.Write(bytes, 0, bytes.Length);
            transfer.ReceivedBytes += bytes.Length;
            transfer.NextChunkIndex++;
            MaybeSendIncomingTransferAck(transfer, transfer.NextChunkIndex >= transfer.ChunkCount || transfer.NextChunkIndex - transfer.LastAcknowledgedChunkIndex >= TransferAckEveryChunks);
            MaybeLogIncomingTransferProgress(transfer);

            if (transfer.NextChunkIndex >= transfer.ChunkCount || transfer.ReceivedBytes >= transfer.FileSizeBytes)
            {
                FinalizeIncomingTransfer(transfer);
            }
        }
        catch (Exception ex)
        {
            ClearRequestedTrackTransfer(transferKey);
            CleanupIncomingTransfer(transferKey);
            Plugin.LogWarning($"[MusicSync] Failed while receiving host track data: {ex.Message}", true);
        }
    }

    private void HandleTrackTransferFailed(MusicSyncWireMessage wireMessage)
    {
        var transferKey = GetTransferKey(wireMessage);
        if (!string.IsNullOrWhiteSpace(transferKey))
        {
            ClearRequestedTrackTransfer(transferKey);
            CleanupIncomingTransfer(transferKey);
        }

        var reason = string.IsNullOrWhiteSpace(wireMessage.ErrorMessage) ? "Host rejected the transfer request." : wireMessage.ErrorMessage.Trim();
        Plugin.LogWarning($"[MusicSync] Host could not transfer {GetTrackIdentity(wireMessage)}: {reason}", true);
    }

    private void FinalizeIncomingTransfer(IncomingTrackTransfer transfer)
    {
        transfer.Stream.Dispose();
        transfer.Stream = null;

        try
        {
            transfer.LocalTrackNumber = AllocateTransferredTrackNumber(ResolveMusicController());
            var finalPath = BuildTransferredTrackPath(transfer);
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(transfer.TempPath, finalPath);
            RegisterTransferredTrack(finalPath, transfer);
            incomingTrackTransfers.Remove(transfer.TransferKey);
            ClearRequestedTrackTransfer(transfer.TransferKey);
            lastMissingTrackIdentity = string.Empty;
            Plugin.LogInfo($"[MusicSync] Imported host track {GetTrackIdentity(new MusicSyncWireMessage { TrackNumber = transfer.TrackNumber, TrackName = transfer.TrackName, ArtistName = transfer.ArtistName })}.", true, Plugin.LogColorSuccess);
            TryApplyPendingState();
        }
        catch (Exception ex)
        {
            ClearRequestedTrackTransfer(transfer.TransferKey);
            CleanupIncomingTransfer(transfer.TransferKey);
            Plugin.LogWarning($"[MusicSync] Failed to finalize host track transfer: {ex.Message}", true);
        }
    }

    private static void RegisterTransferredTrack(string finalPath, IncomingTrackTransfer transfer)
    {
        var music = ResolveMusicController();
        if (music == null)
        {
            return;
        }

        var existingIndex = FindTrackIndex(music, new MusicSyncWireMessage
        {
            TrackNumber = transfer.TrackNumber,
            TrackName = transfer.TrackName,
            ArtistName = transfer.ArtistName,
            AudioFileHash = transfer.AudioFileHash,
            AudioFileStem = transfer.AudioFileStem
        });

        if (existingIndex >= 0)
        {
            return;
        }

        var extension = string.IsNullOrWhiteSpace(transfer.AudioFileExtension) ? ".mp3" : transfer.AudioFileExtension.ToLowerInvariant();
        if (!SetMenuMusicVolume.ExtensionToAudioType.TryGetValue(extension, out var audioType))
        {
            throw new InvalidOperationException($"Unsupported audio extension '{extension}'.");
        }

        var localTrackNumber = transfer.LocalTrackNumber > 0 ? transfer.LocalTrackNumber : AllocateTransferredTrackNumber(music);
        music.MusicTracks.Add(new MusicTrack(new Uri(finalPath).AbsoluteUri, transfer.TrackName, transfer.ArtistName, localTrackNumber, audioType));

        if (TrackNumberToIndexField?.GetValue(music) is Dictionary<int, int> trackNumberToIndex)
        {
            trackNumberToIndex[localTrackNumber] = music.MusicTracks.Count - 1;
        }

        TrackAudioHashByPath[finalPath] = transfer.AudioFileHash ?? string.Empty;
        TrackAudioStemByPath[finalPath] = NormalizeAudioFileStem(Path.GetFileNameWithoutExtension(finalPath));
    }

    private static int AllocateTransferredTrackNumber(SetMenuMusicVolume music)
    {
        if (music == null)
        {
            return TransferTrackNumberStart;
        }

        var usedTrackNumbers = new HashSet<int>();
        for (var i = 0; i < music.MusicTracks.Count; i++)
        {
            usedTrackNumbers.Add(music.MusicTracks[i].TrackNumber);
        }

        var musicFolder = GetMusicFolderPath();
        if (Directory.Exists(musicFolder))
        {
            foreach (var path in Directory.GetFiles(musicFolder))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var separatorIndex = fileName.IndexOf(" - ", StringComparison.Ordinal);
                if (separatorIndex > 0 && int.TryParse(fileName[..separatorIndex].Trim(), out var trackNumber))
                {
                    usedTrackNumbers.Add(trackNumber);
                }
            }
        }

        var candidate = TransferTrackNumberStart;
        while (usedTrackNumbers.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static string BuildTransferredTrackPath(IncomingTrackTransfer transfer)
    {
        Directory.CreateDirectory(GetMusicFolderPath());
        var trackNumber = transfer.LocalTrackNumber > 0 ? transfer.LocalTrackNumber : TransferTrackNumberStart;
        var safeTrackName = SanitizeTrackFilePart(transfer.TrackName, "Unknown Track");
        var safeArtistName = SanitizeTrackFilePart(transfer.ArtistName, "Unknown Artist");
        var extension = string.IsNullOrWhiteSpace(transfer.AudioFileExtension) ? ".mp3" : transfer.AudioFileExtension.ToLowerInvariant();
        return Path.Combine(GetMusicFolderPath(), $"{trackNumber} - {safeTrackName} - {safeArtistName}{extension}");
    }

    private static string SanitizeTrackFilePart(string value, string fallback)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        sanitized = sanitized.Replace("-", " ");
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, ' ');
        }

        sanitized = MultiWhitespaceRegex.Replace(sanitized, " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string SanitizeTransferKey(string transferKey)
    {
        return LooseTokenRegex.Replace(transferKey, string.Empty);
    }

    private static string GetTransferKey(MusicSyncWireMessage state)
    {
        if (!string.IsNullOrWhiteSpace(state.TransferToken))
        {
            return state.TransferToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(state.AudioFileHash))
        {
            return state.AudioFileHash.Trim();
        }

        var audioFileStem = NormalizeAudioFileStem(state.AudioFileStem);
        if (!string.IsNullOrWhiteSpace(audioFileStem))
        {
            return audioFileStem;
        }

        return BuildLooseIdentityKey(state.TrackName, state.ArtistName);
    }

    private static string GetMusicFolderPath()
    {
        return Path.Combine(Application.streamingAssetsPath, "Music");
    }

    private bool HasActiveOutgoingTransfers()
    {
        return outgoingTrackTransfers.Count > 0;
    }

    private IEnumerator StreamTrackTransferToClient(string activeTransferKey)
    {
        yield return null;

        OutgoingTrackTransfer transfer;
        var chunksSentThisFrame = 0;
        while (true)
        {
            if (!outgoingTrackTransfers.TryGetValue(activeTransferKey, out transfer))
            {
                yield break;
            }

            if (transfer.NextChunkIndexToSend >= transfer.ChunkCount)
            {
                if (transfer.AcknowledgedChunkCount >= transfer.ChunkCount)
                {
                    break;
                }

                chunksSentThisFrame = 0;
                yield return null;
                continue;
            }

            var outstandingChunkCount = transfer.NextChunkIndexToSend - transfer.AcknowledgedChunkCount;
            if (outstandingChunkCount >= TransferWindowChunks)
            {
                chunksSentThisFrame = 0;
                yield return null;
                continue;
            }

            var connection = transfer.Connection;
            if (connection == null || !connection.IsActive)
            {
                connection = ResolveTargetConnection(null, transfer.TargetSteamId, $"streaming chunk {transfer.NextChunkIndexToSend} for {GetTrackIdentity(transfer.ToWireMessage())}");
                if (connection != null)
                {
                    transfer.Connection = connection;
                }
            }

            if (connection == null)
            {
                outgoingTrackTransfers.Remove(activeTransferKey);
                activeHostTransferKeys.Remove(activeTransferKey);
                yield break;
            }

            var chunkIndex = transfer.NextChunkIndexToSend;
            var offset = chunkIndex * TransferChunkSizeBytes;
            var count = Mathf.Min(TransferChunkSizeBytes, transfer.FileBytes.Length - offset);
            BroadcastToClient(connection, new MusicSyncWireMessage
            {
                MessageType = MusicSyncMessageType.TrackTransferChunk,
                TransferToken = transfer.ClientTransferKey,
                TargetSteamId = transfer.TargetSteamId,
                SourceSteamId = GetLocalSteamId(),
                ChunkIndex = chunkIndex,
                ChunkCount = transfer.ChunkCount,
                ChunkDataBase64 = Convert.ToBase64String(transfer.FileBytes, offset, count)
            });
            transfer.SentBytes += count;
            transfer.NextChunkIndexToSend++;
            MaybeLogOutgoingTransferProgress(transfer);

            chunksSentThisFrame++;
            if (chunksSentThisFrame >= TransferChunksPerFrame)
            {
                chunksSentThisFrame = 0;
                yield return null;
            }
        }

        if (outgoingTrackTransfers.TryGetValue(activeTransferKey, out transfer))
        {
            Plugin.LogInfo($"[MusicSync] Completed host track transfer for {GetTrackIdentity(transfer.ToWireMessage())} to client {transfer.TargetSteamId}.", true, Plugin.LogColorSuccess);
        }

        outgoingTrackTransfers.Remove(activeTransferKey);
        activeHostTransferKeys.Remove(activeTransferKey);
        nextHostSyncAt = Time.unscaledTime;
    }

    private static bool TryResolveConnectionForSteamId(string steamId, out NetworkConnection connection)
    {
        connection = null;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return false;
        }

        var clientInstances = FindObjectsByType<ClientInstance>(FindObjectsSortMode.None);
        for (var i = 0; i < clientInstances.Length; i++)
        {
            var clientInstance = clientInstances[i];
            if (clientInstance == null ||
                clientInstance.PlayerSteamID == 0UL ||
                !string.Equals(clientInstance.PlayerSteamID.ToString(), steamId, StringComparison.Ordinal))
            {
                continue;
            }

            var networkObject = clientInstance.GetComponent<NetworkObject>();
            if (networkObject?.Owner != null && networkObject.Owner.IsActive)
            {
                connection = networkObject.Owner;
                return true;
            }
        }

        return false;
    }

    private NetworkConnection ResolveTargetConnection(NetworkConnection fallbackConnection, string targetSteamId, string context)
    {
        if (fallbackConnection != null && fallbackConnection.IsActive)
        {
            return fallbackConnection;
        }

        if (TryResolveConnectionForSteamId(targetSteamId, out var resolvedConnection))
        {
            return resolvedConnection;
        }

        Plugin.LogWarning($"[MusicSync] Could not resolve a live connection for client {targetSteamId} while {context}.", true);
        return null;
    }

    private void MarkTrackTransferRequested(string transferKey)
    {
        requestedTrackTransferKeys.Add(transferKey);
        requestedTrackTransferAtByKey[transferKey] = Time.unscaledTime;
    }

    private void ClearRequestedTrackTransfer(string transferKey)
    {
        if (string.IsNullOrWhiteSpace(transferKey))
        {
            return;
        }

        requestedTrackTransferKeys.Remove(transferKey);
        requestedTrackTransferAtByKey.Remove(transferKey);
    }

    private void RequestNextTrackTransferChunk(IncomingTrackTransfer transfer, int chunkIndex)
    {
        if (transfer == null || string.IsNullOrWhiteSpace(transfer.SourceSteamId))
        {
            return;
        }

        var requesterSteamId = string.IsNullOrWhiteSpace(transfer.RequesterSteamId)
            ? GetLocalSteamId()
            : transfer.RequesterSteamId;
        if (string.IsNullOrWhiteSpace(requesterSteamId))
        {
            Plugin.LogWarning($"[MusicSync] Could not request host track chunk {chunkIndex} for {GetTrackIdentity(new MusicSyncWireMessage { TrackNumber = transfer.TrackNumber, TrackName = transfer.TrackName, ArtistName = transfer.ArtistName })} because the local Steam ID is not available yet.", true);
            return;
        }

        BroadcastFromClient(new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.RequestTrackTransferChunk,
            SourceSteamId = requesterSteamId,
            TargetSteamId = transfer.SourceSteamId,
            TrackNumber = transfer.TrackNumber,
            TrackName = transfer.TrackName,
            ArtistName = transfer.ArtistName,
            AudioFileHash = transfer.AudioFileHash,
            AudioFileStem = transfer.AudioFileStem,
            ChunkIndex = chunkIndex
        });
    }

    private void MaybeLogOutgoingTransferProgress(OutgoingTrackTransfer transfer)
    {
        if (transfer == null || transfer.FileBytes == null || transfer.FileBytes.Length <= 0)
        {
            return;
        }

        if (transfer.SentBytes < transfer.FileBytes.Length && Time.unscaledTime < transfer.NextProgressLogAt)
        {
            return;
        }

        transfer.NextProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds;
        var progress = Mathf.Clamp01(transfer.SentBytes / (float)transfer.FileBytes.Length);
        Plugin.LogInfo(
            $"[MusicSync] Uploading {GetTrackIdentity(transfer.ToWireMessage())} {BuildTransferProgressBar(progress)} {progress * 100f:0}% ({FormatBytes(transfer.SentBytes)} / {FormatBytes(transfer.FileBytes.Length)}).",
            true,
            Plugin.LogColorAccent);
    }

    private void MaybeLogIncomingTransferProgress(IncomingTrackTransfer transfer)
    {
        if (transfer == null || transfer.FileSizeBytes <= 0)
        {
            return;
        }

        if (transfer.ReceivedBytes < transfer.FileSizeBytes && Time.unscaledTime < transfer.NextProgressLogAt)
        {
            return;
        }

        transfer.NextProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds;
        var progress = Mathf.Clamp01(transfer.ReceivedBytes / (float)transfer.FileSizeBytes);
        Plugin.LogInfo(
            $"[MusicSync] Downloading {GetTrackIdentity(new MusicSyncWireMessage { TrackNumber = transfer.TrackNumber, TrackName = transfer.TrackName, ArtistName = transfer.ArtistName })} {BuildTransferProgressBar(progress)} {progress * 100f:0}% ({FormatBytes(transfer.ReceivedBytes)} / {FormatBytes(transfer.FileSizeBytes)}).",
            true,
            Plugin.LogColorAccent);
    }

    private void HandleClientTrackTransferProgress(MusicSyncWireMessage wireMessage)
    {
        if (string.IsNullOrWhiteSpace(wireMessage.SourceSteamId) || string.IsNullOrWhiteSpace(wireMessage.TransferToken))
        {
            return;
        }

        var activeTransferKey = $"{wireMessage.SourceSteamId}|{wireMessage.TransferToken}";
        if (!outgoingTrackTransfers.TryGetValue(activeTransferKey, out var transfer) || transfer.FileBytes == null || transfer.FileBytes.Length <= 0)
        {
            return;
        }

        var receivedBytes = Mathf.Clamp(wireMessage.TransferredBytes, 0, transfer.FileBytes.Length);
        var acknowledgedChunkCount = Mathf.Clamp(
            Mathf.CeilToInt(receivedBytes / (float)TransferChunkSizeBytes),
            transfer.AcknowledgedChunkCount,
            transfer.ChunkCount);
        transfer.AcknowledgedChunkCount = acknowledgedChunkCount;

        if (receivedBytes < transfer.FileBytes.Length && Time.unscaledTime < transfer.NextClientProgressLogAt)
        {
            return;
        }

        transfer.NextClientProgressLogAt = Time.unscaledTime + TransferProgressLogIntervalSeconds;
        var progress = Mathf.Clamp01(receivedBytes / (float)transfer.FileBytes.Length);
        Plugin.LogInfo(
            $"[MusicSync] Remote client {wireMessage.SourceSteamId} downloading {GetTrackIdentity(transfer.ToWireMessage())} {BuildTransferProgressBar(progress)} {progress * 100f:0}% ({FormatBytes(receivedBytes)} / {FormatBytes(transfer.FileBytes.Length)}).",
            true,
            Plugin.LogColorAccent);
    }

    private void MaybeSendIncomingTransferAck(IncomingTrackTransfer transfer, bool force)
    {
        if (transfer == null || transfer.FileSizeBytes <= 0)
        {
            return;
        }

        if (!force && transfer.NextChunkIndex - transfer.LastAcknowledgedChunkIndex < TransferAckEveryChunks)
        {
            return;
        }

        transfer.LastAcknowledgedChunkIndex = transfer.NextChunkIndex;
        BroadcastFromClient(new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.TrackTransferProgress,
            TransferToken = transfer.TransferKey,
            SourceSteamId = transfer.RequesterSteamId,
            TargetSteamId = transfer.SourceSteamId,
            TransferredBytes = transfer.ReceivedBytes,
            FileSizeBytes = transfer.FileSizeBytes
        });
    }

    private static string BuildTransferProgressBar(float progress)
    {
        var clampedProgress = Mathf.Clamp01(progress);
        var filledLength = Mathf.RoundToInt(clampedProgress * TransferProgressBarWidth);
        return $"[{new string('#', filledLength)}{new string('-', Mathf.Max(0, TransferProgressBarWidth - filledLength))}]";
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024f * 1024f):0.0} MB";
        }

        return $"{bytes / 1024f:0.0} KB";
    }

    private void CleanupIncomingTransfers()
    {
        foreach (var transferKey in new List<string>(incomingTrackTransfers.Keys))
        {
            CleanupIncomingTransfer(transferKey);
        }
    }

    private void CleanupOutgoingTransfers()
    {
        outgoingTrackTransfers.Clear();
        activeHostTransferKeys.Clear();
    }

    private void CleanupIncomingTransfer(string transferKey)
    {
        if (!incomingTrackTransfers.TryGetValue(transferKey, out var transfer))
        {
            return;
        }

        incomingTrackTransfers.Remove(transferKey);

        try
        {
            transfer.Stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(transfer.TempPath) && File.Exists(transfer.TempPath))
            {
                File.Delete(transfer.TempPath);
            }
        }
        catch
        {
        }
    }

    private void SendTrackTransferFailure(NetworkConnection connection, string targetSteamId, MusicSyncWireMessage request, string reason)
    {
        BroadcastToClient(connection, new MusicSyncWireMessage
        {
            MessageType = MusicSyncMessageType.TrackTransferFailed,
            TargetSteamId = targetSteamId,
            SourceSteamId = GetLocalSteamId(),
            TrackNumber = request.TrackNumber,
            TrackName = request.TrackName,
            ArtistName = request.ArtistName,
            AudioFileHash = request.AudioFileHash,
            AudioFileStem = request.AudioFileStem,
            ErrorMessage = reason
        });
    }

    private void EnsureBroadcastHandlersRegistered()
    {
        if (broadcastHandlersRegistered || InstanceFinder.ClientManager == null || InstanceFinder.ServerManager == null)
        {
            return;
        }

        InstanceFinder.ClientManager.RegisterBroadcast<ChatBroadcast.Message>(OnClientBroadcastReceived);
        InstanceFinder.ServerManager.RegisterBroadcast<ChatBroadcast.Message>(OnServerBroadcastReceived);
        broadcastHandlersRegistered = true;
    }

    private void TryUnregisterBroadcastHandlers()
    {
        if (!broadcastHandlersRegistered || InstanceFinder.ClientManager == null || InstanceFinder.ServerManager == null)
        {
            return;
        }

        InstanceFinder.ClientManager.UnregisterBroadcast<ChatBroadcast.Message>(OnClientBroadcastReceived);
        InstanceFinder.ServerManager.UnregisterBroadcast<ChatBroadcast.Message>(OnServerBroadcastReceived);
        broadcastHandlersRegistered = false;
    }

    private void ResetClientSyncState()
    {
        hasRemoteSync = false;
        pendingState = null;
        lastAppliedSequence = 0;
        lastMissingTrackIdentity = string.Empty;
        lastAnnouncedTrackIdentity = string.Empty;
        lastLobbyStatePayload = string.Empty;
        lastAnnouncedPaused = null;
        requestedTrackTransferKeys.Clear();
        requestedTrackTransferAtByKey.Clear();
        CleanupIncomingTransfers();
    }

    private static void BroadcastToClients(MusicSyncWireMessage state)
    {
        if (!InstanceFinder.IsServer || InstanceFinder.ServerManager == null)
        {
            return;
        }

        InstanceFinder.ServerManager.Broadcast(new ChatBroadcast.Message
        {
            username = BroadcastUserName,
            message = JsonConvert.SerializeObject(state)
        });
    }

    private static void BroadcastToClient(NetworkConnection connection, MusicSyncWireMessage state)
    {
        if (!InstanceFinder.IsServer || InstanceFinder.ServerManager == null || connection == null)
        {
            return;
        }

        InstanceFinder.ServerManager.Broadcast(connection, new ChatBroadcast.Message
        {
            username = BroadcastUserName,
            message = JsonConvert.SerializeObject(state)
        }, false, Channel.Reliable);
    }

    private static bool TryGetCurrentLobbyId(out CSteamID lobbyId)
    {
        var lobby = ResolveSteamLobby();
        if (lobby == null || !lobby.inSteamLobby || lobby.CurrentLobbyID == 0)
        {
            lobbyId = default;
            return false;
        }

        lobbyId = new CSteamID(lobby.CurrentLobbyID);
        return true;
    }

    private void TryWriteLobbyMetadata(MusicSyncWireMessage state)
    {
        if (!TryGetCurrentLobbyId(out var lobbyId) ||
            !TryResolveLobbyMetadataMethods(out var setLobbyDataMethod, out _, out _))
        {
            return;
        }

        try
        {
            var payload = JsonConvert.SerializeObject(state);
            if (setLobbyDataMethod.Invoke(null, new object[] { lobbyId, LobbyStateDataKey, payload }) is bool writeSucceeded && !writeSucceeded)
            {
                Plugin.LogWarning("[MusicSync] Steam rejected lobby metadata sync state write.", true);
                return;
            }

            lastLobbyStatePayload = payload;
        }
        catch (Exception ex)
        {
            Plugin.LogWarning($"[MusicSync] Failed to write lobby metadata sync state: {ex.Message}", true);
        }
    }

    private bool TryQueueStateFromLobbyMetadata(bool force = false)
    {
        if (IsHost() ||
            !TryGetCurrentLobbyId(out var lobbyId) ||
            !TryResolveLobbyMetadataMethods(out _, out var getLobbyDataMethod, out var requestLobbyDataMethod))
        {
            return false;
        }

        try
        {
            requestLobbyDataMethod?.Invoke(null, new object[] { lobbyId });
            var payload = getLobbyDataMethod.Invoke(null, new object[] { lobbyId, LobbyStateDataKey }) as string;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            if (!force && payload == lastLobbyStatePayload)
            {
                return false;
            }

            lastLobbyStatePayload = payload;
            var state = JsonConvert.DeserializeObject<MusicSyncWireMessage>(payload);
            if (state == null || state.MessageType != MusicSyncMessageType.State)
            {
                return false;
            }

            if (state.Sequence <= lastAppliedSequence || (pendingState != null && state.Sequence <= pendingState.Sequence))
            {
                return false;
            }

            hasRemoteSync = true;
            lastStateReceivedAt = Time.unscaledTime;
            pendingState = state;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogWarning($"[MusicSync] Failed to read lobby metadata sync state: {ex.Message}", true);
            return false;
        }
    }

    private static bool TryResolveLobbyMetadataMethods(
        out MethodInfo setLobbyDataMethod,
        out MethodInfo getLobbyDataMethod,
        out MethodInfo requestLobbyDataMethod)
    {
        if (steamMatchmakingSetLobbyDataMethod == null || steamMatchmakingGetLobbyDataMethod == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var steamMatchmakingType =
                    assembly.GetType("Steamworks.SteamMatchmaking") ??
                    assembly.GetType("SteamMatchmaking");
                if (steamMatchmakingType == null)
                {
                    continue;
                }

                steamMatchmakingSetLobbyDataMethod ??=
                    AccessTools.Method(steamMatchmakingType, "SetLobbyData", new[] { typeof(CSteamID), typeof(string), typeof(string) });
                steamMatchmakingGetLobbyDataMethod ??=
                    AccessTools.Method(steamMatchmakingType, "GetLobbyData", new[] { typeof(CSteamID), typeof(string) });
                steamMatchmakingRequestLobbyDataMethod ??=
                    AccessTools.Method(steamMatchmakingType, "RequestLobbyData", new[] { typeof(CSteamID) });

                if (steamMatchmakingSetLobbyDataMethod != null && steamMatchmakingGetLobbyDataMethod != null)
                {
                    break;
                }
            }
        }

        setLobbyDataMethod = steamMatchmakingSetLobbyDataMethod;
        getLobbyDataMethod = steamMatchmakingGetLobbyDataMethod;
        requestLobbyDataMethod = steamMatchmakingRequestLobbyDataMethod;

        if (setLobbyDataMethod != null && getLobbyDataMethod != null)
        {
            return true;
        }

        if (!steamMatchmakingLoggedUnavailable)
        {
            steamMatchmakingLoggedUnavailable = true;
            Plugin.LogWarning("[MusicSync] SteamMatchmaking runtime type was not found; lobby metadata sync is unavailable.", true);
        }

        return false;
    }

    private static void BroadcastFromClient(MusicSyncWireMessage state)
    {
        if (!InstanceFinder.IsClient || InstanceFinder.ClientManager == null)
        {
            return;
        }

        InstanceFinder.ClientManager.Broadcast(new ChatBroadcast.Message
        {
            username = BroadcastUserName,
            message = JsonConvert.SerializeObject(state)
        });
    }

    private static bool TryParseWireMessage(ChatBroadcast.Message message, out MusicSyncWireMessage wireMessage)
    {
        wireMessage = null;
        if (message.username != BroadcastUserName || string.IsNullOrWhiteSpace(message.message))
        {
            return false;
        }

        try
        {
            wireMessage = JsonConvert.DeserializeObject<MusicSyncWireMessage>(message.message);
            return wireMessage != null && !string.IsNullOrWhiteSpace(wireMessage.MessageType);
        }
        catch (Exception ex)
        {
            Plugin.LogWarning($"[MusicSync] Failed to parse a sync packet: {ex.Message}");
            return false;
        }
    }
}

internal static class MusicSyncMessageType
{
    internal const string Request = "request";
    internal const string RequestTrackTransfer = "request_track_transfer";
    internal const string RequestTrackTransferChunk = "request_track_transfer_chunk";
    internal const string State = "state";
    internal const string TrackTransferChunk = "track_transfer_chunk";
    internal const string TrackTransferFailed = "track_transfer_failed";
    internal const string TrackTransferProgress = "track_transfer_progress";
    internal const string TrackTransferStart = "track_transfer_start";
}

internal sealed class MusicSyncWireMessage
{
    public string MessageType { get; set; }

    public string TransferToken { get; set; }

    public long Sequence { get; set; }

    public int TrackNumber { get; set; }

    public string TrackName { get; set; }

    public string ArtistName { get; set; }

    public string AudioFileHash { get; set; }

    public string AudioFileStem { get; set; }

    public string AudioFileExtension { get; set; }

    public string TargetSteamId { get; set; }

    public string SourceSteamId { get; set; }

    public string ErrorMessage { get; set; }

    public int ChunkIndex { get; set; }

    public int ChunkCount { get; set; }

    public int FileSizeBytes { get; set; }

    public int TransferredBytes { get; set; }

    public string ChunkDataBase64 { get; set; }

    public float PositionSeconds { get; set; }

    public bool Paused { get; set; }
}

internal sealed class IncomingTrackTransfer
{
    public string TransferKey { get; set; }

    public string SourceSteamId { get; set; }

    public string RequesterSteamId { get; set; }

    public string AudioFileHash { get; set; }

    public string AudioFileStem { get; set; }

    public string AudioFileExtension { get; set; }

    public int ChunkCount { get; set; }

    public int FileSizeBytes { get; set; }

    public int NextChunkIndex { get; set; }

    public int ReceivedBytes { get; set; }

    public int LastAcknowledgedChunkIndex { get; set; }

    public float NextProgressLogAt { get; set; }

    public string TempPath { get; set; }

    public string TrackName { get; set; }

    public string ArtistName { get; set; }

    public int TrackNumber { get; set; }

    public int LocalTrackNumber { get; set; }

    public FileStream Stream { get; set; }
}

internal sealed class OutgoingTrackTransfer
{
    public string TransferKey { get; set; }

    public string ClientTransferKey { get; set; }

    public NetworkConnection Connection { get; set; }

    public string TargetSteamId { get; set; }

    public int TrackNumber { get; set; }

    public string TrackName { get; set; }

    public string ArtistName { get; set; }

    public string AudioFileHash { get; set; }

    public string AudioFileStem { get; set; }

    public string AudioFileExtension { get; set; }

    public int ChunkCount { get; set; }

    public byte[] FileBytes { get; set; }

    public int SentBytes { get; set; }

    public int NextChunkIndexToSend { get; set; }

    public int AcknowledgedChunkCount { get; set; }

    public float NextProgressLogAt { get; set; }

    public float NextClientProgressLogAt { get; set; }

    public MusicSyncWireMessage ToWireMessage()
    {
        return new MusicSyncWireMessage
        {
            TrackNumber = TrackNumber,
            TrackName = TrackName,
            ArtistName = ArtistName,
            AudioFileHash = AudioFileHash,
            AudioFileStem = AudioFileStem
        };
    }
}
