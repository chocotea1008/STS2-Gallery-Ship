using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;

namespace GalleryShip;

[HarmonyPatch(typeof(NMultiplayerSubmenu), "_Ready")]
internal static class GalleryShipMenuPatch
{
	private const string ButtonScenePath = "res://scenes/ui/submenu_button.tscn";
	private const int CardGap = 70;

	private const string GalleryTitle = "\uAC24\uB9DD\uD638";

	private const string GalleryDescription = "\uC2AC\uB808\uC774 \uB354 \uC2A4\uD30C\uC774\uC5B4 \uAC24\uB7EC\uB9AC\uC758 \uC2AC\uB9DD\uD638 \uAC8C\uC784\uC5D0 \uCC38\uC5EC\uD569\uB2C8\uB2E4.";

	[HarmonyPostfix]
	private static void Postfix(NMultiplayerSubmenu __instance)
	{
		HBoxContainer? container = __instance.GetNodeOrNull<HBoxContainer>("ButtonContainer");
		if (container == null)
		{
			Log.Warn("[GalleryShip] Multiplayer button container not found.");
			return;
		}

		if (container.GetNodeOrNull<NSubmenuButton>("GalleryShipButton") != null)
		{
			return;
		}

		NSubmenuButton? joinButton = container.GetNodeOrNull<NSubmenuButton>("JoinButton");
		NSubmenuButton button = CreateBaseButton(joinButton);
		button.Name = "GalleryShipButton";
		button.CustomMinimumSize = joinButton?.CustomMinimumSize ?? new Vector2(330f, 705f);
		button.SizeFlagsHorizontal = joinButton?.SizeFlagsHorizontal ?? Control.SizeFlags.ExpandFill;
		button.SizeFlagsVertical = joinButton?.SizeFlagsVertical ?? Control.SizeFlags.ExpandFill;
		container.AddChild(button);
		Callable.From(() => FinalizeButton(button, __instance, container)).CallDeferred();
	}

	private static NSubmenuButton CreateBaseButton(NSubmenuButton? joinButton)
	{
		PackedScene scene = PreloadManager.Cache.GetScene(ButtonScenePath);
		return scene.Instantiate<NSubmenuButton>(PackedScene.GenEditState.Disabled);
	}

	private static void FinalizeButton(NSubmenuButton button, NMultiplayerSubmenu submenu, HBoxContainer container)
	{
		if (!GodotObject.IsInstanceValid(button))
		{
			return;
		}

		container.AddThemeConstantOverride("separation", CardGap);
		ApplyDailyBlueBackground(button);
		ApplyIcon(button);
		ApplyHoverOverlay(button);
		ApplyText(button);
		ConnectHoverHighlight(button);
		RecenterVisibleButtons(container);
		button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => GalleryShipMod.OpenGalleryShipScreen(submenu)));
	}

	private static void RecenterVisibleButtons(HBoxContainer container)
	{
		if (!GodotObject.IsInstanceValid(container))
		{
			return;
		}

		int visibleCount = 0;
		float totalButtonWidth = 0f;
		foreach (Node child in container.GetChildren())
		{
			if (child is not Control control || !control.Visible)
			{
				continue;
			}

			visibleCount++;
			totalButtonWidth += Math.Max(330f, control.CustomMinimumSize.X);
		}

		if (visibleCount == 0)
		{
			return;
		}

		float separation = container.GetThemeConstant("separation");
		float totalWidth = totalButtonWidth + Math.Max(0, visibleCount - 1) * separation;
		container.OffsetLeft = -totalWidth * 0.5f;
		container.OffsetRight = totalWidth * 0.5f;
	}

	private static void ApplyDailyBlueBackground(NSubmenuButton button)
	{
		TextureRect? bgPanel = button.GetNodeOrNull<TextureRect>("BgPanel");
		if (bgPanel?.Material is not ShaderMaterial bgMaterial)
		{
			return;
		}

		ShaderMaterial blueMaterial = (ShaderMaterial)bgMaterial.Duplicate();
		blueMaterial.SetShaderParameter("h", 0.4800000228f);
		blueMaterial.SetShaderParameter("s", 0.8f);
		blueMaterial.SetShaderParameter("v", 0.7f);
		bgPanel.Material = blueMaterial;
	}

	private static void ApplyIcon(NSubmenuButton button)
	{
		TextureRect? icon = button.GetNodeOrNull<TextureRect>("Icon");
		Texture2D? texture = GalleryShipMod.GetIconTexture();
		if (icon != null && texture != null)
		{
			icon.Texture = texture;
			icon.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;
		}
	}

	private static void ApplyHoverOverlay(NSubmenuButton button)
	{
		TextureRect? icon = button.GetNodeOrNull<TextureRect>("Icon");
		TextureRect? existing = button.GetNodeOrNull<TextureRect>("GalleryShipHoverOverlay");
		existing?.QueueFree();
		if (icon == null)
		{
			return;
		}

		TextureRect overlay = CreateOverlayRect(icon, "GalleryShipHoverOverlay");
		overlay.Name = "GalleryShipHoverOverlay";
		overlay.Texture = icon.Texture;
		overlay.Material = null;
		overlay.MouseFilter = Control.MouseFilterEnum.Ignore;
		overlay.Modulate = new Color(1f, 1f, 1f, 0f);
		button.AddChild(overlay);
		button.MoveChild(overlay, icon.GetIndex() + 1);
	}

	private static TextureRect CreateOverlayRect(TextureRect template, string name)
	{
		TextureRect overlay = (TextureRect)template.Duplicate();
		overlay.Name = name;
		overlay.Visible = true;
		overlay.MouseFilter = Control.MouseFilterEnum.Ignore;
		overlay.Material = null;
		overlay.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;
		return overlay;
	}

	private static void ConnectHoverHighlight(NSubmenuButton button)
	{
		button.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ => SetHoverHighlight(button, true)));
		button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ => SetHoverHighlight(button, false)));
		SetHoverHighlight(button, false);
	}

	private static void SetHoverHighlight(NSubmenuButton button, bool focused)
	{
		TextureRect? icon = button.GetNodeOrNull<TextureRect>("Icon");
		TextureRect? hoverOverlay = button.GetNodeOrNull<TextureRect>("GalleryShipHoverOverlay");
		if (icon != null)
		{
			icon.Modulate = focused ? new Color(1f, 1f, 1f, 1f) : new Color(0.94f, 0.94f, 0.94f, 1f);
		}

		if (hoverOverlay != null)
		{
			hoverOverlay.Modulate = focused ? new Color(1f, 1f, 1f, 0.18f) : new Color(1f, 1f, 1f, 0f);
		}
	}

	private static void ApplyText(NSubmenuButton button)
	{
		MegaLabel? title = button.GetNodeOrNull<MegaLabel>("%Title");
		if (title != null)
		{
			title.SetTextAutoSize(GalleryTitle);
		}

		MegaRichTextLabel? description = button.GetNodeOrNull<MegaRichTextLabel>("%Description");
		if (description != null)
		{
			description.Text = GalleryDescription;
		}
	}
}
