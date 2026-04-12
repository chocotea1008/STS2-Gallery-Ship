using System.Collections;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;

namespace GalleryShip;

[HarmonyPatch]
internal static class GalleryShipHostLobbyPatch
{
	private static readonly AccessTools.FieldRef<NDailyRunScreen, StartRunLobby?> DailyLobbyField =
		AccessTools.FieldRefAccess<NDailyRunScreen, StartRunLobby?>("_lobby");

	private static readonly AccessTools.FieldRef<StartRunLobby, IList?> ConnectingPlayersField =
		AccessTools.FieldRefAccess<StartRunLobby, IList?>("_connectingPlayers");

	[HarmonyPatch(typeof(NCharacterSelectScreen), "AfterInitialized")]
	[HarmonyPostfix]
	private static void CharacterSelectAfterInitialized(NCharacterSelectScreen __instance)
	{
		// Host control panel temporarily disabled.
	}

	[HarmonyPatch(typeof(NCustomRunScreen), "AfterInitialized")]
	[HarmonyPostfix]
	private static void CustomRunAfterInitialized(NCustomRunScreen __instance)
	{
		// Host control panel temporarily disabled.
	}

	[HarmonyPatch(typeof(NDailyRunScreen), "AfterLobbyInitialized")]
	[HarmonyPostfix]
	private static void DailyRunAfterInitialized(NDailyRunScreen __instance)
	{
		// Host control panel temporarily disabled.
	}

	[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.IsAboutToBeginGame))]
	[HarmonyPrefix]
	private static bool IsAboutToBeginGamePrefix(StartRunLobby __instance, ref bool __result)
	{
		return true;
	}

	[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
	[HarmonyPostfix]
	private static void CleanUpPostfix(StartRunLobby __instance)
	{
		GalleryShipHostLobbyControlState.Clear(__instance);
	}
}
