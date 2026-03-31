using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GalleryShip;

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.JoinGame))]
internal static class GalleryShipJoinPatch
{
	[HarmonyPrefix]
	private static bool Prefix(NMainMenu __instance, IClientConnectionInitializer connInitializer, ref Task __result)
	{
		if (!GalleryShipMod.TryConsumePendingGalleryShipJoin(connInitializer))
		{
			return true;
		}

		__result = GalleryShipMod.JoinViaGalleryShipAsync(__instance, connInitializer);
		return false;
	}
}
