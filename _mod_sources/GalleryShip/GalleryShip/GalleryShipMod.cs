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
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using Steamworks;
using SystemVersion = System.Version;
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
	private const string PendingUpdateDirectoryName = "_pending_update";
	private const string PendingUpdateVersionFileName = "prepared_version.txt";
	private const string PlayerBadgeFileName = "gallery_ship_player_badge.webp";
	private const string LobbyMemberBadgeKey = "galleryship_mod";
	private const string LobbyMemberBadgeValue = "1";

	private static readonly FieldInfo? SteamLobbyIdField = AccessTools.Field(typeof(SteamClientConnectionInitializer), "_lobbySteamId");
	private static readonly JsonSerializerOptions UpdateJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};
	private static readonly string CurrentInformationalVersionText = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
	private static readonly bool IsBetaBuild = CurrentInformationalVersionText.Contains("-beta", StringComparison.OrdinalIgnoreCase);
	private static readonly SystemVersion CurrentVersion = ParseVersionString(CurrentInformationalVersionText);
	private static readonly string CurrentVersionText = CurrentVersion.ToString();

	private static string? _modDirectory;
	private static Texture2D? _iconTexture;
	private static Texture2D? _playerBadgeTexture;
	private static Texture2D? _whiteTexture;
	private static GalleryShipScreen? _screen;
	private static ulong? _currentSteamLobbyId;
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
		Log.Warn($"[GalleryShip] Mod directory: {_modDirectory}");
	}

	private static void WriteDebugLog(string message)
	{
		if (string.IsNullOrWhiteSpace(_modDirectory)) return;
		try
		{
			string logPath = Path.Combine(_modDirectory, "debug_log.txt");
			string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
		}
		catch { }
	}

	internal static void NotifyMainMenuReady(NMainMenu mainMenu)
	{
		_mainMenuContext = mainMenu;
		if (_pendingUpdateToast)
		{
			ShowUpdateToast(mainMenu);
			_pendingUpdateToast = false;
		}

		if (!IsBetaBuild)
		{
			_updateCheckTask ??= TaskHelper.RunSafely(CheckForUpdatesAsync());
		}
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

	internal static Texture2D? GetPlayerBadgeTexture()
	{
		if (_playerBadgeTexture != null)
		{
			return _playerBadgeTexture;
		}

		if (string.IsNullOrWhiteSpace(_modDirectory))
		{
			return null;
		}

		string badgePath = Path.Combine(_modDirectory, PlayerBadgeFileName);
		if (!File.Exists(badgePath))
		{
			Log.Warn("[GalleryShip] Player badge image missing: " + badgePath);
			return null;
		}

		try
		{
			Image image = Image.LoadFromFile(badgePath);
			if (!image.HasMipmaps())
			{
				image.GenerateMipmaps();
			}

			_playerBadgeTexture = ImageTexture.CreateFromImage(image);
			return _playerBadgeTexture;
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to load player badge image: " + ex.Message);
			return null;
		}
	}

	internal static void PublishLocalLobbyBadge(INetGameService gameService)
	{
		if (!TryRememberSteamLobby(gameService, out ulong lobbyId))
		{
			return;
		}

		try
		{
			SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyId), LobbyMemberBadgeKey, LobbyMemberBadgeValue);
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to publish lobby badge: " + ex.Message);
		}
	}

	internal static void RememberCurrentLobbyFromRunManager()
	{
		try
		{
			if (RunManager.Instance?.NetService != null)
			{
				TryRememberSteamLobby(RunManager.Instance.NetService, out _);
			}
		}
		catch
		{
		}
	}

	internal static void ClearLobbyBadgeContext(INetGameService gameService)
	{
		if (!TryParseSteamLobbyId(gameService, out ulong lobbyId))
		{
			_currentSteamLobbyId = null;
			return;
		}

		if (_currentSteamLobbyId == null || _currentSteamLobbyId.Value == lobbyId)
		{
			_currentSteamLobbyId = null;
		}
	}

	internal static bool HasLobbyBadge(ulong playerId)
	{
		if (playerId == 0 || _currentSteamLobbyId == null || !SteamInitializer.Initialized)
		{
			return false;
		}

		try
		{
			string value = SteamMatchmaking.GetLobbyMemberData(new CSteamID(_currentSteamLobbyId.Value), new CSteamID(playerId), LobbyMemberBadgeKey);
			return string.Equals(value, LobbyMemberBadgeValue, StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryGetCurrentSteamLobbyId(out ulong lobbyId)
	{
		if (_currentSteamLobbyId is ulong rememberedLobbyId && rememberedLobbyId != 0 && SteamInitializer.Initialized)
		{
			lobbyId = rememberedLobbyId;
			return true;
		}

		RememberCurrentLobbyFromRunManager();
		if (_currentSteamLobbyId is ulong refreshedLobbyId && refreshedLobbyId != 0 && SteamInitializer.Initialized)
		{
			lobbyId = refreshedLobbyId;
			return true;
		}

		lobbyId = 0;
		return false;
	}

	private static bool TryRememberSteamLobby(INetGameService gameService, out ulong lobbyId)
	{
		if (!TryParseSteamLobbyId(gameService, out lobbyId))
		{
			return false;
		}

		_currentSteamLobbyId = lobbyId;
		return true;
	}

	private static bool TryParseSteamLobbyId(INetGameService gameService, out ulong lobbyId)
	{
		lobbyId = 0;
		if (gameService.Platform != PlatformType.Steam || !SteamInitializer.Initialized)
		{
			return false;
		}

		string? rawLobbyIdentifier = gameService.GetRawLobbyIdentifier();
		return ulong.TryParse(rawLobbyIdentifier, out lobbyId) && lobbyId != 0;
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
		try
		{
			ShowHostClipboardToast(context, HostClipboardToastText);
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to show host clipboard toast: " + ex.Message);
		}
		Log.Info("[GalleryShip] Copied host join URL: " + joinUrl);
	}

	internal static void ClearPendingHostClipboard()
	{
		_pendingHostJoinUrl = null;
		_pendingHostJoinAtMs = 0;
	}

	private static void ShowHostClipboardToast(Node context, string message)
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
		_hostClipboardToastLabel.Text = message;
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
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AutoSizeEnabled = false
		};
		_hostClipboardToastLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f, 1f));
		_hostClipboardToastLabel.AddThemeColorOverride("font_outline_color", new Color(0.16f, 0.11f, 0.06f, 1f));
		_hostClipboardToastLabel.AddThemeConstantOverride(GalleryShipThemeCompat.Label.OutlineSize, 8);
		_hostClipboardToastLabel.AddThemeFontSizeOverride(GalleryShipThemeCompat.Label.FontSize, 22);
		_hostClipboardToastLabel.ApplyLocaleFontSubstitution(FontType.Regular, GalleryShipThemeCompat.Label.Font);
		_hostClipboardToastRoot.AddChild(_hostClipboardToastLabel);
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
		if (IsBetaBuild)
		{
			Log.Info($"[GalleryShip] Skipping auto-update check for beta build {CurrentInformationalVersionText}.");
			return;
		}

		try
		{
			CleanupPreparedUpdateStateIfApplied();
			GitHubReleaseResponse? release = await FetchLatestReleaseAsync();
			if (release == null)
			{
				return;
			}

			SystemVersion latestVersion = ParseVersionString(release.TagName);
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
			if (IsPreparedUpdateAlreadyQueued(latestVersionText))
			{
				_preparedUpdateVersion = latestVersionText;
				return;
			}

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

		string updateDir = GetPendingUpdateDirectory();
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
		WritePreparedUpdateVersion(latestVersionText);

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

[System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDir)
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

	private static bool IsPreparedUpdateAlreadyQueued(string latestVersionText)
	{
		if (string.IsNullOrWhiteSpace(_modDirectory))
		{
			return false;
		}

		string? preparedVersion = ReadPreparedUpdateVersion();
		if (!string.Equals(preparedVersion, latestVersionText, StringComparison.Ordinal))
		{
			return false;
		}

		string updateDir = GetPendingUpdateDirectory();
		string zipPath = Path.Combine(updateDir, $"galleryship-v{latestVersionText}.zip");
		string scriptPath = Path.Combine(updateDir, $"apply_update_v{latestVersionText}.ps1");
		return File.Exists(zipPath) && File.Exists(scriptPath);
	}

	private static void CleanupPreparedUpdateStateIfApplied()
	{
		string? preparedVersionText = ReadPreparedUpdateVersion();
		if (string.IsNullOrWhiteSpace(preparedVersionText))
		{
			return;
		}

		SystemVersion preparedVersion = ParseVersionString(preparedVersionText);
		if (preparedVersion <= CurrentVersion)
		{
			DeletePreparedUpdateVersionFile();
		}
	}

	private static string GetPendingUpdateDirectory()
	{
		return string.IsNullOrWhiteSpace(_modDirectory)
			? string.Empty
			: Path.Combine(_modDirectory, PendingUpdateDirectoryName);
	}

	private static string GetPreparedUpdateVersionPath()
	{
		string updateDir = GetPendingUpdateDirectory();
		return string.IsNullOrWhiteSpace(updateDir)
			? string.Empty
			: Path.Combine(updateDir, PendingUpdateVersionFileName);
	}

	private static string? ReadPreparedUpdateVersion()
	{
		try
		{
			string path = GetPreparedUpdateVersionPath();
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return null;
			}

			return File.ReadAllText(path, Encoding.UTF8).Trim();
		}
		catch
		{
			return null;
		}
	}

	private static void WritePreparedUpdateVersion(string versionText)
	{
		try
		{
			string updateDir = GetPendingUpdateDirectory();
			if (string.IsNullOrWhiteSpace(updateDir))
			{
				return;
			}

			Directory.CreateDirectory(updateDir);
			File.WriteAllText(GetPreparedUpdateVersionPath(), versionText, new UTF8Encoding(false));
		}
		catch
		{
		}
	}

	private static void DeletePreparedUpdateVersionFile()
	{
		try
		{
			string path = GetPreparedUpdateVersionPath();
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static SystemVersion ParseVersionString(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return new SystemVersion(0, 0, 0);
		}

		string trimmed = text.Trim().TrimStart('v', 'V');
		int cutIndex = trimmed.IndexOfAny(['-', '+', ' ']);
		if (cutIndex >= 0)
		{
			trimmed = trimmed[..cutIndex];
		}

		return SystemVersion.TryParse(trimmed, out SystemVersion? version) ? version : new SystemVersion(0, 0, 0);
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
		_updateToastLabel.AddThemeConstantOverride(GalleryShipThemeCompat.Label.OutlineSize, 8);
		_updateToastLabel.AddThemeFontSizeOverride(GalleryShipThemeCompat.Label.FontSize, 20);
		_updateToastLabel.ApplyLocaleFontSubstitution(FontType.Regular, GalleryShipThemeCompat.Label.Font);
		_updateToastRoot.AddChild(_updateToastLabel);
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




