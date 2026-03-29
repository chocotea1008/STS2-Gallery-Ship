using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.addons.mega_text;
using HttpClient = System.Net.Http.HttpClient;

namespace GalleryShip;

[ModInitializer(nameof(OnModLoaded))]
public static class GalleryShipMod
{
	internal const string HarmonyId = "codex.galleryship";

	private const long PendingJoinTimeoutMs = 30000;
	private const long PendingHostClipboardTimeoutMs = 30000;
	private const string HostClipboardToastText = "\uC2A4\uD300 \uB85C\uBE44 ID\uAC00 \uD074\uB9BD\uBCF4\uB4DC\uC5D0 \uBCF5\uC0AC\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
	private const string UpdateToastText = "\uAC24\uB9DD\uD638 \uBAA8\uB4DC \uC5C5\uB370\uC774\uD2B8 \uB418\uC5C8\uC2B5\uB2C8\uB2E4. \uC7AC\uC2E4\uD589\uC2DC \uC801\uC6A9\uB429\uB2C8\uB2E4.";
	private const int SteamAppId = 2868840;
	private const float HostClipboardToastWidth = 780f;
	private const float HostClipboardToastHeight = 54f;
	private const float HostClipboardToastTop = 22f;
	private const float HostClipboardToastRight = 34f;
	private const float UpdateToastWidth = 900f;
	private const float UpdateToastHeight = 54f;
	private const float UpdateToastBottom = 26f;
	private const float UpdateToastRight = 34f;
	private const string UpdateRepoLatestReleaseUrl = "https://api.github.com/repos/chocotea1008/STS2-Gallery-Ship/releases/latest";
	private const string UpdateHttpUserAgent = "GalleryShipAutoUpdater/1.0";

	private static readonly FieldInfo? SteamLobbyIdField = AccessTools.Field(typeof(SteamClientConnectionInitializer), "_lobbySteamId");
	private static readonly JsonSerializerOptions UpdateJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};
	private static readonly Version CurrentVersion = ParseVersionString(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
	private static readonly string CurrentVersionText = CurrentVersion.ToString();

	private static string? _modDirectory;

	private static Texture2D? _iconTexture;

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
	private static Task? _updateCheckTask;
	private static string? _preparedUpdateVersion;
	private static Node? _mainMenuContext;
	private static bool _pendingUpdateToast;
	private static CanvasLayer? _updateToastLayer;
	private static Control? _updateToastRoot;
	private static MegaLabel? _updateToastLabel;
	private static Tween? _updateToastTween;

	public static void OnModLoaded()
	{
		_modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		new Harmony(HarmonyId).PatchAll();
		Log.Warn("[GalleryShip] Loaded.");
	}

	internal static void NotifyMainMenuReady(NMainMenu mainMenu)
	{
		_mainMenuContext = mainMenu;
		if (_pendingUpdateToast)
		{
			ShowUpdateToast(mainMenu);
			_pendingUpdateToast = false;
		}

		_updateCheckTask ??= TaskHelper.RunSafely(CheckForUpdatesAsync());
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

	private static async Task CheckForUpdatesAsync()
	{
		try
		{
			GitHubReleaseResponse? release = await FetchLatestReleaseAsync();
			if (release == null)
			{
				return;
			}

			Version latestVersion = ParseVersionString(release.TagName);
			if (latestVersion <= CurrentVersion)
			{
				return;
			}

			GitHubReleaseAsset? zipAsset = FindZipAsset(release);
			if (zipAsset == null || string.IsNullOrWhiteSpace(zipAsset.BrowserDownloadUrl))
			{
				Log.Warn("[GalleryShip] Latest release has no downloadable zip asset.");
				return;
			}

			string latestVersionText = latestVersion.ToString();
			if (_preparedUpdateVersion == latestVersionText)
			{
				QueueUpdateToast();
				return;
			}

			await DownloadAndPrepareUpdateAsync(latestVersionText, zipAsset.BrowserDownloadUrl);
			_preparedUpdateVersion = latestVersionText;
			QueueUpdateToast();
			Log.Info($"[GalleryShip] Prepared automatic update from {CurrentVersionText} to {latestVersionText}.");
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Auto-update check failed: " + ex.Message);
		}
	}

	private static async Task<GitHubReleaseResponse?> FetchLatestReleaseAsync()
	{
		using HttpClient client = new();
		client.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateHttpUserAgent);
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		string json = await client.GetStringAsync(UpdateRepoLatestReleaseUrl);
		return JsonSerializer.Deserialize<GitHubReleaseResponse>(json, UpdateJsonOptions);
	}

	private static GitHubReleaseAsset? FindZipAsset(GitHubReleaseResponse release)
	{
		if (release.Assets == null)
		{
			return null;
		}

		foreach (GitHubReleaseAsset asset in release.Assets)
		{
			if (!string.IsNullOrWhiteSpace(asset.Name) && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				return asset;
			}
		}

		return null;
	}

	private static async Task DownloadAndPrepareUpdateAsync(string latestVersionText, string downloadUrl)
	{
		if (string.IsNullOrWhiteSpace(_modDirectory))
		{
			return;
		}

		string updateDir = Path.Combine(_modDirectory, "_pending_update");
		Directory.CreateDirectory(updateDir);
		string zipPath = Path.Combine(updateDir, $"galleryship-v{latestVersionText}.zip");
		if (!File.Exists(zipPath))
		{
			using HttpClient client = new();
			client.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateHttpUserAgent);
			await using Stream downloadStream = await client.GetStreamAsync(downloadUrl);
			await using FileStream zipStream = File.Create(zipPath);
			await downloadStream.CopyToAsync(zipStream);
		}

		string scriptPath = Path.Combine(updateDir, $"apply_update_v{latestVersionText}.ps1");
		string scriptContents = BuildUpdaterScript(latestVersionText, zipPath, _modDirectory, Process.GetCurrentProcess().Id);
		File.WriteAllText(scriptPath, scriptContents, new UTF8Encoding(false));

		Process.Start(new ProcessStartInfo
		{
			FileName = "powershell.exe",
			Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		});
	}

	private static string BuildUpdaterScript(string latestVersionText, string zipPath, string targetDir, int currentPid)
	{
		string escapedVersion = EscapePowerShellLiteral(latestVersionText);
		string escapedZipPath = EscapePowerShellLiteral(zipPath);
		string escapedTargetDir = EscapePowerShellLiteral(targetDir);
		return """
$ErrorActionPreference = 'Stop'
$targetPid = CURRENT_PID
$zipPath = 'ZIP_PATH'
$targetDir = 'TARGET_DIR'
$version = 'VERSION_TEXT'

while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 500
}

Start-Sleep -Milliseconds 800
Add-Type -AssemblyName System.IO.Compression.FileSystem
$extractDir = Join-Path ([System.IO.Path]::GetDirectoryName($zipPath)) ('extract-' + $version)
if (Test-Path -LiteralPath $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}

[System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDir, $true)
$payloadDir = Join-Path $extractDir 'galleryship'
if (-not (Test-Path -LiteralPath $payloadDir)) {
    $payloadDir = $extractDir
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Get-ChildItem -LiteralPath $payloadDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetDir $_.Name) -Recurse -Force
}

Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
""".Replace("CURRENT_PID", currentPid.ToString())
			.Replace("ZIP_PATH", escapedZipPath)
			.Replace("TARGET_DIR", escapedTargetDir)
			.Replace("VERSION_TEXT", escapedVersion);
	}

	private static string EscapePowerShellLiteral(string value)
	{
		return value.Replace("'", "''");
	}

	private static Version ParseVersionString(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return new Version(0, 0, 0);
		}

		string trimmed = text.Trim().TrimStart('v', 'V');
		int cutIndex = trimmed.IndexOfAny(['-', '+', ' ']);
		if (cutIndex >= 0)
		{
			trimmed = trimmed[..cutIndex];
		}

		return Version.TryParse(trimmed, out Version? version) ? version : new Version(0, 0, 0);
	}

	private static void QueueUpdateToast()
	{
		_pendingUpdateToast = true;
		if (!GodotObject.IsInstanceValid(_mainMenuContext))
		{
			return;
		}

		Callable.From(() =>
		{
			if (!GodotObject.IsInstanceValid(_mainMenuContext))
			{
				return;
			}

			ShowUpdateToast(_mainMenuContext!);
			_pendingUpdateToast = false;
		}).CallDeferred();
	}

	private static void ShowUpdateToast(Node context)
	{
		SceneTree? tree = context.GetTree();
		Window? root = tree?.Root;
		if (root == null)
		{
			return;
		}

		EnsureUpdateToast(root);
		if (_updateToastRoot == null || _updateToastLabel == null)
		{
			return;
		}

		UpdateUpdateToastLayout(root);
		_updateToastLabel.Text = UpdateToastText;
		_updateToastTween?.Kill();
		_updateToastRoot.Position = GetUpdateToastPosition(root);
		_updateToastRoot.Scale = Vector2.One;
		_updateToastRoot.Modulate = new Color(1f, 1f, 1f, 1f);
		_updateToastTween = _updateToastRoot.CreateTween();
		_updateToastTween.TweenInterval(4.0);
		_updateToastTween.TweenProperty(_updateToastRoot, "modulate:a", 0f, 0.45);
		_updateToastTween.TweenProperty(_updateToastRoot, "scale", Vector2.One * 0.98f, 0.45);
	}

	private static void EnsureUpdateToast(Window root)
	{
		if (GodotObject.IsInstanceValid(_updateToastLayer) && GodotObject.IsInstanceValid(_updateToastRoot) && GodotObject.IsInstanceValid(_updateToastLabel))
		{
			return;
		}

		_updateToastLayer = new CanvasLayer
		{
			Name = "GalleryShipUpdateToastLayer",
			Layer = 200
		};
		root.AddChild(_updateToastLayer);

		_updateToastRoot = new Control
		{
			Name = "GalleryShipUpdateToast",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Modulate = new Color(1f, 1f, 1f, 0f),
			TopLevel = true
		};
		_updateToastRoot.Size = new Vector2(UpdateToastWidth, UpdateToastHeight);
		_updateToastLayer.AddChild(_updateToastRoot);

		_updateToastLabel = new MegaLabel
		{
			Name = "GalleryShipUpdateToastLabel",
			AnchorRight = 1f,
			AnchorBottom = 1f,
			OffsetLeft = 8f,
			OffsetTop = 4f,
			OffsetRight = -8f,
			OffsetBottom = -4f,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AutoSizeEnabled = false
		};
		_updateToastLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f, 1f));
		_updateToastLabel.AddThemeColorOverride("font_outline_color", new Color(0.16f, 0.11f, 0.06f, 1f));
		_updateToastLabel.AddThemeConstantOverride(ThemeConstants.Label.outlineSize, 8);
		_updateToastLabel.AddThemeFontSizeOverride(ThemeConstants.Label.fontSize, 20);
		_updateToastRoot.AddChild(_updateToastLabel);
		_updateToastLabel.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.font);
	}

	private static void UpdateUpdateToastLayout(Window root)
	{
		if (_updateToastRoot == null)
		{
			return;
		}

		_updateToastRoot.Size = new Vector2(UpdateToastWidth, UpdateToastHeight);
		_updateToastRoot.Position = GetUpdateToastPosition(root);
	}

	private static Vector2 GetUpdateToastPosition(Window root)
	{
		Vector2 size = root.Size;
		float x = Math.Max(24f, size.X - UpdateToastWidth - UpdateToastRight);
		float y = Math.Max(24f, size.Y - UpdateToastHeight - UpdateToastBottom);
		return new Vector2(x, y);
	}

	private sealed class GitHubReleaseResponse
	{
		[JsonPropertyName("tag_name")]
		public string? TagName { get; init; }

		[JsonPropertyName("assets")]
		public GitHubReleaseAsset[]? Assets { get; init; }
	}

	private sealed class GitHubReleaseAsset
	{
		[JsonPropertyName("name")]
		public string? Name { get; init; }

		[JsonPropertyName("browser_download_url")]
		public string? BrowserDownloadUrl { get; init; }
	}
}
