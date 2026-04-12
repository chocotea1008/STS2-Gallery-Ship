using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;

namespace GalleryShip;

internal enum GalleryShipEmoteProvider : byte
{
	DcCon = 0,
	Arca = 1
}

internal sealed record class GalleryShipEmotePack
{
	public GalleryShipEmoteProvider Provider { get; init; }

	public string PackId { get; init; } = string.Empty;

	public string Title { get; init; } = string.Empty;

	public string SellerName { get; init; } = string.Empty;

	public string ThumbnailUrl { get; init; } = string.Empty;
}

internal sealed record class GalleryShipEmoteItem
{
	public GalleryShipEmoteProvider Provider { get; init; }

	public string PackId { get; init; } = string.Empty;

	public string PackTitle { get; init; } = string.Empty;

	public string ItemId { get; init; } = string.Empty;

	public string Title { get; init; } = string.Empty;

	public string ImageUrl { get; init; } = string.Empty;

	public string FileExtension { get; init; } = string.Empty;

	public string SellerName { get; init; } = string.Empty;

	public string Key => $"{Provider}:{PackId}:{ItemId}";
}

internal sealed class GalleryShipEmoteStoreData
{
	public List<GalleryShipEmoteItem> Favorites { get; set; } = new();

	public List<GalleryShipEmoteItem?> Slots { get; set; } = new();
}

internal static class GalleryShipEmoteStore
{
	internal const int SlotCount = 8;

	private static readonly object Sync = new();
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	private static GalleryShipEmoteStoreData? _data;

	private static string StorePath => ProjectSettings.GlobalizePath("user://galleryship_emotes.json");

	internal static IReadOnlyList<GalleryShipEmoteItem> GetFavoritesSnapshot()
	{
		lock (Sync)
		{
			EnsureLoaded();
			return _data!.Favorites.Select(CloneItem).ToArray();
		}
	}

	internal static GalleryShipEmoteItem?[] GetSlotsSnapshot()
	{
		lock (Sync)
		{
			EnsureLoaded();
			return _data!.Slots.Select(static item => item == null ? null : CloneItem(item)).ToArray();
		}
	}

	internal static bool IsFavorite(GalleryShipEmoteItem item)
	{
		lock (Sync)
		{
			EnsureLoaded();
			return _data!.Favorites.Any(existing => string.Equals(existing.Key, item.Key, StringComparison.Ordinal));
		}
	}

	internal static void ToggleFavorite(GalleryShipEmoteItem item)
	{
		lock (Sync)
		{
			EnsureLoaded();
			int index = _data!.Favorites.FindIndex(existing => string.Equals(existing.Key, item.Key, StringComparison.Ordinal));
			if (index >= 0)
			{
				_data.Favorites.RemoveAt(index);
			}
			else
			{
				_data.Favorites.Add(CloneItem(item));
				_data.Favorites = _data.Favorites
					.GroupBy(static favorite => favorite.Key, StringComparer.Ordinal)
					.Select(static group => group.First())
					.OrderBy(static favorite => favorite.PackTitle, StringComparer.CurrentCultureIgnoreCase)
					.ThenBy(static favorite => favorite.Title, StringComparer.CurrentCultureIgnoreCase)
					.ToList();
			}

			SaveLocked();
		}
	}

	internal static void SetSlot(int index, GalleryShipEmoteItem item)
	{
		if (index < 0 || index >= SlotCount)
		{
			return;
		}

		lock (Sync)
		{
			EnsureLoaded();
			_data!.Slots[index] = CloneItem(item);
			SaveLocked();
		}
	}

	internal static void ClearSlot(int index)
	{
		if (index < 0 || index >= SlotCount)
		{
			return;
		}

		lock (Sync)
		{
			EnsureLoaded();
			_data!.Slots[index] = null;
			SaveLocked();
		}
	}

	internal static bool HasAnyEquippedSlot()
	{
		lock (Sync)
		{
			EnsureLoaded();
			return _data!.Slots.Any(static item => item != null);
		}
	}

	private static void EnsureLoaded()
	{
		if (_data != null)
		{
			return;
		}

		try
		{
			if (File.Exists(StorePath))
			{
				string json = File.ReadAllText(StorePath, Encoding.UTF8);
				_data = JsonSerializer.Deserialize<GalleryShipEmoteStoreData>(json, JsonOptions) ?? new GalleryShipEmoteStoreData();
			}
			else
			{
				_data = new GalleryShipEmoteStoreData();
			}
		}
		catch
		{
			_data = new GalleryShipEmoteStoreData();
		}

		NormalizeData();
	}

	private static void NormalizeData()
	{
		_data ??= new GalleryShipEmoteStoreData();
		_data.Favorites = _data.Favorites
			.Where(static item => item != null && !string.IsNullOrWhiteSpace(item.ImageUrl))
			.GroupBy(static item => item.Key, StringComparer.Ordinal)
			.Select(static group => CloneItem(group.First()))
			.ToList();

		_data.Slots = _data.Slots
			.Where(static _ => true)
			.Select(static item => item == null || string.IsNullOrWhiteSpace(item.ImageUrl) ? null : CloneItem(item))
			.Take(SlotCount)
			.ToList();

		while (_data.Slots.Count < SlotCount)
		{
			_data.Slots.Add(null);
		}
	}

	private static void SaveLocked()
	{
		try
		{
			string? directory = Path.GetDirectoryName(StorePath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			string json = JsonSerializer.Serialize(_data, JsonOptions);
			File.WriteAllText(StorePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
		}
	}

	private static GalleryShipEmoteItem CloneItem(GalleryShipEmoteItem item)
	{
		return item with { };
	}
}
