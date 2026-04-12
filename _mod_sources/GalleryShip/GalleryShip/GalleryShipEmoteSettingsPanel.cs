using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using static Godot.Control;

namespace GalleryShip;

internal sealed class GalleryShipEmoteSettingsPanel : NSettingsPanel
{
	private enum EmoteTab
	{
		DcCon,
		Favorites
	}

	private const float HorizontalPadding = 192f;
	private const float RightPanelWidth = 332f;
	private const float RadialSurfaceSize = 332f;
	private const float RadialSlotSize = 74f;
	private const float RadialRadius = 110f;
	private const float ExternalScrollPadding = 56f;
	private const float MinimumPanelHeight = 420f;
	private const float PackCardHeight = 132f;
	private const float PackPreviewWidth = 92f;
	private const float PackPreviewHeight = 68f;
	internal const float ItemCardWidth = 140f;
	internal const float ItemCardHeight = 128f;
	internal const float ItemPreviewWidth = 98f;
	internal const float ItemPreviewHeight = 72f;

	private readonly Dictionary<EmoteTab, Button> _tabButtons = new();
	private readonly List<GalleryShipEmoteItemCard> _visibleItemCards = new();
	private readonly GalleryShipPingSlotControl[] _slotControls = new GalleryShipPingSlotControl[GalleryShipEmoteStore.SlotCount];

	private VBoxContainer _root = null!;
	private VBoxContainer _leftColumn = null!;
	private VBoxContainer _rightColumn = null!;
	private ScrollContainer _resultScroll = null!;
	private VBoxContainer _resultContent = null!;
	private Button _backButton = null!;
	private Label _titleLabel = null!;
	private LineEdit _searchEdit = null!;
	private Button _searchButton = null!;
	private Label _statusLabel = null!;
	private Label _selectionLabel = null!;
	private HFlowContainer _resultGrid = null!;
	private Label _emptyLabel = null!;
	private EmoteTab _currentTab = EmoteTab.DcCon;
	private GalleryShipEmotePack? _selectedPack;
	private GalleryShipEmoteItem? _selectedItem;
	private IReadOnlyList<GalleryShipEmotePack> _currentPacks = Array.Empty<GalleryShipEmotePack>();
	private IReadOnlyList<GalleryShipEmoteItem> _currentItems = Array.Empty<GalleryShipEmoteItem>();
	private int _requestVersion;
	private bool _initialLoadRequested;

	public override void _Ready()
	{
		BuildUi();
		_firstControl = _tabButtons[EmoteTab.DcCon];
		Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnPanelVisibilityChanged));
		GetViewport().Connect(Viewport.SignalName.SizeChanged, Callable.From(OnViewportSizeChanged));
		RefreshTabVisuals();
		RefreshPingSlots();
		RefreshSelectionUi();
		RefreshResponsiveLayout();
		RefreshPanelSize();
	}

	private void OnViewportSizeChanged()
	{
		RefreshResponsiveLayout();
		RefreshPanelSize();
	}

	private void OnPanelVisibilityChanged()
	{
		RefreshPingSlots();
		RefreshResponsiveLayout();
		RefreshPanelSize();
		if (!Visible)
		{
			return;
		}

		if (!_initialLoadRequested)
		{
			_initialLoadRequested = true;
			TriggerSearch();
		}

		if (_currentTab == EmoteTab.Favorites)
		{
			RenderCurrentCollection();
		}
	}

	private void BuildUi()
	{
		_root = new VBoxContainer
		{
			Name = "VBoxContainer",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		_root.SetAnchorsPreset(LayoutPreset.FullRect);
		_root.OffsetLeft = HorizontalPadding;
		_root.OffsetTop = 0f;
		_root.OffsetRight = -HorizontalPadding;
		_root.OffsetBottom = 0f;
		_root.AddThemeConstantOverride("separation", 12);
		AddChild(_root);

		HBoxContainer tabRow = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		tabRow.AddThemeConstantOverride("separation", 8);
		_root.AddChild(tabRow);
		CreateTabButton(tabRow, EmoteTab.DcCon, "디시콘");
		CreateTabButton(tabRow, EmoteTab.Favorites, "즐겨찾기");

		_root.AddChild(CreateDivider(horizontal: true));

		HBoxContainer body = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		body.AddThemeConstantOverride("separation", 18);
		_root.AddChild(body);

		_leftColumn = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		_leftColumn.AddThemeConstantOverride("separation", 8);
		body.AddChild(_leftColumn);

		body.AddChild(CreateDivider(horizontal: false));

		_rightColumn = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(RightPanelWidth, 0f),
			SizeFlagsVertical = SizeFlags.ShrinkBegin
		};
		_rightColumn.AddThemeConstantOverride("separation", 10);
		body.AddChild(_rightColumn);

		BuildLeftColumn();
		BuildRightColumn();
	}

	private void BuildLeftColumn()
	{
		HBoxContainer searchRow = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		searchRow.AddThemeConstantOverride("separation", 8);
		_leftColumn.AddChild(searchRow);

		_backButton = CreateActionButton("목록");
		_backButton.Visible = false;
		_backButton.Pressed += () =>
		{
			_requestVersion++;
			_selectedPack = null;
			_selectedItem = null;
			_currentItems = Array.Empty<GalleryShipEmoteItem>();
			SetStatus(string.Empty, busy: false);
			RefreshSelectionUi();
			RenderCurrentCollection();
		};
		searchRow.AddChild(_backButton);

		_titleLabel = CreateTextLabel("디시콘", fontColor: StsColors.cream);
		_titleLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		searchRow.AddChild(_titleLabel);

		Control spacer = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		searchRow.AddChild(spacer);

		_searchEdit = new LineEdit
		{
			PlaceholderText = "디시콘 검색",
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		GalleryShipUiStyles.ApplyInputStyle(_searchEdit);
		_searchEdit.TextSubmitted += _ => TriggerSearch();
		searchRow.AddChild(_searchEdit);

		_searchButton = CreateActionButton("검색");
		_searchButton.Pressed += TriggerSearch;
		searchRow.AddChild(_searchButton);

		_statusLabel = CreateTextLabel(string.Empty, fontColor: StsColors.halfTransparentCream);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_statusLabel.Visible = false;
		_leftColumn.AddChild(_statusLabel);

		_selectionLabel = CreateTextLabel(string.Empty, fontColor: StsColors.gold);
		_selectionLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_selectionLabel.Visible = false;
		_leftColumn.AddChild(_selectionLabel);

		_leftColumn.AddChild(CreateDivider(horizontal: true));

		_resultScroll = new ScrollContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
		};
		_leftColumn.AddChild(_resultScroll);

		_resultContent = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		_resultContent.AddThemeConstantOverride("separation", 6);
		_resultScroll.AddChild(_resultContent);

		_resultGrid = new HFlowContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		_resultGrid.AddThemeConstantOverride("h_separation", 6);
		_resultGrid.AddThemeConstantOverride("v_separation", 6);
		_resultContent.AddChild(_resultGrid);

		_emptyLabel = CreateTextLabel("아직 아무것도 없습니다.", fontColor: StsColors.halfTransparentCream);
		_emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_emptyLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_emptyLabel.Visible = false;
		_resultContent.AddChild(_emptyLabel);
	}

	private void BuildRightColumn()
	{
		Label title = CreateTextLabel("커스텀 핑", fontColor: StsColors.cream);
		title.HorizontalAlignment = HorizontalAlignment.Center;
		title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_rightColumn.AddChild(title);

		Label hint = CreateTextLabel("1. 콘 선택  2. 슬롯 선택", fontColor: StsColors.halfTransparentCream);
		hint.HorizontalAlignment = HorizontalAlignment.Center;
		hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_rightColumn.AddChild(hint);

		PanelContainer surfaceFrame = new()
		{
			CustomMinimumSize = new Vector2(RadialSurfaceSize, RadialSurfaceSize),
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
			SizeFlagsVertical = SizeFlags.ShrinkCenter
		};
		surfaceFrame.AddThemeStyleboxOverride("panel", GalleryShipUiStyles.CreateSurfaceStyle());
		_rightColumn.AddChild(surfaceFrame);

		Control surface = new()
		{
			CustomMinimumSize = new Vector2(RadialSurfaceSize, RadialSurfaceSize),
			MouseFilter = MouseFilterEnum.Ignore
		};
		surfaceFrame.AddChild(surface);

		Vector2 center = new(RadialSurfaceSize * 0.5f, RadialSurfaceSize * 0.5f);
		for (int index = 0; index < _slotControls.Length; index++)
		{
			float angle = Mathf.DegToRad(index * 45f);
			Vector2 offset = Vector2.Right.Rotated(angle) * RadialRadius;
			GalleryShipPingSlotControl slot = new();
			slot.Setup(index, OnSlotClicked);
			slot.Position = center + offset - Vector2.One * (RadialSlotSize * 0.5f);
			slot.CustomMinimumSize = new Vector2(RadialSlotSize, RadialSlotSize);
			slot.Size = new Vector2(RadialSlotSize, RadialSlotSize);
			surface.AddChild(slot);
			_slotControls[index] = slot;
		}

		Label centerKey = CreateTextLabel("C", fontColor: StsColors.cream);
		centerKey.HorizontalAlignment = HorizontalAlignment.Center;
		centerKey.VerticalAlignment = VerticalAlignment.Center;
		centerKey.CustomMinimumSize = new Vector2(70f, 52f);
		centerKey.Position = center - new Vector2(35f, 34f);
		centerKey.MouseFilter = MouseFilterEnum.Ignore;
		surface.AddChild(centerKey);

		Label centerLabel = CreateTextLabel("핑", fontColor: StsColors.halfTransparentCream);
		centerLabel.HorizontalAlignment = HorizontalAlignment.Center;
		centerLabel.VerticalAlignment = VerticalAlignment.Center;
		centerLabel.CustomMinimumSize = new Vector2(70f, 22f);
		centerLabel.Position = center - new Vector2(35f, -4f);
		centerLabel.MouseFilter = MouseFilterEnum.Ignore;
		surface.AddChild(centerLabel);

		Label clearHint = CreateTextLabel("선택된 콘이 없을 때 슬롯을 누르면 해제됩니다.", fontColor: StsColors.gray);
		clearHint.HorizontalAlignment = HorizontalAlignment.Center;
		clearHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		clearHint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_rightColumn.AddChild(clearHint);
	}

	private void CreateTabButton(HBoxContainer parent, EmoteTab tab, string text)
	{
		Button button = CreateActionButton(text);
		button.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		button.Pressed += () =>
		{
			if (_currentTab == tab)
			{
				return;
			}

			_currentTab = tab;
			_selectedPack = null;
			_selectedItem = null;
			RefreshTabVisuals();
			RefreshSelectionUi();
			RenderCurrentCollection();
			if (tab == EmoteTab.DcCon)
			{
				TriggerSearch();
			}
		};
		parent.AddChild(button);
		_tabButtons[tab] = button;
	}

	private void TriggerSearch()
	{
		if (_currentTab == EmoteTab.Favorites)
		{
			RenderFavorites();
			return;
		}

		_requestVersion++;
		int requestVersion = _requestVersion;
		string query = _searchEdit.Text ?? string.Empty;
		SetStatus("불러오는 중...", busy: true);
		_ = SearchAsync(query, requestVersion);
	}

	private async Task SearchAsync(string query, int requestVersion)
	{
		GalleryShipPackSearchResult result = await GalleryShipEmoteService.SearchPacksAsync(GalleryShipEmoteProvider.DcCon, query);
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		Callable.From(() => ApplySearchResult(result, requestVersion)).CallDeferred();
	}

	private void ApplySearchResult(GalleryShipPackSearchResult result, int requestVersion)
	{
		if (!GodotObject.IsInstanceValid(this) || requestVersion != _requestVersion || _currentTab != EmoteTab.DcCon)
		{
			return;
		}

		_selectedPack = null;
		_selectedItem = null;
		_currentPacks = result.Packs;
		_currentItems = Array.Empty<GalleryShipEmoteItem>();
		RefreshSelectionUi();
		SetStatus(result.ErrorMessage ?? string.Empty, busy: false);
		RenderPackResults(result.Packs, result.ErrorMessage);
	}

	private async Task LoadPackAsync(GalleryShipEmotePack pack)
	{
		_requestVersion++;
		int requestVersion = _requestVersion;
		Log.Info($"[GalleryShip] Loading pack detail: {pack.Provider}:{pack.PackId}");
		_selectedPack = pack;
		_selectedItem = null;
		_currentItems = Array.Empty<GalleryShipEmoteItem>();
		RefreshSelectionUi();
		SetStatus("세부 콘을 불러오는 중...", busy: true);
		RenderCurrentCollection();

		GalleryShipPackItemsResult result = await GalleryShipEmoteService.FetchPackItemsAsync(pack);
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		Callable.From(() => ApplyPackItemsResult(pack, result, requestVersion)).CallDeferred();
	}

	private void ApplyPackItemsResult(GalleryShipEmotePack pack, GalleryShipPackItemsResult result, int requestVersion)
	{
		if (!GodotObject.IsInstanceValid(this) || requestVersion != _requestVersion || _selectedPack?.PackId != pack.PackId)
		{
			return;
		}

		Log.Info($"[GalleryShip] Pack detail applied: {pack.Provider}:{pack.PackId}, items={result.Items.Count}, error={(result.ErrorMessage ?? "<none>")}");
		_currentItems = result.Items;
		SetStatus(result.ErrorMessage ?? string.Empty, busy: false);
		RenderPackItems(pack, result.Items, result.ErrorMessage);
	}

	private void RenderCurrentCollection()
	{
		if (_currentTab == EmoteTab.Favorites)
		{
			RenderFavorites();
			return;
		}

		if (_selectedPack == null)
		{
			RenderPackResults(_currentPacks, _searchButton.Disabled && _currentPacks.Count == 0 ? "불러오는 중..." : null);
			return;
		}

		RenderPackItems(_selectedPack, _currentItems, _searchButton.Disabled && _currentItems.Count == 0 ? "세부 콘을 불러오는 중..." : null);
	}

	internal bool TryHandleBackNavigation()
	{
		if (_selectedPack == null)
		{
			return false;
		}

		Log.Info("[GalleryShip] Returning from pack detail to pack list.");
		_requestVersion++;
		_selectedPack = null;
		_selectedItem = null;
		_currentItems = Array.Empty<GalleryShipEmoteItem>();
		SetStatus(string.Empty, busy: false);
		RefreshSelectionUi();
		RenderCurrentCollection();
		return true;
	}

	private void RenderPackResults(IReadOnlyList<GalleryShipEmotePack> packs, string? errorMessage)
	{
		_titleLabel.Text = "디시콘";
		_backButton.Visible = false;
		_searchEdit.Visible = true;
		_searchButton.Visible = true;
		ClearResultGrid();

		if (!string.IsNullOrWhiteSpace(errorMessage))
		{
			_emptyLabel.Visible = true;
			_emptyLabel.Text = errorMessage;
			RefreshResponsiveLayout();
			return;
		}

		if (packs.Count == 0)
		{
			_emptyLabel.Visible = true;
			_emptyLabel.Text = "검색 결과가 없습니다.";
			RefreshResponsiveLayout();
			return;
		}

		foreach (GalleryShipEmotePack pack in packs)
		{
			Button card = CreatePackCard(pack);
			_resultGrid.AddChild(card);
		}

		RefreshResponsiveLayout();
	}

	private Button CreatePackCard(GalleryShipEmotePack pack)
	{
		Button button = new()
		{
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			SizeFlagsVertical = SizeFlags.ShrinkBegin,
			CustomMinimumSize = new Vector2(156f, PackCardHeight),
			FocusMode = FocusModeEnum.All
		};
		GalleryShipUiStyles.ApplyCardButtonStyle(button, selected: false);

		VBoxContainer stack = new()
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		stack.SetAnchorsPreset(LayoutPreset.FullRect);
		stack.OffsetLeft = 4f;
		stack.OffsetTop = 4f;
		stack.OffsetRight = -4f;
		stack.OffsetBottom = -4f;
		stack.AddThemeConstantOverride("separation", 2);
		button.AddChild(stack);

		CenterContainer previewHolder = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, PackPreviewHeight)
		};
		stack.AddChild(previewHolder);

		TextureRect preview = new()
		{
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			CustomMinimumSize = new Vector2(PackPreviewWidth, PackPreviewHeight),
			MouseFilter = MouseFilterEnum.Ignore
		};
		previewHolder.AddChild(preview);

		Label title = CreateTextLabel(pack.Title, fontColor: StsColors.cream);
		title.HorizontalAlignment = HorizontalAlignment.Center;
		title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		title.MouseFilter = MouseFilterEnum.Ignore;
		stack.AddChild(title);

		if (!string.IsNullOrWhiteSpace(pack.SellerName))
		{
			Label seller = CreateTextLabel(pack.SellerName, fontColor: StsColors.halfTransparentCream);
			seller.HorizontalAlignment = HorizontalAlignment.Center;
			seller.MouseFilter = MouseFilterEnum.Ignore;
			stack.AddChild(seller);
		}

		button.ActionMode = BaseButton.ActionModeEnum.Press;
		button.ButtonDown += () =>
		{
			Log.Info($"[GalleryShip] Pack pressed: {pack.Provider}:{pack.PackId}");
			_ = LoadPackAsync(pack);
		};
		preview.TreeEntered += () => _ = LoadPackPreviewAsync(pack, preview);
		return button;
	}

	private async Task LoadPackPreviewAsync(GalleryShipEmotePack pack, TextureRect preview)
	{
		string? path = GalleryShipEmoteService.TryGetCachedImagePath(pack.Provider, pack.ThumbnailUrl)
			?? await GalleryShipEmoteService.EnsureLocalImageAsync(pack.Provider, pack.ThumbnailUrl, string.Empty);
		if (!GodotObject.IsInstanceValid(preview) || !preview.IsInsideTree() || string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		GalleryShipTextureAsset? textureAsset = GalleryShipEmoteService.LoadTextureAssetFromFile(path);
		if (textureAsset == null || !GodotObject.IsInstanceValid(preview) || !preview.IsInsideTree())
		{
			return;
		}

		Callable.From(() => GalleryShipAnimatedTexturePlayer.Apply(preview, textureAsset)).CallDeferred();
	}

	private void RenderPackItems(GalleryShipEmotePack pack, IReadOnlyList<GalleryShipEmoteItem> items, string? errorMessage)
	{
		_titleLabel.Text = pack.Title;
		_backButton.Visible = true;
		_searchEdit.Visible = false;
		_searchButton.Visible = false;
		ClearResultGrid();

		if (!string.IsNullOrWhiteSpace(errorMessage))
		{
			_emptyLabel.Visible = true;
			_emptyLabel.Text = errorMessage;
			RefreshResponsiveLayout();
			return;
		}

		if (items.Count == 0)
		{
			_emptyLabel.Visible = true;
			_emptyLabel.Text = "세부 항목이 없습니다.";
			RefreshResponsiveLayout();
			return;
		}

		foreach (GalleryShipEmoteItem item in items)
		{
			GalleryShipEmoteItemCard card = new();
			card.Setup(item, ToggleFavorite, IsFavorite, SelectItem, IsSelectedItem(item));
			_visibleItemCards.Add(card);
			_resultGrid.AddChild(card);
			card.QueuePreviewLoad();
		}

		RefreshResponsiveLayout();
	}

	private void RenderFavorites()
	{
		_titleLabel.Text = "즐겨찾기";
		_backButton.Visible = false;
		_searchEdit.Visible = false;
		_searchButton.Visible = false;
		ClearResultGrid();

		GalleryShipEmoteItem[] favorites = GalleryShipEmoteStore.GetFavoritesSnapshot().ToArray();
		if (favorites.Length == 0)
		{
			_emptyLabel.Visible = true;
			_emptyLabel.Text = "즐겨찾기한 콘이 없습니다.";
			RefreshResponsiveLayout();
			return;
		}

		foreach (GalleryShipEmoteItem item in favorites)
		{
			GalleryShipEmoteItemCard card = new();
			card.Setup(item, ToggleFavorite, IsFavorite, SelectItem, IsSelectedItem(item));
			_visibleItemCards.Add(card);
			_resultGrid.AddChild(card);
			card.QueuePreviewLoad();
		}

		RefreshResponsiveLayout();
	}

	private void ClearResultGrid()
	{
		foreach (GalleryShipEmoteItemCard card in _visibleItemCards)
		{
			if (GodotObject.IsInstanceValid(card))
			{
				card.QueueFree();
			}
		}

		_visibleItemCards.Clear();
		foreach (Node child in _resultGrid.GetChildren())
		{
			child.QueueFree();
		}

		if (GodotObject.IsInstanceValid(_resultScroll))
		{
			_resultScroll.ScrollVertical = 0;
		}

		_emptyLabel.Visible = false;
	}

	private void SelectItem(GalleryShipEmoteItem item)
	{
		_selectedItem = IsSelectedItem(item) ? null : item;
		Log.Info(_selectedItem == null
			? "[GalleryShip] Emote item selection cleared."
			: $"[GalleryShip] Selected emote item: {_selectedItem.Key}");
		RefreshSelectionUi();
		foreach (GalleryShipEmoteItemCard card in _visibleItemCards)
		{
			card.SetSelected(IsSelectedItemByKey(card.ItemKey));
		}
	}

	private bool IsSelectedItem(GalleryShipEmoteItem item)
	{
		return _selectedItem?.Key == item.Key;
	}

	private bool IsSelectedItemByKey(string itemKey)
	{
		return _selectedItem?.Key == itemKey;
	}

	private void RefreshSelectionUi()
	{
		_selectionLabel.Visible = false;
		_selectionLabel.Text = string.Empty;
	}

	private void ToggleFavorite(GalleryShipEmoteItem item)
	{
		GalleryShipEmoteStore.ToggleFavorite(item);
		foreach (GalleryShipEmoteItemCard card in _visibleItemCards)
		{
			card.RefreshFavoriteState();
		}

		if (_currentTab == EmoteTab.Favorites)
		{
			RenderFavorites();
		}
	}

	private bool IsFavorite(GalleryShipEmoteItem item)
	{
		return GalleryShipEmoteStore.IsFavorite(item);
	}

	private void OnSlotClicked(int index)
	{
		Log.Info($"[GalleryShip] Slot clicked: {index + 1}, selected={_selectedItem?.Key ?? "<none>"}");
		if (_selectedItem != null)
		{
			GalleryShipEmoteStore.SetSlot(index, _selectedItem);
			RefreshPingSlots();
			SetStatus($"슬롯 {index + 1}에 '{GalleryShipEmoteItemCard.GetDisplayTitle(_selectedItem)}' 장착", busy: false);
			Log.Info($"[GalleryShip] Equipped slot {index + 1} with {_selectedItem.Key}");
			_ = GalleryShipEmoteService.EnsureLocalImageAsync(_selectedItem);
			_selectedItem = null;
			RefreshSelectionUi();
			foreach (GalleryShipEmoteItemCard card in _visibleItemCards)
			{
				card.SetSelected(IsSelectedItemByKey(card.ItemKey));
			}
			return;
		}

		GalleryShipEmoteItem? existingItem = GalleryShipEmoteStore.GetSlotsSnapshot()[index];
		if (existingItem == null)
		{
			return;
		}

		GalleryShipEmoteStore.ClearSlot(index);
		RefreshPingSlots();
		SetStatus($"슬롯 {index + 1} 해제", busy: false);
		Log.Info($"[GalleryShip] Cleared slot {index + 1}");
	}

	private void RefreshPingSlots()
	{
		GalleryShipEmoteItem?[] slots = GalleryShipEmoteStore.GetSlotsSnapshot();
		for (int index = 0; index < _slotControls.Length; index++)
		{
			_slotControls[index]?.SetItem(slots[index]);
		}
	}

	private void RefreshTabVisuals()
	{
		foreach ((EmoteTab tab, Button button) in _tabButtons)
		{
			GalleryShipUiStyles.ApplyActionButtonStyle(button, tab == _currentTab);
		}
	}

	private void RefreshResponsiveLayout()
	{
		if (!GodotObject.IsInstanceValid(_resultGrid))
		{
			return;
		}

		bool showingPackList = _currentTab == EmoteTab.DcCon && _selectedPack == null;
		int spacing = showingPackList ? 8 : 6;
		_resultGrid.Alignment = FlowContainer.AlignmentMode.Begin;
		_resultGrid.CustomMinimumSize = Vector2.Zero;
		_resultGrid.AddThemeConstantOverride("h_separation", spacing);
		_resultGrid.AddThemeConstantOverride("v_separation", spacing);
		if (_visibleItemCards.Count == 0)
		{
			return;
		}

		float availableWidth = _resultScroll.Size.X;
		ScrollBar? verticalBar = _resultScroll.GetVScrollBar();
		if (verticalBar != null && verticalBar.Visible)
		{
			availableWidth -= verticalBar.Size.X + spacing;
		}

		int columns = Math.Max(1, (int)Mathf.Floor((availableWidth + spacing) / (ItemCardWidth + spacing)));
		float cardWidth = Mathf.Max(96f, Mathf.Floor((availableWidth - (spacing * (columns - 1))) / columns));
		foreach (GalleryShipEmoteItemCard card in _visibleItemCards)
		{
			card.SetCardWidth(cardWidth);
		}
	}

	private void RefreshPanelSize()
	{
		if (!GodotObject.IsInstanceValid(_root))
		{
			return;
		}

		Control? parent = GetParentOrNull<Control>();
		float width = parent?.Size.X ?? 1120f;
		float height = Mathf.Max(MinimumPanelHeight, (parent?.Size.Y ?? 720f) - ExternalScrollPadding);
		CustomMinimumSize = new Vector2(width, height);
		Size = CustomMinimumSize;
	}

	private void SetStatus(string text, bool busy)
	{
		_searchButton.Disabled = busy;
		_statusLabel.Text = text;
		_statusLabel.Visible = !string.IsNullOrWhiteSpace(text);
	}

	private static Label CreateTextLabel(string text, Color fontColor)
	{
		Label label = new()
		{
			Text = text,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			VerticalAlignment = VerticalAlignment.Center
		};
		label.AddThemeColorOverride("font_color", fontColor);
		GalleryShipUiStyles.ApplyLabelFont(label);
		return label;
	}

	private static Control CreateDivider(bool horizontal)
	{
		ColorRect divider = new()
		{
			Color = StsColors.quarterTransparentWhite,
			MouseFilter = MouseFilterEnum.Ignore
		};
		divider.CustomMinimumSize = horizontal ? new Vector2(0f, 1f) : new Vector2(1f, 0f);
		divider.SizeFlagsHorizontal = horizontal ? SizeFlags.ExpandFill : SizeFlags.ShrinkCenter;
		divider.SizeFlagsVertical = horizontal ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill;
		return divider;
	}

	private static Button CreateActionButton(string text)
	{
		Button button = new()
		{
			Text = text,
			FocusMode = FocusModeEnum.All,
			CustomMinimumSize = new Vector2(90f, 38f)
		};
		GalleryShipUiStyles.ApplyActionButtonStyle(button, selected: false);
		return button;
	}
}

internal static class GalleryShipUiStyles
{
	internal static StyleBoxFlat CreatePanelStyle(Color background, Color border, float marginX = 10f, float marginY = 8f)
	{
		return new StyleBoxFlat
		{
			BgColor = background,
			BorderColor = border,
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusBottomLeft = 4,
			CornerRadiusBottomRight = 4,
			CornerRadiusTopLeft = 4,
			CornerRadiusTopRight = 4,
			ContentMarginLeft = marginX,
			ContentMarginTop = marginY,
			ContentMarginRight = marginX,
			ContentMarginBottom = marginY
		};
	}

	internal static StyleBoxFlat CreateSurfaceStyle()
	{
		return CreatePanelStyle(new Color(0f, 0f, 0f, 0.4f), StsColors.quarterTransparentWhite);
	}

	internal static void ApplyLabelFont(Control control)
	{
		control.ApplyLocaleFontSubstitution(FontType.Regular, new StringName("font"));
	}

	internal static void ApplyInputStyle(LineEdit lineEdit)
	{
		lineEdit.AddThemeColorOverride("font_color", StsColors.cream);
		lineEdit.AddThemeColorOverride("font_placeholder_color", StsColors.halfTransparentCream);
		lineEdit.AddThemeStyleboxOverride("normal", CreatePanelStyle(new Color(0f, 0f, 0f, 0.35f), StsColors.quarterTransparentWhite));
		lineEdit.AddThemeStyleboxOverride("focus", CreatePanelStyle(new Color(0f, 0f, 0f, 0.45f), StsColors.halfTransparentCream));
		lineEdit.AddThemeStyleboxOverride("read_only", CreatePanelStyle(new Color(0f, 0f, 0f, 0.25f), StsColors.quarterTransparentWhite));
		lineEdit.ApplyLocaleFontSubstitution(FontType.Regular, new StringName("font"));
	}

	internal static void ApplyActionButtonStyle(Button button, bool selected)
	{
		Color border = selected ? StsColors.gold : StsColors.quarterTransparentWhite;
		Color background = selected ? new Color(0.18f, 0.14f, 0.04f, 0.62f) : new Color(0f, 0f, 0f, 0.35f);
		button.AddThemeColorOverride("font_color", selected ? StsColors.gold : StsColors.cream);
		button.AddThemeColorOverride("font_hover_color", StsColors.cream);
		button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
		button.AddThemeStyleboxOverride("normal", CreatePanelStyle(background, border));
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(new Color(0f, 0f, 0f, 0.48f), StsColors.halfTransparentCream));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(new Color(0f, 0f, 0f, 0.6f), StsColors.gold));
		button.AddThemeStyleboxOverride("disabled", CreatePanelStyle(new Color(0f, 0f, 0f, 0.22f), StsColors.quarterTransparentWhite));
		ApplyLabelFont(button);
	}

	internal static void ApplyCardButtonStyle(Button button, bool selected)
	{
		Color border = selected ? StsColors.gold : StsColors.quarterTransparentWhite;
		button.AddThemeColorOverride("font_color", StsColors.cream);
		button.AddThemeStyleboxOverride("normal", CreatePanelStyle(new Color(0f, 0f, 0f, 0.32f), border, 4f, 4f));
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(new Color(0f, 0f, 0f, 0.45f), StsColors.halfTransparentCream, 4f, 4f));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(new Color(0f, 0f, 0f, 0.55f), StsColors.gold, 4f, 4f));
		ApplyLabelFont(button);
	}

	internal static void ApplyItemCardStyle(Button button, bool selected)
	{
		Color border = selected ? StsColors.gold : StsColors.quarterTransparentWhite;
		StyleBoxFlat normal = CreatePanelStyle(new Color(0f, 0f, 0f, selected ? 0.48f : 0.3f), border, 4f, 4f);
		button.AddThemeColorOverride("font_color", StsColors.cream);
		button.AddThemeColorOverride("font_hover_color", StsColors.cream);
		button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(new Color(0f, 0f, 0f, selected ? 0.54f : 0.42f), StsColors.halfTransparentCream, 4f, 4f));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(new Color(0f, 0f, 0f, 0.58f), StsColors.gold, 4f, 4f));
		button.AddThemeStyleboxOverride("focus", normal);
		ApplyLabelFont(button);
	}

	internal static void ApplySlotStyle(Button button, bool filled)
	{
		Color border = filled ? StsColors.halfTransparentCream : StsColors.quarterTransparentWhite;
		StyleBoxFlat normal = CreatePanelStyle(new Color(0f, 0f, 0f, 0.38f), border, 6f, 6f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(new Color(0f, 0f, 0f, 0.46f), StsColors.halfTransparentCream, 6f, 6f));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(new Color(0f, 0f, 0f, 0.52f), StsColors.gold, 6f, 6f));
		button.AddThemeStyleboxOverride("focus", normal);
		ApplyLabelFont(button);
	}
}

internal sealed class GalleryShipEmoteItemCard : Button
{
	private GalleryShipEmoteItem _item = null!;
	private Action<GalleryShipEmoteItem>? _favoriteToggle;
	private Func<GalleryShipEmoteItem, bool>? _isFavorite;
	private Action<GalleryShipEmoteItem>? _selectionRequested;
	private TextureRect _preview = null!;
	private Button _favoriteButton = null!;
	private Label _titleLabel = null!;
	private bool _selected;
	private bool _built;
	private int _previewVersion;

	internal string ItemKey => _item.Key;

	internal void Setup(
		GalleryShipEmoteItem item,
		Action<GalleryShipEmoteItem> favoriteToggle,
		Func<GalleryShipEmoteItem, bool> isFavorite,
		Action<GalleryShipEmoteItem> selectionRequested,
		bool selected)
	{
		_item = item;
		_favoriteToggle = favoriteToggle;
		_isFavorite = isFavorite;
		_selectionRequested = selectionRequested;
		_selected = selected;
		MouseFilter = MouseFilterEnum.Stop;
		FocusMode = FocusModeEnum.All;
		ActionMode = ActionModeEnum.Press;
		SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		SizeFlagsVertical = SizeFlags.ShrinkBegin;
		ClipContents = true;
		CustomMinimumSize = new Vector2(GalleryShipEmoteSettingsPanel.ItemCardWidth, GalleryShipEmoteSettingsPanel.ItemCardHeight);
		if (!_built)
		{
			BuildUi();
			_built = true;
		}

		GalleryShipAnimatedTexturePlayer.Clear(_preview);
		_preview.Texture = null;
		_titleLabel.Text = GetDisplayTitle(item);
		TooltipText = string.IsNullOrWhiteSpace(item.Title) ? _titleLabel.Text : item.Title;
		RefreshFavoriteState();
		RefreshSelectionState();
		_previewVersion++;
	}

	internal static string GetDisplayTitle(GalleryShipEmoteItem item)
	{
		string title = item.Title?.Trim() ?? string.Empty;
		return string.IsNullOrWhiteSpace(title) ? item.ItemId : title;
	}

	internal void QueuePreviewLoad()
	{
		int requestVersion = _previewVersion;
		Callable.From(() =>
		{
			_ = LoadPreviewAsync(requestVersion);
		}).CallDeferred();
	}

	internal void RefreshFavoriteState()
	{
		if (_isFavorite == null)
		{
			return;
		}

		_favoriteButton.Text = _isFavorite(_item) ? "★" : "☆";
	}

	internal void RefreshSelectionState()
	{
		GalleryShipUiStyles.ApplyItemCardStyle(this, _selected);
	}

	internal void SetSelected(bool selected)
	{
		_selected = selected;
		RefreshSelectionState();
	}

	private void OnButtonDown()
	{
		if (_selectionRequested == null)
		{
			return;
		}

		Log.Info($"[GalleryShip] Item button down: {_item.Key}");
		_selectionRequested(_item);
	}

	private void BuildUi()
	{
		Text = string.Empty;
		GalleryShipUiStyles.ApplyItemCardStyle(this, selected: false);
		ButtonDown += OnButtonDown;

		VBoxContainer root = new()
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		root.SetAnchorsPreset(LayoutPreset.FullRect);
		root.OffsetLeft = 4f;
		root.OffsetTop = 4f;
		root.OffsetRight = -4f;
		root.OffsetBottom = -4f;
		root.AddThemeConstantOverride("separation", 4);
		AddChild(root);

		HBoxContainer topRow = new()
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		topRow.Alignment = BoxContainer.AlignmentMode.Begin;
		topRow.AddThemeConstantOverride("separation", 4);
		root.AddChild(topRow);

		MarginContainer titleContainer = new()
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		titleContainer.AddThemeConstantOverride("margin_left", 6);
		topRow.AddChild(titleContainer);

		_titleLabel = new Label
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			MouseFilter = MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		_titleLabel.AddThemeColorOverride("font_color", StsColors.cream);
		GalleryShipUiStyles.ApplyLabelFont(_titleLabel);
		titleContainer.AddChild(_titleLabel);

		_favoriteButton = new Button
		{
			Text = "☆",
			CustomMinimumSize = new Vector2(34f, 28f)
		};
		GalleryShipUiStyles.ApplyActionButtonStyle(_favoriteButton, selected: false);
		_favoriteButton.Pressed += () =>
		{
			_favoriteToggle?.Invoke(_item);
			RefreshFavoriteState();
		};
		topRow.AddChild(_favoriteButton);

		CenterContainer previewHolder = new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, GalleryShipEmoteSettingsPanel.ItemPreviewHeight)
		};
		root.AddChild(previewHolder);

		_preview = new TextureRect
		{
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			CustomMinimumSize = new Vector2(GalleryShipEmoteSettingsPanel.ItemPreviewWidth, GalleryShipEmoteSettingsPanel.ItemPreviewHeight),
			MouseFilter = MouseFilterEnum.Ignore
		};
		previewHolder.AddChild(_preview);
	}

	internal void SetCardWidth(float width)
	{
		CustomMinimumSize = new Vector2(width, GalleryShipEmoteSettingsPanel.ItemCardHeight);
	}

	private async Task LoadPreviewAsync(int requestVersion)
	{
		string? path = GalleryShipEmoteService.TryGetCachedImagePath(_item.Provider, _item.ImageUrl)
			?? await GalleryShipEmoteService.EnsureLocalImageAsync(_item);
		if (requestVersion != _previewVersion || !GodotObject.IsInstanceValid(_preview) || !_preview.IsInsideTree() || string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		GalleryShipTextureAsset? textureAsset = GalleryShipEmoteService.LoadTextureAssetFromFile(path);
		if (textureAsset == null || requestVersion != _previewVersion || !GodotObject.IsInstanceValid(_preview) || !_preview.IsInsideTree())
		{
			return;
		}

		Callable.From(() => GalleryShipAnimatedTexturePlayer.Apply(_preview, textureAsset)).CallDeferred();
	}
}

internal sealed class GalleryShipPingSlotControl : Button
{
	private int _index;
	private Action<int>? _clickHandler;
	private TextureRect _preview = null!;
	private Label _label = null!;
	private GalleryShipEmoteItem? _item;
	private bool _built;
	private int _previewVersion;

	internal void Setup(int index, Action<int> clickHandler)
	{
		_index = index;
		_clickHandler = clickHandler;
		MouseFilter = MouseFilterEnum.Stop;
		FocusMode = FocusModeEnum.All;
		ActionMode = ActionModeEnum.Press;
		CustomMinimumSize = new Vector2(74f, 74f);
		Size = new Vector2(74f, 74f);
		if (!_built)
		{
			BuildUi();
			_built = true;
		}

		SetItem(null);
	}

	internal void SetItem(GalleryShipEmoteItem? item)
	{
		_item = item;
		TooltipText = item == null ? $"슬롯 {_index + 1}" : GalleryShipEmoteItemCard.GetDisplayTitle(item);
		GalleryShipUiStyles.ApplySlotStyle(this, item != null);
		GalleryShipAnimatedTexturePlayer.Clear(_preview);
		_preview.Texture = null;
		_label.Text = item == null ? (_index + 1).ToString() : string.Empty;
		_previewVersion++;
		if (item != null)
		{
			int requestVersion = _previewVersion;
			Callable.From(() =>
			{
				_ = LoadPreviewAsync(item, requestVersion);
			}).CallDeferred();
		}
	}

	private void OnButtonDown()
	{
		if (_clickHandler == null)
		{
			return;
		}

		Log.Info($"[GalleryShip] Slot button down: {_index + 1}, selected={_item?.Key ?? "<empty>"}");
		_clickHandler(_index);
	}

	private void BuildUi()
	{
		Text = string.Empty;
		GalleryShipUiStyles.ApplySlotStyle(this, filled: false);
		ButtonDown += OnButtonDown;

		_preview = new TextureRect
		{
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			CustomMinimumSize = new Vector2(56f, 56f),
			MouseFilter = MouseFilterEnum.Ignore
		};
		_preview.Position = new Vector2(9f, 8f);
		AddChild(_preview);

		_label = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			CustomMinimumSize = new Vector2(44f, 20f),
			Position = new Vector2(15f, 26f),
			MouseFilter = MouseFilterEnum.Ignore
		};
		_label.AddThemeColorOverride("font_color", StsColors.halfTransparentCream);
		GalleryShipUiStyles.ApplyLabelFont(_label);
		AddChild(_label);
	}

	private async Task LoadPreviewAsync(GalleryShipEmoteItem item, int requestVersion)
	{
		string? path = GalleryShipEmoteService.TryGetCachedImagePath(item.Provider, item.ImageUrl)
			?? await GalleryShipEmoteService.EnsureLocalImageAsync(item);
		if (requestVersion != _previewVersion || !GodotObject.IsInstanceValid(_preview) || !_preview.IsInsideTree() || _item?.Key != item.Key || string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		GalleryShipTextureAsset? textureAsset = GalleryShipEmoteService.LoadTextureAssetFromFile(path);
		if (textureAsset == null || requestVersion != _previewVersion || !GodotObject.IsInstanceValid(_preview) || !_preview.IsInsideTree() || _item?.Key != item.Key)
		{
			return;
		}

		Callable.From(() => GalleryShipAnimatedTexturePlayer.Apply(_preview, textureAsset)).CallDeferred();
	}
}
