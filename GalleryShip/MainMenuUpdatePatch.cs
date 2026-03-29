using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GalleryShip;

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuUpdatePatch
{
	[HarmonyPostfix]
	private static void Postfix(NMainMenu __instance)
	{
		GalleryShipMod.NotifyMainMenuReady(__instance);
	}
}
