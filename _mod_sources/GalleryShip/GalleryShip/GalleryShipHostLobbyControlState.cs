using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace GalleryShip;

internal static class GalleryShipHostLobbyControlState
{
	private static readonly HashSet<StartRunLobby> StartLockedLobbies = new(ReferenceEqualityComparer.Instance);
	private static readonly HashSet<StartRunLobby> ForceLaunchPendingLobbies = new(ReferenceEqualityComparer.Instance);

	internal static bool IsStartLocked(StartRunLobby lobby)
	{
		return StartLockedLobbies.Contains(lobby);
	}

	internal static void SetStartLocked(StartRunLobby lobby, bool isLocked)
	{
		if (isLocked)
		{
			StartLockedLobbies.Add(lobby);
			return;
		}

		StartLockedLobbies.Remove(lobby);
	}

	internal static void RequestForceLaunch(StartRunLobby lobby)
	{
		ForceLaunchPendingLobbies.Add(lobby);
	}

	internal static bool ConsumeForceLaunch(StartRunLobby lobby)
	{
		return ForceLaunchPendingLobbies.Remove(lobby);
	}

	internal static void Clear(StartRunLobby lobby)
	{
		StartLockedLobbies.Remove(lobby);
		ForceLaunchPendingLobbies.Remove(lobby);
	}
}
