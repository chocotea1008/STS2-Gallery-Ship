using System;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;

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
	private static readonly Logger Logger = new("GalleryShipProbe", LogType.Network);

	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

	public static async Task<GalleryShipProbeOutcome> ProbeAsync(ulong lobbyId, CancellationToken cancellationToken)
	{
		using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probeCts.CancelAfter(ProbeTimeout);

		NetClientGameService gameService = new();
		TaskCompletionSource<ProbeSignal> signalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		void HandleInitialGameInfo(InitialGameInfoMessage message, ulong _)
		{
			signalCompletion.TrySetResult(new ProbeSignal(message, null));
		}

		void HandleDisconnected(NetErrorInfo info)
		{
			signalCompletion.TrySetResult(new ProbeSignal(null, info));
		}

		Task updateLoop = UpdateLoopAsync(gameService, probeCts.Token);
		gameService.RegisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
		gameService.Disconnected += HandleDisconnected;

		try
		{
			NetErrorInfo? connectError = await SteamClientConnectionInitializer.FromLobby(lobbyId).Connect(gameService, probeCts.Token);
			if (connectError.HasValue)
			{
				GalleryShipProbeOutcome outcome = MapNetError(connectError.Value);
				Logger.Info($"[GalleryShip] probe lobby {lobbyId} rejected during JoinLobby: {outcome}");
				return outcome;
			}

			ProbeSignal signal = await signalCompletion.Task.WaitAsync(probeCts.Token);
			if (signal.DisconnectInfo.HasValue)
			{
				GalleryShipProbeOutcome outcome = MapNetError(signal.DisconnectInfo.Value);
				Logger.Info($"[GalleryShip] probe lobby {lobbyId} disconnected before confirmation: {outcome}");
				return outcome;
			}

			GalleryShipProbeOutcome messageOutcome = MapInitialMessage(signal.InitialMessage!.Value);
			Logger.Info($"[GalleryShip] probe lobby {lobbyId} initial state: {messageOutcome}");
			return messageOutcome;
		}
		catch (OperationCanceledException)
		{
			Logger.Warn($"[GalleryShip] probe lobby {lobbyId} timed out.");
			return GalleryShipProbeOutcome.Unavailable;
		}
		catch (Exception ex)
		{
			Logger.Warn($"[GalleryShip] probe lobby {lobbyId} failed unexpectedly: {ex}");
			return GalleryShipProbeOutcome.Unavailable;
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
			gameService.Disconnected -= HandleDisconnected;
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

	private readonly record struct ProbeSignal(InitialGameInfoMessage? InitialMessage, NetErrorInfo? DisconnectInfo);
}
