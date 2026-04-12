using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;

namespace GalleryShip;

internal static class GalleryShipHostLobbyPanel
{
	private const float PanelWidth = 336f;
	private const float PanelHeight = 146f;
	private const float Margin = 16f;
	private const float GapAboveConfirmButton = 12f;

	private static CanvasLayer? _layer;
	private static Control? _root;
	private static Panel? _background;
	private static NRunModifierTickbox? _startLockTickbox;
	private static NSubmenuButton? _forceLaunchButton;
	private static StartRunLobby? _lobby;
	private static Control? _screen;

	internal static void Attach(Control screen, StartRunLobby? lobby)
	{
		SceneTree? tree = screen.GetTree();
		Window? rootWindow = tree?.Root;
		if (rootWindow == null)
		{
			return;
		}

		EnsureOverlay(rootWindow);
		_screen = screen;
		_lobby = lobby;

		bool isHostLobby = lobby != null && lobby.NetService.Type == NetGameType.Host;
		if (_root != null)
		{
			_root.Visible = isHostLobby;
		}

		if (!isHostLobby)
		{
			Log.Info("[GalleryShip] Host control panel hidden because current screen is not a host lobby.");
			return;
		}

		UpdateUi();
		Log.Info("[GalleryShip] Host control panel attached.");
	}

	private static void EnsureOverlay(Window rootWindow)
	{
		if (!GodotObject.IsInstanceValid(_layer))
		{
			_layer = new CanvasLayer
			{
				Name = "GalleryShipHostLobbyControlLayer",
				Layer = 200
			};
			rootWindow.AddChild(_layer);
		}

		if (GodotObject.IsInstanceValid(_root))
		{
			return;
		}

		_root = new Control
		{
			Name = "GalleryShipHostLobbyControlPanel",
			MouseFilter = Control.MouseFilterEnum.Pass,
			TopLevel = true,
			Visible = false,
			Size = new Vector2(PanelWidth, PanelHeight)
		};
		_layer!.AddChild(_root);

		_background = new Panel
		{
			Name = "Background",
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_background.AddThemeStyleboxOverride("panel", CreatePanelStyle());
		_root.AddChild(_background);

		VBoxContainer column = new()
		{
			Name = "Column",
			OffsetLeft = 10f,
			OffsetTop = 10f,
			OffsetRight = -10f,
			OffsetBottom = -10f,
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		column.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		column.AddThemeConstantOverride("separation", 8);
		_root.AddChild(column);

		_startLockTickbox = PreloadManager.Cache.GetScene(NRunModifierTickbox.scenePath).Instantiate<NRunModifierTickbox>(PackedScene.GenEditState.Disabled);
		_startLockTickbox.Name = "StartLockTickbox";
		_startLockTickbox.CustomMinimumSize = new Vector2(0f, 50f);
		_startLockTickbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_startLockTickbox.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(OnStartLockToggled));
		column.AddChild(_startLockTickbox);

		_forceLaunchButton = PreloadManager.Cache.GetScene("res://scenes/ui/submenu_button.tscn").Instantiate<NSubmenuButton>(PackedScene.GenEditState.Disabled);
		_forceLaunchButton.Name = "ForceLaunchButton";
		_forceLaunchButton.CustomMinimumSize = new Vector2(0f, 64f);
		_forceLaunchButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_forceLaunchButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnForceLaunchPressed()));
		column.AddChild(_forceLaunchButton);

		_forceLaunchButton.GetNodeOrNull<TextureRect>("Icon")?.Hide();
		if (_forceLaunchButton.GetNodeOrNull<MegaLabel>("%Title") is MegaLabel title)
		{
			title.SetTextAutoSize("강제 출항");
			title.AddThemeFontSizeOverride("font_size", 18);
			title.ApplyLocaleFontSubstitution(FontType.Regular, "font");
		}

		if (_forceLaunchButton.GetNodeOrNull<MegaRichTextLabel>("%Description") is MegaRichTextLabel description)
		{
			description.AddThemeColorOverride("font_outline_color", new Color("1A120C"));
			description.AddThemeConstantOverride("outline_size", 4);
			description.ApplyLocaleFontSubstitution(FontType.Regular, "normal_font");
		}

		Log.Info("[GalleryShip] Host control panel overlay created.");
	}

	private static void OnStartLockToggled(NTickbox tickbox)
	{
		if (_lobby == null)
		{
			return;
		}

		GalleryShipHostLobbyControlState.SetStartLocked(_lobby, tickbox.IsTicked);
		Log.Info($"[GalleryShip] Host start lock set to {tickbox.IsTicked}.");
		UpdateUi();
	}

	private static void OnForceLaunchPressed()
	{
		if (_lobby == null || _lobby.NetService.Type != NetGameType.Host)
		{
			return;
		}

		if (_lobby.Players.Count <= 1)
		{
			Log.Warn("[GalleryShip] Force launch requires at least two players in the lobby.");
			UpdateUi();
			return;
		}

		GalleryShipHostLobbyControlState.RequestForceLaunch(_lobby);
		Log.Info("[GalleryShip] Force launch requested from host control panel.");
		_lobby.SetReady(ready: true);
		UpdateUi();
	}

	private static void UpdateUi()
	{
		if (_root == null || _screen == null || _startLockTickbox == null)
		{
			return;
		}

		_root.Size = new Vector2(PanelWidth, PanelHeight);
		_root.Position = GetPanelPosition(_screen, _root.GetTree()!.Root);
		_startLockTickbox.IsTicked = _lobby != null && GalleryShipHostLobbyControlState.IsStartLocked(_lobby);

		MegaRichTextLabel? tickboxDescription = _startLockTickbox.GetNodeOrNull<MegaRichTextLabel>("%Description");
		if (tickboxDescription != null)
		{
			tickboxDescription.Text = "[color=#EFC851]스타트 락[/color]\n[color=#FFF6E2]전원 레디여도 출항 보류[/color]";
			tickboxDescription.AddThemeColorOverride("font_outline_color", new Color("1A120C"));
			tickboxDescription.AddThemeConstantOverride("outline_size", 5);
			tickboxDescription.ApplyLocaleFontSubstitution(FontType.Regular, "normal_font");
		}

		bool canForceLaunch = _lobby != null && _lobby.Players.Count > 1;
		if (_forceLaunchButton != null)
		{
			if (canForceLaunch)
			{
				_forceLaunchButton.Enable();
				_forceLaunchButton.Modulate = new Color(1f, 1f, 1f, 1f);
			}
			else
			{
				_forceLaunchButton.Disable();
				_forceLaunchButton.Modulate = new Color(1f, 1f, 1f, 0.72f);
			}

			if (_forceLaunchButton.GetNodeOrNull<MegaRichTextLabel>("%Description") is MegaRichTextLabel buttonDescription)
			{
				buttonDescription.Text = canForceLaunch
					? "[color=#FFF6E2]레디 미완료 상태 무시[/color]"
					: "[color=#BDB2A0]2인 이상부터 사용 가능[/color]";
			}
		}

		Log.Info($"[GalleryShip] Host control panel positioned at {_root.Position}.");
	}

	private static Vector2 GetPanelPosition(Control screen, Window rootWindow)
	{
		Control? confirmButton = screen.GetNodeOrNull<Control>("ConfirmButton");
		Vector2 viewportSize = rootWindow.Size;
		if (confirmButton == null)
		{
			float fallbackX = viewportSize.X - PanelWidth - Margin;
			float fallbackY = viewportSize.Y - PanelHeight - 180f;
			return new Vector2(Mathf.Max(Margin, fallbackX), Mathf.Max(Margin, fallbackY));
		}

		Vector2 confirmPosition = confirmButton.GlobalPosition;
		Vector2 confirmSize = confirmButton.Size;
		float x = confirmPosition.X + confirmSize.X - PanelWidth;
		float y = confirmPosition.Y - PanelHeight - GapAboveConfirmButton;
		float maxX = Mathf.Max(Margin, viewportSize.X - PanelWidth - Margin);
		float maxY = Mathf.Max(Margin, viewportSize.Y - PanelHeight - Margin);
		return new Vector2(Mathf.Clamp(x, Margin, maxX), Mathf.Clamp(y, Margin, maxY));
	}

	private static StyleBoxFlat CreatePanelStyle()
	{
		return new StyleBoxFlat
		{
			BgColor = new Color("120F0CAA"),
			BorderColor = new Color("8D7750CC"),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusTopLeft = 10,
			CornerRadiusTopRight = 10,
			CornerRadiusBottomLeft = 10,
			CornerRadiusBottomRight = 10,
			ShadowColor = new Color("00000040"),
			ShadowSize = 4,
			ContentMarginLeft = 8f,
			ContentMarginTop = 8f,
			ContentMarginRight = 8f,
			ContentMarginBottom = 8f
		};
	}
}
