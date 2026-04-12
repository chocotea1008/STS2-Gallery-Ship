using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Reaction;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace GalleryShip;

internal struct GalleryShipCustomReactionMessage : INetMessage, IPacketSerializable
{
	private static readonly QuantizeParams QuantizeParams = new(-3f, 3f, 16);

	public GalleryShipEmoteProvider provider;

	public Vector2 normalizedPosition;

	public string imageUrl;

	public string fileExtension;

	public bool ShouldBroadcast => true;

	public NetTransferMode Mode => NetTransferMode.Unreliable;

	public LogLevel LogLevel => LogLevel.VeryDebug;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteByte((byte)provider);
		writer.WriteVector2(normalizedPosition, QuantizeParams, QuantizeParams);
		writer.WriteString(imageUrl ?? string.Empty);
		writer.WriteString(fileExtension ?? string.Empty);
	}

	public void Deserialize(PacketReader reader)
	{
		provider = (GalleryShipEmoteProvider)reader.ReadByte();
		normalizedPosition = reader.ReadVector2(QuantizeParams, QuantizeParams);
		imageUrl = reader.ReadString();
		fileExtension = reader.ReadString();
	}
}

internal static class GalleryShipCustomReactionRuntime
{
	private const float WheelIconScale = 1.05f;
	private const float WheelOutwardOffset = 4f;
	private const float WheelIconMaxSize = 54f;
	private const float PopupIconSize = 112f;

	private sealed class WheelState
	{
		public Texture2D?[] OriginalTextures { get; } = new Texture2D?[GalleryShipEmoteStore.SlotCount];

		public Vector2[] OriginalPositions { get; } = new Vector2[GalleryShipEmoteStore.SlotCount];

		public Vector2[] OriginalSizes { get; } = new Vector2[GalleryShipEmoteStore.SlotCount];

		public Vector2[] OriginalMinimumSizes { get; } = new Vector2[GalleryShipEmoteStore.SlotCount];

		public TextureRect.StretchModeEnum[] OriginalStretchModes { get; } = new TextureRect.StretchModeEnum[GalleryShipEmoteStore.SlotCount];

		public TextureRect.ExpandModeEnum[] OriginalExpandModes { get; } = new TextureRect.ExpandModeEnum[GalleryShipEmoteStore.SlotCount];

		public bool CustomModeActive { get; set; }
	}

	private sealed class Registration
	{
		public required INetGameService NetService { get; init; }

		public required MessageHandlerDelegate<GalleryShipCustomReactionMessage> Handler { get; init; }
	}

	private static readonly Dictionary<NReactionWheel, WheelState> WheelStates = new();
	private static readonly Dictionary<NReactionContainer, Registration> Registrations = new();
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> RightWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_rightWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> DownRightWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_downRightWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> DownWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_downWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> DownLeftWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_downLeftWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> LeftWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_leftWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> UpLeftWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_upLeftWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> UpWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_upWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge> UpRightWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge>("_upRightWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, TextureRect> MarkerRef = AccessTools.FieldRefAccess<NReactionWheel, TextureRect>("_marker");
	private static readonly AccessTools.FieldRef<NReactionWheel, Vector2> CenterPositionRef = AccessTools.FieldRefAccess<NReactionWheel, Vector2>("_centerPosition");
	private static readonly AccessTools.FieldRef<NReactionWheel, NReactionWheelWedge?> SelectedWedgeRef = AccessTools.FieldRefAccess<NReactionWheel, NReactionWheelWedge?>("_selectedWedge");
	private static readonly AccessTools.FieldRef<NReactionWheel, bool> IgnoreNextMouseInputRef = AccessTools.FieldRefAccess<NReactionWheel, bool>("_ignoreNextMouseInput");
	private static readonly AccessTools.FieldRef<NReactionWheel, Player?> LocalPlayerRef = AccessTools.FieldRefAccess<NReactionWheel, Player?>("_localPlayer");
	private static readonly MethodInfo? HideWheelMethod = AccessTools.Method(typeof(NReactionWheel), "HideWheel");

	internal static void Attach(NReactionContainer container, INetGameService netService)
	{
		Detach(container);
		MessageHandlerDelegate<GalleryShipCustomReactionMessage> handler = async (message, senderId) =>
		{
			await HandleRemoteMessageAsync(container, message);
		};

		netService.RegisterMessageHandler(handler);
		Registrations[container] = new Registration
		{
			NetService = netService,
			Handler = handler
		};
	}

	internal static void Detach(NReactionContainer container)
	{
		if (!Registrations.Remove(container, out Registration? registration))
		{
			return;
		}

		registration.NetService.UnregisterMessageHandler(registration.Handler);
	}

	internal static void HandleWheelInput(NReactionWheel wheel, InputEvent inputEvent)
	{
		if (!GodotObject.IsInstanceValid(wheel))
		{
			return;
		}

		if (inputEvent is not InputEventKey keyEvent
			|| keyEvent.Keycode != Key.C
			|| keyEvent.AltPressed
			|| keyEvent.CtrlPressed
			|| keyEvent.MetaPressed)
		{
			return;
		}

		if (HasTextFocus(wheel))
		{
			return;
		}

		NGame? game = NGame.Instance;
		if (game == null || !game.ReactionContainer.InMultiplayer)
		{
			return;
		}

		if (keyEvent.Pressed && !keyEvent.Echo)
		{
			ShowCustomWheel(wheel);
			return;
		}

		if (!keyEvent.Pressed)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			ResolveCustomReaction(wheel);
		}
	}

	internal static void RestoreWheelTextures(NReactionWheel wheel)
	{
		if (!WheelStates.TryGetValue(wheel, out WheelState? state) || !state.CustomModeActive)
		{
			return;
		}

		ResetWheelSelection(wheel);
		RestoreTextureRects(wheel, state);
		ApplyTextures(wheel, state.OriginalTextures);
		IgnoreNextMouseInputRef(wheel) = false;
		if (!wheel.Visible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
		state.CustomModeActive = false;
	}

	private static bool HasTextFocus(NReactionWheel wheel)
	{
		return wheel.GetViewport().GuiGetFocusOwner() is TextEdit or LineEdit;
	}

	private static void ShowCustomWheel(NReactionWheel wheel)
	{
		if (wheel.Visible)
		{
			return;
		}

		GalleryShipEmoteItem?[] slots = GalleryShipEmoteStore.GetSlotsSnapshot();
		if (!slots.Any(static slot => slot != null))
		{
			return;
		}

		WheelState state = GetOrCreateWheelState(wheel);
		CaptureWheelLayout(wheel, state);
		state.CustomModeActive = true;
		GalleryShipTextureAsset?[] customAssets = BuildCustomAssets(slots);
		ResetWheelSelection(wheel);
		ApplyCustomTextures(wheel, state, customAssets);
		IgnoreNextMouseInputRef(wheel) = false;
		SelectedWedgeRef(wheel) = null;
		TextureRect marker = MarkerRef(wheel);
		Player? localPlayer = LocalPlayerRef(wheel);
		if (localPlayer != null)
		{
			marker.Texture = localPlayer.Character.MapMarker;
		}

		Vector2 centerPosition = wheel.GetViewport().GetMousePosition();
		CenterPositionRef(wheel) = centerPosition;
		marker.Position = (wheel.Size - marker.Size) * 0.5f;
		wheel.GlobalPosition = centerPosition - wheel.Size * wheel.Scale * 0.5f;
		wheel.Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Hidden;
	}

	private static void ResolveCustomReaction(NReactionWheel wheel)
	{
		if (!WheelStates.TryGetValue(wheel, out WheelState? state) || !state.CustomModeActive)
		{
			return;
		}

		GalleryShipEmoteItem?[] slots = GalleryShipEmoteStore.GetSlotsSnapshot();
		NReactionWheelWedge? selectedWedge = SelectedWedgeRef(wheel);
		HideWheelMethod?.Invoke(wheel, Array.Empty<object>());
		if (selectedWedge == null)
		{
			return;
		}

		int slotIndex = FindSelectedSlotIndex(wheel, selectedWedge);
		if (slotIndex < 0 || slotIndex >= slots.Length || slots[slotIndex] == null)
		{
			return;
		}

		NGame? game = NGame.Instance;
		if (game == null)
		{
			return;
		}

		SendCustomReactionAsync(game.ReactionContainer, slots[slotIndex]!, CenterPositionRef(wheel));
	}

	private static WheelState GetOrCreateWheelState(NReactionWheel wheel)
	{
		if (WheelStates.TryGetValue(wheel, out WheelState? existing))
		{
			return existing;
		}

		WheelState state = new();
		TextureRect[] wedgeTextures = GetWedgeTextureRects(wheel);
		for (int index = 0; index < wedgeTextures.Length; index++)
		{
			TextureRect textureRect = wedgeTextures[index];
			state.OriginalTextures[index] = textureRect.Texture;
			state.OriginalPositions[index] = textureRect.Position;
			state.OriginalSizes[index] = textureRect.Size;
			state.OriginalMinimumSizes[index] = textureRect.CustomMinimumSize;
			state.OriginalStretchModes[index] = textureRect.StretchMode;
			state.OriginalExpandModes[index] = textureRect.ExpandMode;
		}

		WheelStates[wheel] = state;
		return state;
	}

	private static void CaptureWheelLayout(NReactionWheel wheel, WheelState state)
	{
		TextureRect[] wedgeTextures = GetWedgeTextureRects(wheel);
		for (int index = 0; index < wedgeTextures.Length; index++)
		{
			TextureRect textureRect = wedgeTextures[index];
			state.OriginalPositions[index] = textureRect.Position;
			state.OriginalSizes[index] = textureRect.Size;
			state.OriginalMinimumSizes[index] = textureRect.CustomMinimumSize;
			state.OriginalStretchModes[index] = textureRect.StretchMode;
			state.OriginalExpandModes[index] = textureRect.ExpandMode;
		}
	}

	private static GalleryShipTextureAsset?[] BuildCustomAssets(IReadOnlyList<GalleryShipEmoteItem?> slots)
	{
		GalleryShipTextureAsset?[] textures = new GalleryShipTextureAsset?[GalleryShipEmoteStore.SlotCount];
		for (int index = 0; index < textures.Length; index++)
		{
			GalleryShipEmoteItem? slot = index < slots.Count ? slots[index] : null;
			if (slot == null)
			{
				textures[index] = null;
				continue;
			}

			string? localPath = GalleryShipEmoteService.TryGetCachedImagePath(slot.Provider, slot.ImageUrl);
			if (string.IsNullOrWhiteSpace(localPath))
			{
				_ = GalleryShipEmoteService.EnsureLocalImageAsync(slot);
			}

			textures[index] = GalleryShipEmoteService.LoadTextureAssetFromFile(localPath);
		}

		return textures;
	}

	private static TextureRect[] GetWedgeTextureRects(NReactionWheel wheel)
	{
		return new[]
		{
			RightWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			DownRightWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			DownWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			DownLeftWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			LeftWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			UpLeftWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			UpWedgeRef(wheel).GetNode<TextureRect>("TextureRect"),
			UpRightWedgeRef(wheel).GetNode<TextureRect>("TextureRect")
		};
	}

	private static void ApplyTextures(NReactionWheel wheel, IReadOnlyList<Texture2D?> textures)
	{
		TextureRect[] wedgeTextures = GetWedgeTextureRects(wheel);
		for (int index = 0; index < wedgeTextures.Length; index++)
		{
			wedgeTextures[index].Texture = index < textures.Count ? textures[index] : null;
		}
	}

	private static void ApplyCustomTextures(NReactionWheel wheel, WheelState state, IReadOnlyList<GalleryShipTextureAsset?> textures)
	{
		TextureRect[] wedgeTextures = GetWedgeTextureRects(wheel);
		for (int index = 0; index < wedgeTextures.Length; index++)
		{
			TextureRect textureRect = wedgeTextures[index];
			GalleryShipAnimatedTexturePlayer.Clear(textureRect);
			GalleryShipTextureAsset? textureAsset = index < textures.Count ? textures[index] : null;
			textureRect.Texture = textureAsset?.Texture;
			textureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
			textureRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
			Vector2 originalSize = state.OriginalSizes[index];
			if (originalSize == Vector2.Zero)
			{
				originalSize = new Vector2(40f, 40f);
			}

			textureRect.CustomMinimumSize = originalSize;
			textureRect.Size = originalSize;
			textureRect.Position = state.OriginalPositions[index];
			if (textureAsset != null)
			{
				GalleryShipAnimatedTexturePlayer.Apply(textureRect, textureAsset);
			}
		}
	}

	private static void RestoreTextureRects(NReactionWheel wheel, WheelState state)
	{
		TextureRect[] wedgeTextures = GetWedgeTextureRects(wheel);
		for (int index = 0; index < wedgeTextures.Length; index++)
		{
			TextureRect textureRect = wedgeTextures[index];
			GalleryShipAnimatedTexturePlayer.Clear(textureRect);
			textureRect.Position = state.OriginalPositions[index];
			textureRect.Size = state.OriginalSizes[index];
			textureRect.CustomMinimumSize = state.OriginalMinimumSizes[index];
			textureRect.StretchMode = state.OriginalStretchModes[index];
			textureRect.ExpandMode = state.OriginalExpandModes[index];
		}
	}

	private static void ResetWheelSelection(NReactionWheel wheel)
	{
		foreach (NReactionWheelWedge wedge in GetWedges(wheel))
		{
			wedge.OnDeselected();
		}

		SelectedWedgeRef(wheel) = null;
	}

	private static NReactionWheelWedge[] GetWedges(NReactionWheel wheel)
	{
		return
		[
			RightWedgeRef(wheel),
			DownRightWedgeRef(wheel),
			DownWedgeRef(wheel),
			DownLeftWedgeRef(wheel),
			LeftWedgeRef(wheel),
			UpLeftWedgeRef(wheel),
			UpWedgeRef(wheel),
			UpRightWedgeRef(wheel)
		];
	}

	private static int FindSelectedSlotIndex(NReactionWheel wheel, NReactionWheelWedge selectedWedge)
	{
		NReactionWheelWedge[] wedges =
		{
			RightWedgeRef(wheel),
			DownRightWedgeRef(wheel),
			DownWedgeRef(wheel),
			DownLeftWedgeRef(wheel),
			LeftWedgeRef(wheel),
			UpLeftWedgeRef(wheel),
			UpWedgeRef(wheel),
			UpRightWedgeRef(wheel)
		};

		for (int index = 0; index < wedges.Length; index++)
		{
			if (ReferenceEquals(wedges[index], selectedWedge))
			{
				return index;
			}
		}

		return -1;
	}

	private static async void SendCustomReactionAsync(NReactionContainer container, GalleryShipEmoteItem item, Vector2 mouseScreenPos)
	{
		try
		{
			string? localPath = await GalleryShipEmoteService.EnsureLocalImageAsync(item);
			if (!GodotObject.IsInstanceValid(container) || string.IsNullOrWhiteSpace(localPath))
			{
				return;
			}

			await container.ToSignal(container.GetTree(), SceneTree.SignalName.ProcessFrame);
			if (!GodotObject.IsInstanceValid(container))
			{
				return;
			}

			GalleryShipTextureAsset? textureAsset = GalleryShipEmoteService.LoadTextureAssetFromFile(localPath);
			if (textureAsset == null)
			{
				return;
			}

			ShowReaction(container, textureAsset, mouseScreenPos);
			if (!Registrations.TryGetValue(container, out Registration? registration))
			{
				return;
			}

			registration.NetService.SendMessage(new GalleryShipCustomReactionMessage
			{
				provider = item.Provider,
				normalizedPosition = NetCursorHelper.GetNormalizedPosition(mouseScreenPos, container),
				imageUrl = item.ImageUrl,
				fileExtension = item.FileExtension
			});
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to send custom reaction: " + ex.Message);
		}
	}

	private static async Task HandleRemoteMessageAsync(NReactionContainer container, GalleryShipCustomReactionMessage message)
	{
		try
		{
			string? localPath = await GalleryShipEmoteService.EnsureLocalImageAsync(message.provider, message.imageUrl, message.fileExtension);
			if (!GodotObject.IsInstanceValid(container) || string.IsNullOrWhiteSpace(localPath))
			{
				return;
			}

			await container.ToSignal(container.GetTree(), SceneTree.SignalName.ProcessFrame);
			if (!GodotObject.IsInstanceValid(container))
			{
				return;
			}

			GalleryShipTextureAsset? textureAsset = GalleryShipEmoteService.LoadTextureAssetFromFile(localPath);
			if (textureAsset == null)
			{
				return;
			}

			Vector2 position = NetCursorHelper.GetControlSpacePosition(message.normalizedPosition, container);
			ShowReaction(container, textureAsset, position);
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to handle remote custom reaction: " + ex.Message);
		}
	}

	private static void ShowReaction(NReactionContainer container, GalleryShipTextureAsset textureAsset, Vector2 position)
	{
		NReaction reaction = NReaction.Create(textureAsset.Texture);
		container.AddChild(reaction);
		reaction.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
		reaction.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
		reaction.CustomMinimumSize = new Vector2(PopupIconSize, PopupIconSize);
		reaction.Size = reaction.CustomMinimumSize;
		GalleryShipAnimatedTexturePlayer.Apply(reaction, textureAsset);
		reaction.GlobalPosition = position - reaction.Size * 0.5f;
		if (textureAsset.IsAnimated)
		{
			BeginAnimatedReaction(reaction, textureAsset);
		}
		else
		{
			reaction.BeginAnim();
		}
	}

	private static async void BeginAnimatedReaction(NReaction reaction, GalleryShipTextureAsset textureAsset)
	{
		try
		{
			Color modulate = reaction.Modulate;
			modulate.A = 0f;
			reaction.Modulate = modulate;
			float distance = Rng.Chaotic.NextFloat(40f, 60f);
			float degrees = Rng.Chaotic.NextFloat(-30f, 30f);
			Vector2 destination = reaction.Position + Vector2.Up.Rotated(Mathf.DegToRad(degrees)) * distance;
			double fullLoopDuration = textureAsset.FrameDurations.Sum();
			double holdDelay = Math.Max(0.6d, fullLoopDuration);
			Tween tween = reaction.CreateTween();
			tween.SetParallel();
			tween.TweenProperty(reaction, "position", destination, 0.30000001192092896).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
			tween.TweenProperty(reaction, "modulate:a", 1f, 0.30000001192092896).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
			tween.SetParallel(parallel: false);
			tween.TweenProperty(reaction, "modulate:a", 0f, 0.20000000298023224).SetDelay(holdDelay).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Expo);
			await reaction.ToSignal(tween, Tween.SignalName.Finished);
			if (GodotObject.IsInstanceValid(reaction))
			{
				reaction.QueueFree();
			}
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed animated reaction tween: " + ex.Message);
			if (GodotObject.IsInstanceValid(reaction))
			{
				reaction.QueueFree();
			}
		}
	}
}

[HarmonyPatch]
internal static class GalleryShipCustomReactionPatch
{
	[HarmonyPatch(typeof(NReactionContainer), nameof(NReactionContainer.InitializeNetworking))]
	[HarmonyPostfix]
	private static void ReactionContainerInitializeNetworkingPostfix(NReactionContainer __instance, INetGameService netService)
	{
		GalleryShipCustomReactionRuntime.Attach(__instance, netService);
	}

	[HarmonyPatch(typeof(NReactionContainer), nameof(NReactionContainer.DeinitializeNetworking))]
	[HarmonyPrefix]
	private static void ReactionContainerDeinitializeNetworkingPrefix(NReactionContainer __instance)
	{
		GalleryShipCustomReactionRuntime.Detach(__instance);
	}

	[HarmonyPatch(typeof(NReactionWheel), nameof(NReactionWheel._Input))]
	[HarmonyPostfix]
	private static void ReactionWheelInputPostfix(NReactionWheel __instance, InputEvent inputEvent)
	{
		GalleryShipCustomReactionRuntime.HandleWheelInput(__instance, inputEvent);
	}

	[HarmonyPatch(typeof(NReactionWheel), "HideWheel")]
	[HarmonyPostfix]
	private static void ReactionWheelHideWheelPostfix(NReactionWheel __instance)
	{
		GalleryShipCustomReactionRuntime.RestoreWheelTextures(__instance);
	}

	[HarmonyPatch(typeof(NReactionWheel), nameof(NReactionWheel._Notification))]
	[HarmonyPostfix]
	private static void ReactionWheelNotificationPostfix(NReactionWheel __instance)
	{
		if (!__instance.Visible)
		{
			GalleryShipCustomReactionRuntime.RestoreWheelTextures(__instance);
		}
	}

	[HarmonyPatch(typeof(NReactionWheel), nameof(NReactionWheel._ExitTree))]
	[HarmonyPostfix]
	private static void ReactionWheelExitTreePostfix(NReactionWheel __instance)
	{
		GalleryShipCustomReactionRuntime.RestoreWheelTextures(__instance);
	}
}
