using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace GalleryShip;

internal enum GalleryShipProbeOutcome
{
	Joinable,
	Full,
	RunInProgress,
	Unavailable
}

internal static class GalleryShipLobbyProbe
{
	private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new("GalleryShipProbe", LogType.Network);

	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

	public static async Task<GalleryShipProbeResult> ProbeAsync(ulong lobbyId, CancellationToken cancellationToken)
	{
		using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probeCts.CancelAfter(ProbeTimeout);

		NetClientGameService gameService = new();
		TaskCompletionSource<InitialGameInfoMessage> initialCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<ClientLobbyJoinResponseMessage> joinCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		void HandleInitialGameInfo(InitialGameInfoMessage message, ulong _)
		{
			initialCompletion.TrySetResult(message);
		}

		void HandleJoinResponse(ClientLobbyJoinResponseMessage message, ulong _)
		{
			joinCompletion.TrySetResult(message);
		}

		void HandleDisconnected(NetErrorInfo info)
		{
			ProbeDisconnectedException exception = new(info);
			initialCompletion.TrySetException(exception);
			joinCompletion.TrySetException(exception);
		}

		Task updateLoop = UpdateLoopAsync(gameService, probeCts.Token);
		gameService.RegisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
		gameService.RegisterMessageHandler<ClientLobbyJoinResponseMessage>(HandleJoinResponse);
		gameService.Disconnected += HandleDisconnected;

		try
		{
			NetErrorInfo? connectError = await SteamClientConnectionInitializer.FromLobby(lobbyId).Connect(gameService, probeCts.Token);
			if (connectError.HasValue)
			{
				GalleryShipProbeOutcome outcome = MapNetError(connectError.Value);
				Logger.Info($"[GalleryShip] probe lobby {lobbyId} rejected during JoinLobby: {outcome}");
				return GalleryShipProbeResult.FromOutcome(outcome);
			}

			InitialGameInfoMessage initialMessage = await initialCompletion.Task.WaitAsync(probeCts.Token);
			GalleryShipProbeOutcome initialOutcome = MapInitialMessage(initialMessage);
			if (initialOutcome != GalleryShipProbeOutcome.Joinable)
			{
				Logger.Info($"[GalleryShip] probe lobby {lobbyId} initial state: {initialOutcome}");
				return GalleryShipProbeResult.FromOutcome(initialOutcome);
			}

			gameService.SendMessage(CreateJoinRequest());
			ClientLobbyJoinResponseMessage joinResponse = await joinCompletion.Task.WaitAsync(probeCts.Token);
			IReadOnlyList<GalleryShipListingPlayer> players = BuildPlayers(joinResponse);
			Logger.Info($"[GalleryShip] probe lobby {lobbyId} joinable with {players.Count} player slots populated.");
			return new GalleryShipProbeResult(GalleryShipProbeOutcome.Joinable, players);
		}
		catch (ProbeDisconnectedException ex)
		{
			GalleryShipProbeOutcome outcome = MapNetError(ex.Info);
			Logger.Info($"[GalleryShip] probe lobby {lobbyId} disconnected before confirmation: {outcome}");
			return GalleryShipProbeResult.FromOutcome(outcome);
		}
		catch (OperationCanceledException)
		{
			Logger.Warn($"[GalleryShip] probe lobby {lobbyId} timed out.");
			return GalleryShipProbeResult.FromOutcome(GalleryShipProbeOutcome.Unavailable);
		}
		catch (Exception ex)
		{
			Logger.Warn($"[GalleryShip] probe lobby {lobbyId} failed unexpectedly: {ex}");
			return GalleryShipProbeResult.FromOutcome(GalleryShipProbeOutcome.Unavailable);
		}
		finally
		{
			if (gameService.IsConnected)
			{
				gameService.Disconnect(NetError.CancelledJoin, now: true);
			}

			probeCts.Cancel();
			try
			{
				await updateLoop;
			}
			catch (OperationCanceledException)
			{
			}

			gameService.UnregisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
			gameService.UnregisterMessageHandler<ClientLobbyJoinResponseMessage>(HandleJoinResponse);
			gameService.Disconnected -= HandleDisconnected;
		}
	}

	private static ClientLobbyJoinRequestMessage CreateJoinRequest()
	{
		SaveManager saveManager = SaveManager.Instance;
		int maxAscension = saveManager.Progress.MaxMultiplayerAscension;
		SerializableUnlockState unlockState = saveManager.GenerateUnlockStateFromProgress().ToSerializable();
		return new ClientLobbyJoinRequestMessage
		{
			maxAscensionUnlocked = maxAscension,
			unlockState = unlockState
		};
	}

	private static IReadOnlyList<GalleryShipListingPlayer> BuildPlayers(ClientLobbyJoinResponseMessage joinResponse)
	{
		if (joinResponse.playersInLobby == null || joinResponse.playersInLobby.Count == 0)
		{
			return Array.Empty<GalleryShipListingPlayer>();
		}

		ulong localPlayerId = 0;
		try
		{
			localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
		}
		catch (Exception ex)
		{
			Logger.Warn($"[GalleryShip] failed to resolve local player id: {ex.Message}");
		}

		List<LobbyPlayer> filteredPlayers = new(joinResponse.playersInLobby.Count);
		foreach (LobbyPlayer player in joinResponse.playersInLobby)
		{
			if (localPlayerId != 0 && player.id == localPlayerId)
			{
				continue;
			}

			filteredPlayers.Add(player);
		}

		if (filteredPlayers.Count == 0)
		{
			return Array.Empty<GalleryShipListingPlayer>();
		}

		filteredPlayers.Sort((left, right) => left.slotId.CompareTo(right.slotId));
		List<GalleryShipListingPlayer> players = new(filteredPlayers.Count);
		for (int index = 0; index < filteredPlayers.Count; index++)
		{
			LobbyPlayer player = filteredPlayers[index];
			string name = GetPlayerName(player.id);
			Texture2D? iconTexture = player.character?.IconTexture;
			players.Add(new GalleryShipListingPlayer(index, player.id, name, iconTexture));
		}
		return players;
	}

	private static string GetPlayerName(ulong playerId)
	{
		try
		{
			return PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, playerId);
		}
		catch (Exception ex)
		{
			Logger.Warn($"[GalleryShip] failed to resolve player name for {playerId}: {ex.Message}");
			return playerId.ToString();
		}
	}

	private static GalleryShipProbeOutcome MapInitialMessage(InitialGameInfoMessage message)
	{
		if (message.connectionFailureReason == ConnectionFailureReason.LobbyFull)
		{
			return GalleryShipProbeOutcome.Full;
		}

		if (message.connectionFailureReason == ConnectionFailureReason.RunInProgress || message.sessionState == RunSessionState.Running)
		{
			return GalleryShipProbeOutcome.RunInProgress;
		}

		if (message.connectionFailureReason.HasValue)
		{
			return GalleryShipProbeOutcome.Unavailable;
		}

		return GalleryShipProbeOutcome.Joinable;
	}

	private static GalleryShipProbeOutcome MapNetError(NetErrorInfo errorInfo)
	{
		return errorInfo.GetReason() switch
		{
			NetError.LobbyFull => GalleryShipProbeOutcome.Full,
			NetError.RunInProgress => GalleryShipProbeOutcome.RunInProgress,
			_ => GalleryShipProbeOutcome.Unavailable
		};
	}

	private static async Task UpdateLoopAsync(NetClientGameService gameService, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				gameService.Update();
			}
			catch (Exception ex)
			{
				Logger.Warn($"[GalleryShip] probe update loop error: {ex}");
			}

			await Task.Delay(16, cancellationToken);
		}
	}

	private sealed class ProbeDisconnectedException(NetErrorInfo info) : Exception(info.ToString())
	{
		public NetErrorInfo Info { get; } = info;
	}
}

internal readonly record struct GalleryShipProbeResult(
	GalleryShipProbeOutcome Outcome,
	IReadOnlyList<GalleryShipListingPlayer> Players)
{
	public static GalleryShipProbeResult FromOutcome(GalleryShipProbeOutcome outcome)
	{
		return new GalleryShipProbeResult(outcome, Array.Empty<GalleryShipListingPlayer>());
	}
}
