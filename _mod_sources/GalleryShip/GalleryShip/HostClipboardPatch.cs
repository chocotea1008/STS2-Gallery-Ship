using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GalleryShip;

[HarmonyPatch]
internal static class HostClipboardPatch
{
	[HarmonyPatch(typeof(NCharacterSelectScreen), "InitializeMultiplayerAsHost")]
	[HarmonyPostfix]
	private static void CharacterSelectHostPostfix(NCharacterSelectScreen __instance, INetGameService gameService, int maxPlayers)
	{
		GalleryShipMod.PreparePendingHostClipboard(gameService);
	}

	[HarmonyPatch(typeof(NDailyRunScreen), "InitializeMultiplayerAsHost")]
	[HarmonyPostfix]
	private static void DailyHostPostfix(INetGameService gameService)
	{
		GalleryShipMod.PreparePendingHostClipboard(gameService);
	}

	[HarmonyPatch(typeof(NCustomRunScreen), "InitializeMultiplayerAsHost")]
	[HarmonyPostfix]
	private static void CustomHostPostfix(NCustomRunScreen __instance, INetGameService gameService, int maxPlayers)
	{
		GalleryShipMod.PreparePendingHostClipboard(gameService);
	}

	[HarmonyPatch(typeof(NSubmenuStack), "Push")]
	[HarmonyPostfix]
	private static void PushPostfix(NSubmenu screen)
	{
		if (screen is NCharacterSelectScreen or NDailyRunScreen or NCustomRunScreen)
		{
			GalleryShipMod.TryConsumePendingHostClipboard(screen);
		}
	}
}
