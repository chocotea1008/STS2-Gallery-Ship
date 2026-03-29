using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.addons.mega_text;

namespace GalleryShip;

[ModInitializer(nameof(OnModLoaded))]
public static class GalleryShipMod
{
	internal const string HarmonyId = "codex.galleryship";

	private const long PendingJoinTimeoutMs = 30000;
	private const long PendingHostClipboardTimeoutMs = 30000;
	private const string HostClipboardToastText = "\uC2A4\uD300 \uB85C\uBE44 ID\uAC00 \uD074\uB9BD\uBCF4\uB4DC\uC5D0 \uBCF5\uC0AC\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
	private const int SteamAppId = 2868840;
	private const float HostClipboardToastWidth = 780f;
	private const float HostClipboardToastHeight = 54f;
	private const float HostClipboardToastTop = 22f;
	private const float HostClipboardToastRight = 34f;

	private static readonly FieldInfo? SteamLobbyIdField = AccessTools.Field(typeof(SteamClientConnectionInitializer), "_lobbySteamId");

	private static string? _modDirectory;

	private static Texture2D? _iconTexture;

	private static Texture2D? _frameOverlayTexture;

	private static Texture2D? _whiteTexture;

	private static GalleryShipScreen? _screen;

	private static ulong? _pendingGalleryShipLobbyId;

	private static ulong _pendingGalleryShipJoinAtMs;

	private static string? _pendingHostJoinUrl;

	private static ulong _pendingHostJoinAtMs;

	private static CanvasLayer? _hostClipboardToastLayer;

	private static Control? _hostClipboardToastRoot;

	private static MegaLabel? _hostClipboardToastLabel;

	private static Tween? _hostClipboardToastTween;

	public static void OnModLoaded()
	{
		_modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		new Harmony(HarmonyId).PatchAll();
		Log.Warn("[GalleryShip] Loaded.");
	}

	internal static Texture2D? GetIconTexture()
	{
		if (_iconTexture != null)
		{
			return _iconTexture;
		}

		if (string.IsNullOrWhiteSpace(_modDirectory))
		{
			return null;
		}

		string iconPath = Path.Combine(_modDirectory, "gallery_ship_button.png");
		if (!File.Exists(iconPath))
		{
			Log.Warn("[GalleryShip] Button image missing: " + iconPath);
			return null;
		}

		Image image = Image.LoadFromFile(iconPath);
		if (!image.HasMipmaps())
		{
			image.GenerateMipmaps();
		}
		_iconTexture = ImageTexture.CreateFromImage(image);
		return _iconTexture;
	}

	internal static Texture2D? GetFrameOverlayTexture()
	{
		if (_frameOverlayTexture != null)
		{
			return _frameOverlayTexture;
		}

		if (string.IsNullOrWhiteSpace(_modDirectory))
		{
			return null;
		}

		string overlayPath = Path.Combine(_modDirectory, "gallery_ship_frame_overlay.png");
		if (!File.Exists(overlayPath))
		{
			Log.Warn("[GalleryShip] Frame overlay missing: " + overlayPath);
			return null;
		}

		Image image = Image.LoadFromFile(overlayPath);
		if (!image.HasMipmaps())
		{
			image.GenerateMipmaps();
		}
		_frameOverlayTexture = ImageTexture.CreateFromImage(image);
		return _frameOverlayTexture;
	}

	internal static Texture2D GetWhiteTexture()
	{
		if (_whiteTexture != null)
		{
			return _whiteTexture;
		}

		Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		image.Fill(Colors.White);
		_whiteTexture = ImageTexture.CreateFromImage(image);
		return _whiteTexture;
	}

	internal static GalleryShipScreen? OpenGalleryShipScreen(NMultiplayerSubmenu submenu)
	{
		if (submenu.GetParent() is not NSubmenuStack stack)
		{
			Log.Warn("[GalleryShip] Could not find submenu stack.");
			return null;
		}

		if (!GodotObject.IsInstanceValid(_screen))
		{
			_screen = new GalleryShipScreen
			{
				Name = "GalleryShipScreen",
				Visible = false
			};
			stack.AddChild(_screen);
		}

		stack.Push(_screen);
		return _screen;
	}

	internal static async Task JoinViaGalleryShipAsync(NMainMenu mainMenu, IClientConnectionInitializer connInitializer)
	{
		NMultiplayerSubmenu submenu = mainMenu.OpenMultiplayerSubmenu();
		GalleryShipScreen? screen = OpenGalleryShipScreen(submenu);
		if (screen != null)
		{
			await screen.JoinGameAsync(connInitializer);
			return;
		}

		NJoinFriendScreen joinFriendScreen = submenu.OnJoinFriendsPressed();
		await joinFriendScreen.JoinGameAsync(connInitializer);
	}

	internal static void PreparePendingGalleryShipJoin(GalleryShipListing listing)
	{
		if (!listing.TryGetLobbyId(out ulong lobbyId))
		{
			ClearPendingGalleryShipJoin();
			return;
		}

		_pendingGalleryShipLobbyId = lobbyId;
		_pendingGalleryShipJoinAtMs = Time.GetTicksMsec();
	}

	internal static bool TryConsumePendingGalleryShipJoin(IClientConnectionInitializer connInitializer)
	{
		if (_pendingGalleryShipLobbyId == null)
		{
			return false;
		}

		ulong ageMs = Time.GetTicksMsec() - _pendingGalleryShipJoinAtMs;
		if (ageMs > PendingJoinTimeoutMs)
		{
			ClearPendingGalleryShipJoin();
			return false;
		}

		if (connInitializer is not SteamClientConnectionInitializer steamInitializer)
		{
			return false;
		}

		if (SteamLobbyIdField?.GetValue(steamInitializer) is not ulong lobbyId || lobbyId != _pendingGalleryShipLobbyId.Value)
		{
			return false;
		}

		ClearPendingGalleryShipJoin();
		return true;
	}

	internal static void ClearPendingGalleryShipJoin()
	{
		_pendingGalleryShipLobbyId = null;
		_pendingGalleryShipJoinAtMs = 0;
	}

	internal static void OpenListing(GalleryShipListing listing)
	{
		if (!string.IsNullOrWhiteSpace(listing.SteamUrl))
		{
			PreparePendingGalleryShipJoin(listing);
			Error error = OS.ShellOpen(listing.SteamUrl);
			if (error == Error.Ok)
			{
				return;
			}

			ClearPendingGalleryShipJoin();
			Log.Warn($"[GalleryShip] Failed to open steam URL directly ({error}), falling back to article page.");
		}

		ClearPendingGalleryShipJoin();
		OS.ShellOpen(listing.ArticleUrl);
	}

	internal static void PreparePendingHostClipboard(INetGameService gameService)
	{
		if (gameService.Platform != PlatformType.Steam)
		{
			ClearPendingHostClipboard();
			return;
		}

		string? lobbyId = gameService.GetRawLobbyIdentifier();
		ulong ownerId = PlatformUtil.GetLocalPlayerId(PlatformType.Steam);
		if (string.IsNullOrWhiteSpace(lobbyId) || ownerId == 0)
		{
			ClearPendingHostClipboard();
			return;
		}

		_pendingHostJoinUrl = $"steam://joinlobby/{SteamAppId}/{lobbyId}/{ownerId}";
		_pendingHostJoinAtMs = Time.GetTicksMsec();
		Log.Info("[GalleryShip] Prepared host join URL copy: " + _pendingHostJoinUrl);
	}

	internal static void TryConsumePendingHostClipboard(Node context)
	{
		if (string.IsNullOrWhiteSpace(_pendingHostJoinUrl))
		{
			return;
		}

		ulong ageMs = Time.GetTicksMsec() - _pendingHostJoinAtMs;
		if (ageMs > PendingHostClipboardTimeoutMs)
		{
			ClearPendingHostClipboard();
			return;
		}

		if (context.GetTree() == null)
		{
			return;
		}

		string joinUrl = _pendingHostJoinUrl;
		ClearPendingHostClipboard();
		DisplayServer.ClipboardSet(joinUrl);
		ShowHostClipboardToast(context);
		Log.Info("[GalleryShip] Copied host join URL: " + joinUrl);
	}

	internal static void ClearPendingHostClipboard()
	{
		_pendingHostJoinUrl = null;
		_pendingHostJoinAtMs = 0;
	}

	private static void ShowHostClipboardToast(Node context)
	{
		SceneTree? tree = context.GetTree();
		Window? root = tree?.Root;
		if (root == null)
		{
			return;
		}

		EnsureHostClipboardToast(root);
		if (_hostClipboardToastRoot == null || _hostClipboardToastLabel == null)
		{
			return;
		}

		UpdateHostClipboardToastLayout(root);
		_hostClipboardToastLabel.Text = HostClipboardToastText;
		_hostClipboardToastTween?.Kill();
		_hostClipboardToastRoot.Position = GetHostClipboardToastPosition(root);
		_hostClipboardToastRoot.Scale = Vector2.One;
		_hostClipboardToastRoot.Modulate = new Color(1f, 1f, 1f, 1f);
		_hostClipboardToastTween = _hostClipboardToastRoot.CreateTween();
		_hostClipboardToastTween.TweenProperty(_hostClipboardToastRoot, "position:y", HostClipboardToastTop + 8f, 0.18).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
		_hostClipboardToastTween.TweenInterval(2.0);
		_hostClipboardToastTween.TweenProperty(_hostClipboardToastRoot, "modulate:a", 0f, 0.45);
		_hostClipboardToastTween.TweenProperty(_hostClipboardToastRoot, "scale", Vector2.One * 0.96f, 0.45);
	}

	private static void EnsureHostClipboardToast(Window root)
	{
		if (GodotObject.IsInstanceValid(_hostClipboardToastLayer) && GodotObject.IsInstanceValid(_hostClipboardToastRoot) && GodotObject.IsInstanceValid(_hostClipboardToastLabel))
		{
			return;
		}

		_hostClipboardToastLayer = new CanvasLayer
		{
			Name = "GalleryShipHostClipboardToastLayer",
			Layer = 200
		};
		root.AddChild(_hostClipboardToastLayer);

		_hostClipboardToastRoot = new Control
		{
			Name = "GalleryShipHostClipboardToast",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Modulate = new Color(1f, 1f, 1f, 0f),
			TopLevel = true
		};
		_hostClipboardToastRoot.Size = new Vector2(HostClipboardToastWidth, HostClipboardToastHeight);
		_hostClipboardToastLayer.AddChild(_hostClipboardToastRoot);

		_hostClipboardToastLabel = new MegaLabel
		{
			Name = "GalleryShipHostClipboardToastLabel",
			AnchorRight = 1f,
			AnchorBottom = 1f,
			OffsetLeft = 8f,
			OffsetTop = 4f,
			OffsetRight = -8f,
			OffsetBottom = -4f,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_hostClipboardToastLabel.AutoSizeEnabled = false;
		_hostClipboardToastLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f, 1f));
		_hostClipboardToastLabel.AddThemeColorOverride("font_outline_color", new Color(0.16f, 0.11f, 0.06f, 1f));
		_hostClipboardToastLabel.AddThemeConstantOverride(ThemeConstants.Label.outlineSize, 8);
		_hostClipboardToastLabel.AddThemeFontSizeOverride(ThemeConstants.Label.fontSize, 22);
		_hostClipboardToastRoot.AddChild(_hostClipboardToastLabel);
		_hostClipboardToastLabel.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.font);
	}

	private static void UpdateHostClipboardToastLayout(Window root)
	{
		if (_hostClipboardToastRoot == null)
		{
			return;
		}

		_hostClipboardToastRoot.Size = new Vector2(HostClipboardToastWidth, HostClipboardToastHeight);
		_hostClipboardToastRoot.Position = GetHostClipboardToastPosition(root);
	}

	private static Vector2 GetHostClipboardToastPosition(Window root)
	{
		Vector2 size = root.Size;
		float x = Math.Max(24f, size.X - HostClipboardToastWidth - HostClipboardToastRight);
		return new Vector2(x, HostClipboardToastTop);
	}
}
