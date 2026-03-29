using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
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

	private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";

	private static readonly string PopupScenePath = SceneHelper.GetScenePath("ui/vertical_popup");

	private const string TitleText = "\uAC24\uB9DD\uD638";

	private const string SubtitleText = "\uB313\uAE00 \uC5C6\uB294 \uC2AC\uB9DD\uD638 \uAE00 \uC911 \uC2E4\uC81C \uCC38\uC5EC \uAC00\uB2A5\uD55C \uBC29\uB9CC \uBCF4\uC5EC\uC90D\uB2C8\uB2E4.";

	private const string RefreshText = "\uC0C8\uB85C\uACE0\uCE68";

	private const string LoadingText = "\uC0C8\uB85C \uACE0\uCE68\uC911...";

	private const string NoJoinableText = "\uCC38\uC5EC \uAC00\uB2A5 \uBC29\uC744 \uCC3E\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.";

	private const string RetryHintText = "\uC0C8\uB85C\uACE0\uCE68\uC744 \uB2E4\uC2DC \uB20C\uB7EC \uC8FC\uC138\uC694.";

	private const string ArticleFallbackText = "\uCC38\uC5EC \uB9C1\uD06C\uAC00 \uC5C6\uC5B4 \uC6D0\uBB38 \uAE00\uC744 \uC5FD\uB2C8\uB2E4.";

	private static readonly Vector2 PopupSize = new(1120f, 820f);

	private const float ListingButtonWidth = 620f;

	private const float CompactListingButtonHeight = 84f;

	private const float CompactListingRowHeight = 92f;

	private const float DetailedListingButtonHeight = 112f;

	private const float DetailedListingRowHeight = 120f;

	private const float ListingButtonTop = 4f;

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

	protected override Control? InitialFocusedControl => _firstFocusableEntry ?? _refreshButton;

	private readonly record struct ProbeCandidate(GalleryShipListing Listing, ulong LobbyId);

	public async Task JoinGameAsync(IClientConnectionInitializer connInitializer)
	{
		NJoinFriendScreen joinFriendScreen = _stack.GetSubmenuType<NJoinFriendScreen>();
		joinFriendScreen.SetStack(_stack);
		if (!joinFriendScreen.IsNodeReady())
		{
			await ToSignal(joinFriendScreen, Node.SignalName.Ready);
		}

		await joinFriendScreen.JoinGameAsync(connInitializer);
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
		if (_refreshTask == null || _refreshTask.IsCompleted)
		{
			RefreshListings();
		}
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
		_popup.GetNodeOrNull<NPopupYesNoButton>("NoButton")?.QueueFree();

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
			_bodyLabel.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.normalFontSize, 25);
			_bodyLabel.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.boldFontSize, 25);
			_bodyLabel.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.italicsFontSize, 25);
		}

		if (_refreshButton != null)
		{
			_refreshButton.AnchorLeft = 1f;
			_refreshButton.AnchorTop = 1f;
			_refreshButton.AnchorRight = 1f;
			_refreshButton.AnchorBottom = 1f;
			_refreshButton.OffsetLeft = -340f;
			_refreshButton.OffsetTop = -128f;
			_refreshButton.OffsetRight = -54f;
			_refreshButton.OffsetBottom = -32f;
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
		}

		UpdateListLayout();
		if (Visible && (_refreshTask == null || _refreshTask.IsCompleted) && _pendingListings.Count == 0)
		{
			RefreshListings();
		}
	}

	private void RefreshListings()
	{
		if (_refreshTask != null && !_refreshTask.IsCompleted)
		{
			return;
		}

		_refreshCts?.Cancel();
		_refreshCts = new CancellationTokenSource();
		_pendingSourceCount = 0;
		_firstFocusableEntry = null;
		SetBodyText(LoadingText);
		ClearList();
		_refreshTask = TaskHelper.RunSafely(RefreshListingsAsync(_refreshCts.Token));
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

		Callable.From(ApplyPendingRefresh).CallDeferred();
	}

	private void ApplyPendingRefresh()
	{
		if (!GodotObject.IsInstanceValid(this) || _listContainer == null)
		{
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
			GalleryShipProbeOutcome outcome = await GalleryShipLobbyProbe.ProbeAsync(candidate.LobbyId, cancellationToken);
			if (outcome == GalleryShipProbeOutcome.Joinable)
			{
				accepted[slot] = candidate.Listing;
				return;
			}

			GalleryShipCrawler.MarkArticleRejected(candidate.Listing.ArticleId);
		}
		finally
		{
			gate.Release();
		}
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

	private static (NJoinFriendButton Button, Control RowHost) CreateListingRow(GalleryShipListing listing)
	{
		ulong playerId = 0;
		if (PlatformUtil.PrimaryPlatform == PlatformType.Steam)
		{
			playerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
		}

		string? detail = GetListingDetailText(listing);
		bool isCompact = string.IsNullOrWhiteSpace(detail);
		float buttonHeight = isCompact ? CompactListingButtonHeight : DetailedListingButtonHeight;
		float rowHeight = isCompact ? CompactListingRowHeight : DetailedListingRowHeight;

		Control rowHost = new()
		{
			Name = "ListingRow",
			CustomMinimumSize = new Vector2(0f, rowHeight),
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
		button.OffsetBottom = ListingButtonTop + buttonHeight;
		button.CustomMinimumSize = new Vector2(ListingButtonWidth, buttonHeight);
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
		text.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.normalFontSize, 20);
		text.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.boldFontSize, 20);
		text.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.italicsFontSize, 20);

		string? detail = GetListingDetailText(listing);
		text.Text = string.IsNullOrWhiteSpace(detail)
			? "[center][b]" + EscapeBbcode(listing.Title) + "[/b][/center]"
			: "[center][b]" + EscapeBbcode(listing.Title) + "[/b]\n" + EscapeBbcode(detail) + "[/center]";
	}

	private static string? GetListingDetailText(GalleryShipListing listing)
	{
		return listing.ModSummary
			?? listing.Summary
			?? (listing.HasSteamUrl ? null : ArticleFallbackText);
	}

	private static string EscapeBbcode(string value)
	{
		return value
			.Replace("[", "\\[", StringComparison.Ordinal)
			.Replace("]", "\\]", StringComparison.Ordinal);
	}
}
