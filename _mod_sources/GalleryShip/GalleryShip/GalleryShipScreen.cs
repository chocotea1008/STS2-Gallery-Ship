using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.addons.mega_text;

namespace GalleryShip;

internal sealed partial class GalleryShipScreen : NSubmenu
{
	private const int MaxProbeConcurrency = 4;

	private const int RecentUnavailableRetryCount = 2;

	private static readonly TimeSpan RecentUnavailableRetryWindow = TimeSpan.FromMinutes(5);

	private static readonly TimeSpan UnavailableRetryDelay = TimeSpan.FromMilliseconds(450);

	private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";

	private static readonly string PopupScenePath = SceneHelper.GetScenePath("ui/vertical_popup");

	private const string TitleText = "\uAC24\uB9DD\uD638";

	private const string SubtitleText = "\uB313\uAE00 \uC5C6\uB294 \uC2AC\uB9DD\uD638 \uAE00 \uC911 \uC2E4\uC81C \uCC38\uC5EC \uAC00\uB2A5\uD55C \uBC29\uB9CC \uBCF4\uC5EC\uC90D\uB2C8\uB2E4.";

	private const string RefreshText = "\uC0C8\uB85C\uACE0\uCE68";

	private const string LoadingText = "\uC0C8\uB85C \uACE0\uCE68\uC911...";

	private const string ConnectingText = "\uC811\uC18D \uC911...";

	private const string NoJoinableText = "\uCC38\uC5EC \uAC00\uB2A5 \uBC29\uC744 \uCC3E\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.";

	private const string RetryHintText = "\uC0C8\uB85C\uACE0\uCE68\uC744 \uB2E4\uC2DC \uB20C\uB7EC \uC8FC\uC138\uC694.";

	private static readonly Vector2 PopupSize = new(1120f, 820f);

	private const float ListingButtonWidth = 620f;

	private const float ListingButtonHeight = 92f;

	private const float ListingRowHeight = 100f;

	private const float ListingButtonTop = 4f;

	private const float ListingTitleTop = -12f;

	private const float ListingTitleHeight = 18f;

	private const float ListingPlayerRowBottom = 4f;

	private const float ListingPlayerIconSize = 24f;

	private const int MaxLobbySlots = 3;

	private const int ListingTitleMaxChars = 34;

	private const int PlayerNameMaxChars = 12;

	private Control? _popup;
	private MegaLabel? _titleLabel;

	private MegaRichTextLabel? _bodyLabel;

	private NPopupYesNoButton? _refreshButton;

	private ScrollContainer? _scrollContainer;

	private VBoxContainer? _listContainer;

	private Control? _firstFocusableEntry;

	private CancellationTokenSource? _refreshCts;

	private Task? _refreshTask;

	private IReadOnlyList<GalleryShipListing> _pendingListings = Array.Empty<GalleryShipListing>();

	private string? _pendingError;

	private int _pendingSourceCount;

	private bool _refreshButtonConnected;

	private bool _scrollResizeConnected;

	private bool _forceRefreshOnOpen;

	private bool _isRefreshing;

	protected override Control? InitialFocusedControl => _firstFocusableEntry ?? _refreshButton;

	private readonly record struct ProbeCandidate(GalleryShipListing Listing, ulong LobbyId);

	public async Task JoinGameAsync(IClientConnectionInitializer connInitializer)
	{
		ShowConnectingState();
		try
		{
			NJoinFriendScreen joinFriendScreen = _stack.GetSubmenuType<NJoinFriendScreen>();
			joinFriendScreen.SetStack(_stack);
			if (!joinFriendScreen.IsNodeReady())
			{
				await ToSignal(joinFriendScreen, Node.SignalName.Ready);
			}

			await joinFriendScreen.JoinGameAsync(connInitializer);
		}
		finally
		{
			_forceRefreshOnOpen = true;
		}
	}

	public override void _Ready()
	{
		BuildUi();
		ConnectSignals();
		Callable.From(InitializePopupContent).CallDeferred();
	}

	public override void OnSubmenuOpened()
	{
		base.OnSubmenuOpened();
		if (_forceRefreshOnOpen || IsShowingConnectingState() || _refreshTask == null || _refreshTask.IsCompleted)
		{
			RefreshListings();
		}
	}

	public override void _Process(double delta)
	{
		base._Process(delta);
		// No per-frame updates are currently required here.
	}

	public override void OnSubmenuClosed()
	{
		_refreshCts?.Cancel();
		base.OnSubmenuClosed();
	}

	protected override void ConnectSignals()
	{
		BuildUi();
		base.ConnectSignals();
		if (_refreshButton != null && !_refreshButtonConnected)
		{
			_refreshButtonConnected = true;
			_refreshButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => RefreshListings()));
		}

		if (_scrollContainer != null && !_scrollResizeConnected)
		{
			_scrollResizeConnected = true;
			_scrollContainer.Connect(Control.SignalName.Resized, Callable.From(UpdateListLayout));
		}
	}

	private void BuildUi()
	{
		if (_popup != null)
		{
			return;
		}

		AnchorRight = 1f;
		AnchorBottom = 1f;
		MouseFilter = MouseFilterEnum.Stop;

		ColorRect dimmer = new()
		{
			Name = "Backdrop",
			AnchorRight = 1f,
			AnchorBottom = 1f,
			Color = new Color(0f, 0f, 0f, 0.12f),
			MouseFilter = MouseFilterEnum.Ignore
		};
		AddChild(dimmer);

		PackedScene backButtonScene = PreloadManager.Cache.GetScene(BackButtonScenePath);
		NBackButton backButton = backButtonScene.Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
		backButton.Name = "BackButton";
		AddChild(backButton);

		PackedScene popupScene = PreloadManager.Cache.GetScene(PopupScenePath);
		_popup = popupScene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
		_popup.Name = "GalleryShipPopup";
		_popup.AnchorLeft = 0.5f;
		_popup.AnchorTop = 0.5f;
		_popup.AnchorRight = 0.5f;
		_popup.AnchorBottom = 0.5f;
		_popup.OffsetLeft = -PopupSize.X * 0.5f;
		_popup.OffsetTop = -PopupSize.Y * 0.5f;
		_popup.OffsetRight = PopupSize.X * 0.5f;
		_popup.OffsetBottom = PopupSize.Y * 0.5f;
		_popup.CustomMinimumSize = PopupSize;
		AddChild(_popup);

		_titleLabel = _popup.GetNodeOrNull<MegaLabel>("Header");
		_bodyLabel = _popup.GetNodeOrNull<MegaRichTextLabel>("Description");
		_refreshButton = _popup.GetNodeOrNull<NPopupYesNoButton>("YesButton");
		NPopupYesNoButton? createButton = _popup.GetNodeOrNull<NPopupYesNoButton>("NoButton");

		if (_titleLabel != null)
		{
			_titleLabel.AddThemeFontSizeOverride("font_size", 56);
		}

		if (_bodyLabel != null)
		{
			_bodyLabel.AnchorLeft = 0f;
			_bodyLabel.AnchorTop = 0f;
			_bodyLabel.AnchorRight = 1f;
			_bodyLabel.AnchorBottom = 0f;
			_bodyLabel.OffsetLeft = 112f;
			_bodyLabel.OffsetTop = 116f;
			_bodyLabel.OffsetRight = -112f;
			_bodyLabel.OffsetBottom = 170f;
			_bodyLabel.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.NormalFontSize, 25);
			_bodyLabel.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.BoldFontSize, 25);
			_bodyLabel.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.ItalicsFontSize, 25);
		}

		if (_refreshButton != null)
		{
			UpdateRefreshButtonLayout();
		}

		if (createButton != null)
		{
			createButton.Hide();
			createButton.SetProcess(false);
		}

		_scrollContainer = new ScrollContainer
		{
			Name = "ListingScroll",
			AnchorRight = 1f,
			AnchorBottom = 1f,
			OffsetLeft = 92f,
			OffsetTop = 194f,
			OffsetRight = -92f,
			OffsetBottom = -150f,
			FollowFocus = true,
			MouseFilter = MouseFilterEnum.Stop
		};
		_popup.AddChild(_scrollContainer);

		_listContainer = new VBoxContainer
		{
			Name = "ListContainer",
			MouseFilter = MouseFilterEnum.Ignore
		};
		_listContainer.AddThemeConstantOverride("separation", 16);
		_scrollContainer.AddChild(_listContainer);
		Callable.From(UpdateListLayout).CallDeferred();
	}

	private void UpdateRefreshButtonLayout()
	{
		if (_refreshButton == null) return;

		_refreshButton.AnchorLeft = 1f;
		_refreshButton.AnchorTop = 1f;
		_refreshButton.AnchorRight = 1f;
		_refreshButton.AnchorBottom = 1f;
		_refreshButton.OffsetLeft = -382f;
		_refreshButton.OffsetTop = -141f;
		_refreshButton.OffsetRight = -96f;
		_refreshButton.OffsetBottom = -45f;
	}

	private void InitializePopupContent()
	{
		if (_titleLabel != null)
		{
			_titleLabel.SetTextAutoSize(TitleText);
		}

		if (_refreshTask == null || _refreshTask.IsCompleted)
		{
			SetBodyText(SubtitleText);
		}

		if (_refreshButton != null)
		{
			_refreshButton.IsYes = true;
			_refreshButton.SetText(RefreshText);
			UpdateRefreshButtonState();
		}

		UpdateListLayout();
		if (Visible && (_refreshTask == null || _refreshTask.IsCompleted) && _pendingListings.Count == 0)
		{
			RefreshListings();
		}
	}

	private void RefreshListings()
	{
		if (_refreshTask != null && !_refreshTask.IsCompleted && !_forceRefreshOnOpen)
		{
			return;
		}

		_refreshCts?.Cancel();
		_refreshCts = new CancellationTokenSource();
		_forceRefreshOnOpen = false;
		_isRefreshing = true;
		UpdateRefreshButtonState();
		_pendingSourceCount = 0;
		_firstFocusableEntry = null;
		SetBodyText(LoadingText);
		ClearList();
		_refreshTask = TaskHelper.RunSafely(RefreshListingsAsync(_refreshCts.Token));
	}

	private void ShowConnectingState()
	{
		_refreshCts?.Cancel();
		_forceRefreshOnOpen = true;
		_firstFocusableEntry = null;
		SetBodyText(ConnectingText);
		ClearList();
	}

	private bool IsShowingConnectingState()
	{
		return _bodyLabel != null && _bodyLabel.Text.Contains(ConnectingText, StringComparison.Ordinal);
	}

	private async Task RefreshListingsAsync(CancellationToken cancellationToken)
	{
		try
		{
			IReadOnlyList<GalleryShipListing> fetchedListings = await GalleryShipCrawler.FetchListingsAsync(cancellationToken);
			_pendingSourceCount = fetchedListings.Count;
			_pendingListings = await FilterJoinableListingsAsync(fetchedListings, cancellationToken);
			_pendingError = null;
			Log.Info($"[GalleryShip] fetched {_pendingSourceCount} listings and kept {_pendingListings.Count} joinable listings.");
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Log.Error("[GalleryShip] Failed to fetch board listings: " + ex);
			_pendingListings = Array.Empty<GalleryShipListing>();
			_pendingError = ex.Message;
		}
		_isRefreshing = false;

		Callable.From(ApplyPendingRefresh).CallDeferred();
	}

	private void ApplyPendingRefresh()
	{
		if (!GodotObject.IsInstanceValid(this) || _listContainer == null)
		{
			_isRefreshing = false;
			UpdateRefreshButtonState();
			return;
		}

		ClearList();
		_firstFocusableEntry = null;

		if (!string.IsNullOrWhiteSpace(_pendingError))
		{
			SetBodyText("\uBD88\uB7EC\uC624\uAE30\uC5D0 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4. " + RetryHintText);
			return;
		}

		if (_pendingListings.Count == 0)
		{
			SetBodyText(NoJoinableText);
			UpdateRefreshButtonState();
			return;
		}

		foreach (GalleryShipListing listing in _pendingListings)
		{
			(NJoinFriendButton button, Control rowHost) = CreateListingRow(listing);
			_listContainer.AddChild(rowHost);
			Callable.From(() => ApplyListingButtonContent(button, listing)).CallDeferred();
			_firstFocusableEntry ??= button;
		}

		SetBodyText($"\uCC38\uC5EC \uAC00\uB2A5\uD55C \uBC29 {_pendingListings.Count}\uAC1C \uC788\uC2B5\uB2C8\uB2E4.");
		UpdateListLayout();
		UpdateRefreshButtonState();
		(_firstFocusableEntry ?? _refreshButton)?.GrabFocus();
	}

	private static async Task<IReadOnlyList<GalleryShipListing>> FilterJoinableListingsAsync(
		IReadOnlyList<GalleryShipListing> fetchedListings,
		CancellationToken cancellationToken)
	{
		List<ProbeCandidate> candidates = new();
		foreach (GalleryShipListing listing in fetchedListings)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!listing.TryGetLobbyId(out ulong lobbyId))
			{
				continue;
			}

			candidates.Add(new ProbeCandidate(listing, lobbyId));
		}

		if (candidates.Count == 0)
		{
			return Array.Empty<GalleryShipListing>();
		}

		GalleryShipListing?[] accepted = new GalleryShipListing?[candidates.Count];
		Task[] probeTasks = new Task[candidates.Count];
		using SemaphoreSlim gate = new(MaxProbeConcurrency);
		for (int i = 0; i < candidates.Count; i++)
		{
			ProbeCandidate candidate = candidates[i];
			int slot = i;
			probeTasks[i] = ProbeListingAsync(candidate, slot, accepted, gate, cancellationToken);
		}

		await Task.WhenAll(probeTasks);

		List<GalleryShipListing> filtered = new();
		foreach (GalleryShipListing? listing in accepted)
		{
			if (listing != null)
			{
				filtered.Add(listing);
			}
		}

		return filtered;
	}

	private static async Task ProbeListingAsync(
		ProbeCandidate candidate,
		int slot,
		GalleryShipListing?[] accepted,
		SemaphoreSlim gate,
		CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			GalleryShipProbeResult probeResult = await ProbeWithUnavailableRetriesAsync(candidate.Listing, candidate.LobbyId, cancellationToken);
			if (probeResult.Outcome == GalleryShipProbeOutcome.Joinable)
			{
				accepted[slot] = candidate.Listing with
				{
					LobbyPlayers = probeResult.Players
				};
				return;
			}

			GalleryShipCrawler.MarkArticleRejected(candidate.Listing.ArticleId, probeResult.Outcome);
		}
		finally
		{
			gate.Release();
		}
	}

	private static async Task<GalleryShipProbeResult> ProbeWithUnavailableRetriesAsync(
		GalleryShipListing listing,
		ulong lobbyId,
		CancellationToken cancellationToken)
	{
		GalleryShipProbeResult lastResult = GalleryShipProbeResult.FromOutcome(GalleryShipProbeOutcome.Unavailable);
		bool shouldRetryUnavailable = ShouldRetryUnavailable(listing);
		int additionalRetryCount = shouldRetryUnavailable ? RecentUnavailableRetryCount : 0;
		for (int attempt = 0; attempt <= additionalRetryCount; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lastResult = await GalleryShipLobbyProbe.ProbeAsync(lobbyId, cancellationToken);
			if (lastResult.Outcome != GalleryShipProbeOutcome.Unavailable)
			{
				return lastResult;
			}

			if (attempt == additionalRetryCount)
			{
				return lastResult;
			}

			await Task.Delay(UnavailableRetryDelay, cancellationToken);
		}

		return lastResult;
	}

	private static bool ShouldRetryUnavailable(GalleryShipListing listing)
	{
		if (listing.PostedAt is not DateTimeOffset postedAt)
		{
			return false;
		}

		return DateTimeOffset.Now - postedAt < RecentUnavailableRetryWindow;
	}

	private void ClearList()
	{
		if (_listContainer == null)
		{
			return;
		}

		foreach (Node child in _listContainer.GetChildren())
		{
			child.QueueFree();
		}

		UpdateListLayout();
	}

	private void UpdateListLayout()
	{
		if (_scrollContainer == null || _listContainer == null)
		{
			return;
		}

		float width = Math.Max(ListingButtonWidth, _scrollContainer.Size.X - 18f);
		Vector2 containerSize = new(width, 0f);
		_listContainer.Position = new Vector2(0f, 8f);
		_listContainer.CustomMinimumSize = containerSize;
		_listContainer.Size = containerSize;

		foreach (Node child in _listContainer.GetChildren())
		{
			if (child is Control control)
			{
				control.CustomMinimumSize = new Vector2(width, control.CustomMinimumSize.Y);
			}
		}
	}

	private void SetBodyText(string text)
	{
		if (_bodyLabel != null)
		{
			_bodyLabel.Text = "[center]" + EscapeBbcode(text) + "[/center]";
		}
	}

	private void UpdateRefreshButtonState()
	{
		if (_refreshButton == null)
		{
			return;
		}

		bool disabled = _isRefreshing;
			_refreshButton.SetProcess(!disabled);
		_refreshButton.MouseFilter = disabled ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
		_refreshButton.Modulate = disabled ? new Color(0.68f, 0.68f, 0.68f, 0.95f) : new Color(1f, 1f, 1f, 1f);
	}


	private static (NJoinFriendButton Button, Control RowHost) CreateListingRow(GalleryShipListing listing)
	{
		ulong playerId = 0;
		if (PlatformUtil.PrimaryPlatform == PlatformType.Steam)
		{
			playerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
		}

		Control rowHost = new()
		{
			Name = "ListingRow",
			CustomMinimumSize = new Vector2(0f, ListingRowHeight),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MouseFilter = MouseFilterEnum.Ignore
		};

		NJoinFriendButton button = NJoinFriendButton.Create(playerId);
		button.AnchorLeft = 0.5f;
		button.AnchorTop = 0f;
		button.AnchorRight = 0.5f;
		button.AnchorBottom = 0f;
		button.OffsetLeft = -ListingButtonWidth * 0.5f;
		button.OffsetTop = ListingButtonTop;
		button.OffsetRight = ListingButtonWidth * 0.5f;
		button.OffsetBottom = ListingButtonTop + ListingButtonHeight;
		button.CustomMinimumSize = new Vector2(ListingButtonWidth, ListingButtonHeight);
		button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
		button.FocusMode = FocusModeEnum.All;
		button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => GalleryShipMod.OpenListing(listing)));
		rowHost.AddChild(button);
		return (button, rowHost);
	}

	private static void ApplyListingButtonContent(NJoinFriendButton button, GalleryShipListing listing)
	{
		MegaRichTextLabel? text = button.GetNodeOrNull<MegaRichTextLabel>("%Text");
		if (text == null)
		{
			return;
		}

		text.FitContent = false;
		text.BbcodeEnabled = true;
		text.ScrollActive = false;
		text.AnchorLeft = 0f;
		text.AnchorTop = 0f;
		text.AnchorRight = 1f;
		text.AnchorBottom = 0f;
		text.OffsetLeft = 18f;
		text.OffsetTop = ListingTitleTop;
		text.OffsetRight = -18f;
		text.OffsetBottom = ListingTitleTop + ListingTitleHeight;
		text.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.NormalFontSize, 12);
		text.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.BoldFontSize, 12);
		text.AddThemeFontSizeOverride(GalleryShipThemeCompat.RichTextLabel.ItalicsFontSize, 12);
		text.Text = "[center][b]" + EscapeBbcode(TrimDisplayText(listing.Title, ListingTitleMaxChars)) + "[/b][/center]";
		ApplyPlayerSlots(button, listing.LobbyPlayers);
	}

	private static void ApplyPlayerSlots(NJoinFriendButton button, IReadOnlyList<GalleryShipListingPlayer>? players)
	{
		HBoxContainer row = EnsurePlayerSlotRow(button);
		for (int slotIndex = 0; slotIndex < MaxLobbySlots; slotIndex++)
		{
			Control? slot = row.GetNodeOrNull<Control>($"Slot{slotIndex}");
			if (slot == null)
			{
				continue;
			}

			TextureRect? icon = slot.GetNodeOrNull<TextureRect>("Content/Icon");
			MegaLabel? label = slot.GetNodeOrNull<MegaLabel>("Content/Name");
			GalleryShipListingPlayer? player = FindPlayerForSlot(players, slotIndex);
			bool hasPlayer = player != null;
			if (icon != null)
			{
				icon.Texture = player?.IconTexture;
				icon.Visible = hasPlayer && player!.IconTexture != null;
			}

			if (label != null)
			{
				label.Text = hasPlayer ? TrimDisplayText(player!.Name, PlayerNameMaxChars) : string.Empty;
				label.Visible = hasPlayer;
			}
		}
	}

	private static GalleryShipListingPlayer? FindPlayerForSlot(IReadOnlyList<GalleryShipListingPlayer>? players, int slotIndex)
	{
		if (players == null)
		{
			return null;
		}

		foreach (GalleryShipListingPlayer player in players)
		{
			if (player.SlotId == slotIndex)
			{
				return player;
			}
		}

		return null;
	}

	private static HBoxContainer EnsurePlayerSlotRow(Control button)
	{
		HBoxContainer? existing = button.GetNodeOrNull<HBoxContainer>("GalleryShipPlayerRow");
		if (existing != null)
		{
			return existing;
		}

		HBoxContainer row = new()
		{
			Name = "GalleryShipPlayerRow",
			AnchorLeft = 0f,
			AnchorTop = 1f,
			AnchorRight = 1f,
			AnchorBottom = 1f,
			OffsetLeft = 16f,
			OffsetTop = -(ListingPlayerIconSize + ListingPlayerRowBottom + 2f),
			OffsetRight = -16f,
			OffsetBottom = -ListingPlayerRowBottom,
			MouseFilter = MouseFilterEnum.Ignore,
			Alignment = BoxContainer.AlignmentMode.Center
		};
		row.AddThemeConstantOverride("separation", 0);
		button.AddChild(row);

		for (int slotIndex = 0; slotIndex < MaxLobbySlots; slotIndex++)
		{
			CenterContainer slot = new()
			{
				Name = $"Slot{slotIndex}",
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				MouseFilter = MouseFilterEnum.Ignore
			};

			HBoxContainer content = new()
			{
				Name = "Content",
				Alignment = BoxContainer.AlignmentMode.Center,
				MouseFilter = MouseFilterEnum.Ignore
			};
			content.AddThemeConstantOverride("separation", 6);
			slot.AddChild(content);

			TextureRect icon = new()
			{
				Name = "Icon",
				CustomMinimumSize = new Vector2(ListingPlayerIconSize, ListingPlayerIconSize),
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				Visible = false,
				MouseFilter = MouseFilterEnum.Ignore
			};
			content.AddChild(icon);

			MegaLabel label = new()
			{
				Name = "Name",
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Left,
				Visible = false,
				MouseFilter = MouseFilterEnum.Ignore,
				AutoSizeEnabled = false
			};
			label.AddThemeFontSizeOverride(GalleryShipThemeCompat.Label.FontSize, 18);
			label.AddThemeColorOverride("font_color", Colors.White);
			label.AddThemeColorOverride("font_outline_color", new Color(0.09f, 0.08f, 0.06f, 1f));
			label.AddThemeConstantOverride(GalleryShipThemeCompat.Label.OutlineSize, 6);
			label.ApplyLocaleFontSubstitution(FontType.Regular, GalleryShipThemeCompat.Label.Font);
			content.AddChild(label);

			row.AddChild(slot);
		}

		return row;
	}

	private static string TrimDisplayText(string text, int maxChars)
	{
		if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
		{
			return text;
		}

		return text.Substring(0, maxChars) + "...";
	}

	private static string EscapeBbcode(string value)
	{
		return value
			.Replace("[", "\\[", StringComparison.Ordinal)
			.Replace("]", "\\]", StringComparison.Ordinal);
	}
}











