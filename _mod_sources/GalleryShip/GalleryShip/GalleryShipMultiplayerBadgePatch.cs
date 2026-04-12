using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.addons.mega_text;

namespace GalleryShip;

internal static class GalleryShipLobbyBadgeUi
{
	private const string BadgeNodeName = "GalleryShipInstallBadge";
	private const float BadgePadding = 4f;
	private const float BadgeScale = 0.95f;
	private const float MinimumBadgeSize = 14f;

	internal static void RefreshLobbyPlayer(NRemoteLobbyPlayer player)
	{
		MegaLabel? nameplate = player.GetNodeOrNull<MegaLabel>("%NameplateLabel");
		Refresh(player, nameplate, player.PlayerId);
	}

	internal static void RefreshPlayerState(NMultiplayerPlayerState playerState)
	{
		if (playerState.Player == null)
		{
			return;
		}

		MegaLabel? nameplate = playerState.GetNodeOrNull<MegaLabel>("%NameplateLabel");
		Refresh(playerState, nameplate, playerState.Player.NetId);
	}

	private static void Refresh(Control owner, MegaLabel? nameplate, ulong playerId)
	{
		if (nameplate == null)
		{
			return;
		}

		TextureRect? badge = owner.GetNodeOrNull<TextureRect>(BadgeNodeName);
		if (!GalleryShipMod.HasLobbyBadge(playerId))
		{
			if (badge != null)
			{
				badge.Visible = false;
			}

			return;
		}

		Texture2D? badgeTexture = GalleryShipMod.GetPlayerBadgeTexture();
		if (badgeTexture == null)
		{
			if (badge != null)
			{
				badge.Visible = false;
			}

			return;
		}

		badge ??= CreateBadge(owner);
		float badgeSize = ResolveBadgeSize(nameplate);
		badge.Texture = badgeTexture;
		badge.Size = new Vector2(badgeSize, badgeSize);
		badge.CustomMinimumSize = badge.Size;
		badge.Position = ResolveBadgePosition(owner, nameplate, badgeSize);
		badge.Visible = true;
	}

	private static TextureRect CreateBadge(Control owner)
	{
		TextureRect badge = new()
		{
			Name = BadgeNodeName,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize
		};
		owner.AddChild(badge);
		return badge;
	}

	private static float ResolveBadgeSize(MegaLabel nameplate)
	{
		float fontSize = nameplate.GetThemeFontSize("font_size");
		if (fontSize <= 0f)
		{
			fontSize = nameplate.Size.Y;
		}

		float badgeSize = Mathf.Max(MinimumBadgeSize, fontSize * BadgeScale);
		if (nameplate.Size.Y > 0f)
		{
			badgeSize = Mathf.Min(badgeSize, nameplate.Size.Y);
		}

		return badgeSize;
	}

	private static Vector2 ResolveBadgePosition(Control owner, MegaLabel nameplate, float badgeSize)
	{
		Vector2 labelPosition = nameplate.GlobalPosition - owner.GlobalPosition;
		float textWidth = ResolveRenderedTextWidth(nameplate);
		float textStartX = labelPosition.X;
		switch (nameplate.HorizontalAlignment)
		{
			case HorizontalAlignment.Center:
				textStartX += Mathf.Max(0f, (nameplate.Size.X - textWidth) * 0.5f);
				break;
			case HorizontalAlignment.Right:
				textStartX += Mathf.Max(0f, nameplate.Size.X - textWidth);
				break;
		}

		float x = textStartX + textWidth + BadgePadding;
		float y = labelPosition.Y + Mathf.Max(0f, (nameplate.Size.Y - badgeSize) * 0.5f);
		return new Vector2(Mathf.Max(0f, x), Mathf.Max(0f, y));
	}

	private static float ResolveRenderedTextWidth(MegaLabel nameplate)
	{
		Font? font = nameplate.GetThemeFont("font");
		int fontSize = nameplate.GetThemeFontSize("font_size");
		if (font != null && !string.IsNullOrEmpty(nameplate.Text))
		{
			float width = font.GetStringSize(nameplate.Text, HorizontalAlignment.Left, -1, fontSize).X;
			if (width > 0f)
			{
				return width;
			}
		}

		float fallbackWidth = nameplate.GetCombinedMinimumSize().X;
		if (nameplate.Size.X > 0f)
		{
			fallbackWidth = Mathf.Min(fallbackWidth, nameplate.Size.X);
		}

		return fallbackWidth;
	}
}

[HarmonyPatch]
internal static class GalleryShipMultiplayerBadgePatch
{
	[HarmonyPatch(typeof(NRemoteLobbyPlayerContainer), nameof(NRemoteLobbyPlayerContainer.Initialize))]
	[HarmonyPostfix]
	private static void RemoteLobbyPlayerContainerInitializePostfix(StartRunLobby lobby)
	{
		GalleryShipMod.PublishLocalLobbyBadge(lobby.NetService);
	}

	[HarmonyPatch(typeof(NRemoteLoadLobbyPlayerContainer), nameof(NRemoteLoadLobbyPlayerContainer.Initialize))]
	[HarmonyPostfix]
	private static void RemoteLoadLobbyPlayerContainerInitializePostfix(LoadRunLobby runLobby)
	{
		GalleryShipMod.PublishLocalLobbyBadge(runLobby.NetService);
	}

	[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
	[HarmonyPostfix]
	private static void StartRunLobbyCleanUpPostfix(StartRunLobby __instance, bool disconnectSession)
	{
		if (disconnectSession)
		{
			GalleryShipMod.ClearLobbyBadgeContext(__instance.NetService);
		}
	}

	[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
	[HarmonyPostfix]
	private static void LoadRunLobbyCleanUpPostfix(LoadRunLobby __instance, bool disconnectSession)
	{
		if (disconnectSession)
		{
			GalleryShipMod.ClearLobbyBadgeContext(__instance.NetService);
		}
	}

	[HarmonyPatch(typeof(NRemoteLobbyPlayer), "RefreshVisuals")]
	[HarmonyPostfix]
	private static void RemoteLobbyPlayerRefreshVisualsPostfix(NRemoteLobbyPlayer __instance)
	{
		GalleryShipLobbyBadgeUi.RefreshLobbyPlayer(__instance);
	}

	[HarmonyPatch(typeof(NRemoteLobbyPlayer), nameof(NRemoteLobbyPlayer._Process))]
	[HarmonyPostfix]
	private static void RemoteLobbyPlayerProcessPostfix(NRemoteLobbyPlayer __instance)
	{
		GalleryShipLobbyBadgeUi.RefreshLobbyPlayer(__instance);
	}

	[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
	[HarmonyPostfix]
	private static void MultiplayerPlayerStateReadyPostfix(NMultiplayerPlayerState __instance)
	{
		GalleryShipMod.RememberCurrentLobbyFromRunManager();
		GalleryShipLobbyBadgeUi.RefreshPlayerState(__instance);
	}

	[HarmonyPatch(typeof(NMultiplayerPlayerState), "RefreshValues")]
	[HarmonyPostfix]
	private static void MultiplayerPlayerStateRefreshValuesPostfix(NMultiplayerPlayerState __instance)
	{
		GalleryShipMod.RememberCurrentLobbyFromRunManager();
		GalleryShipLobbyBadgeUi.RefreshPlayerState(__instance);
	}
}
