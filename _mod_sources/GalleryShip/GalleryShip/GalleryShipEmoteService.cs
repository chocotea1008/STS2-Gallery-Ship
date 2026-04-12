using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Color = Godot.Color;
using HttpClient = System.Net.Http.HttpClient;
using Image = Godot.Image;

namespace GalleryShip;

internal sealed class GalleryShipPackSearchResult
{
	public IReadOnlyList<GalleryShipEmotePack> Packs { get; init; } = Array.Empty<GalleryShipEmotePack>();

	public string? ErrorMessage { get; init; }
}

internal sealed class GalleryShipPackItemsResult
{
	public IReadOnlyList<GalleryShipEmoteItem> Items { get; init; } = Array.Empty<GalleryShipEmoteItem>();

	public string? ErrorMessage { get; init; }
}

internal sealed class GalleryShipTextureAsset
{
	public required Texture2D Texture { get; init; }

	public IReadOnlyList<Texture2D> Frames { get; init; } = Array.Empty<Texture2D>();

	public IReadOnlyList<double> FrameDurations { get; init; } = Array.Empty<double>();

	public bool IsAnimated => Frames.Count > 1 && FrameDurations.Count == Frames.Count;
}

internal static class GalleryShipEmoteService
{
	private const string DcHomeUrl = "https://dccon.dcinside.com/";
	private const string DcBaseUrl = "https://dccon.dcinside.com";
	private const string DcPackageDetailUrl = "https://dccon.dcinside.com/index/package_detail";
	private const string DcImageBaseUrl = "https://dcimg5.dcinside.com/dccon.php?no=";
	private const string ArcaEmoteUrl = "https://arca.live/e";
	private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";
	private const string ImageCacheVersion = "v5";

	private static readonly Regex DcPackRegex = new(
		"<li\\s+class=\"div_package[^>]*\"\\s+package_idx=\"(?<id>\\d+)\"[^>]*>.*?<img[^>]*class=\"thumb_img\"[^>]*src=\"(?<img>[^\"]+)\"[^>]*>.*?<strong[^>]*class=\"dcon_name\"[^>]*>(?<title>.*?)</strong>.*?<span[^>]*class=\"dcon_seller\"[^>]*>(?<seller>.*?)</span>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly SemaphoreSlim ImageDownloadGate = new(3, 3);
	private static readonly CookieContainer DcCookies = new();
	private static readonly CookieContainer ArcaCookies = new();
	private static readonly HttpClient DcClient = CreateClient(DcCookies);
	private static readonly HttpClient ArcaClient = CreateClient(ArcaCookies);
	private static readonly ConcurrentDictionary<string, Task<string?>> InflightImageTasks = new(StringComparer.Ordinal);
	private static readonly object TextureCacheSync = new();
	private static readonly Dictionary<string, GalleryShipTextureAsset> TextureAssetCache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly uint[] PngCrcTable = CreatePngCrcTable();

	internal static async Task<GalleryShipPackSearchResult> SearchPacksAsync(GalleryShipEmoteProvider provider, string query)
	{
		return provider switch
		{
			GalleryShipEmoteProvider.DcCon => await SearchDcPacksAsync(query),
			GalleryShipEmoteProvider.Arca => await SearchArcaPacksAsync(query),
			_ => new GalleryShipPackSearchResult()
		};
	}

	internal static async Task<GalleryShipPackItemsResult> FetchPackItemsAsync(GalleryShipEmotePack pack)
	{
		return pack.Provider switch
		{
			GalleryShipEmoteProvider.DcCon => await FetchDcPackItemsAsync(pack),
			GalleryShipEmoteProvider.Arca => await FetchArcaPackItemsAsync(pack),
			_ => new GalleryShipPackItemsResult()
		};
	}

	internal static async Task<string?> EnsureLocalImageAsync(GalleryShipEmoteItem item)
	{
		return await EnsureLocalImageAsync(item.Provider, item.ImageUrl, item.FileExtension);
	}

	internal static string? TryGetCachedImagePath(GalleryShipEmoteProvider provider, string imageUrl)
	{
		if (string.IsNullOrWhiteSpace(imageUrl))
		{
			return null;
		}

		string cacheDirectory = ProjectSettings.GlobalizePath($"user://galleryship_emote_cache/{provider.ToString().ToLowerInvariant()}");
		if (!Directory.Exists(cacheDirectory))
		{
			return null;
		}

		string hash = ComputeImageHash(imageUrl);
		foreach (string path in Directory.GetFiles(cacheDirectory, hash + ".*"))
		{
			FileInfo info = new(path);
			if (info.Length > 0 && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
			{
				return path;
			}
		}

		return null;
	}

	internal static async Task<string?> EnsureLocalImageAsync(GalleryShipEmoteProvider provider, string imageUrl, string extensionHint)
	{
		if (string.IsNullOrWhiteSpace(imageUrl))
		{
			return null;
		}

		string cacheDirectory = ProjectSettings.GlobalizePath($"user://galleryship_emote_cache/{provider.ToString().ToLowerInvariant()}");
		Directory.CreateDirectory(cacheDirectory);

		string hash = ComputeImageHash(imageUrl);
		string? cachedPath = TryGetCachedImagePath(provider, imageUrl);
		if (!string.IsNullOrWhiteSpace(cachedPath))
		{
			return cachedPath;
		}

		string taskKey = provider + ":" + hash;
		Task<string?> task = InflightImageTasks.GetOrAdd(taskKey, _ => DownloadAndCacheImageAsync(provider, imageUrl, extensionHint, cacheDirectory, hash));
		try
		{
			return await task;
		}
		finally
		{
			if (task.IsCompleted)
			{
				InflightImageTasks.TryRemove(taskKey, out _);
			}
		}
	}

	private static async Task<string?> DownloadAndCacheImageAsync(GalleryShipEmoteProvider provider, string imageUrl, string extensionHint, string cacheDirectory, string hash)
	{
		string? cachedPath = TryGetCachedImagePath(provider, imageUrl);
		if (!string.IsNullOrWhiteSpace(cachedPath))
		{
			return cachedPath;
		}

		await ImageDownloadGate.WaitAsync();
		try
		{
			cachedPath = TryGetCachedImagePath(provider, imageUrl);
			if (!string.IsNullOrWhiteSpace(cachedPath))
			{
				return cachedPath;
			}

			using HttpResponseMessage response = await DownloadImageAsync(provider, imageUrl);
			if (!response.IsSuccessStatusCode)
			{
				Log.Warn($"[GalleryShip] Emote image download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({provider}) {imageUrl}");
				return null;
			}

			byte[] bytes = await response.Content.ReadAsByteArrayAsync();
			string extension = DetermineImageExtension(response.Content.Headers, extensionHint, imageUrl);
			string finalPath = Path.Combine(cacheDirectory, hash + "." + extension);
			string tempFinalPath = finalPath + ".tmp";
			await File.WriteAllBytesAsync(tempFinalPath, bytes);
			File.Move(tempFinalPath, finalPath, overwrite: true);
			return finalPath;
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to cache emote image: " + ex);
			return null;
		}
		finally
		{
			ImageDownloadGate.Release();
		}
	}

	internal static Texture2D? LoadTextureFromFile(string? path)
	{
		return LoadTextureAssetFromFile(path)?.Texture;
	}

	internal static GalleryShipTextureAsset? LoadTextureAssetFromFile(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return null;
		}

		lock (TextureCacheSync)
		{
			if (TextureAssetCache.TryGetValue(path, out GalleryShipTextureAsset? cached) && GodotObject.IsInstanceValid(cached.Texture))
			{
				return cached;
			}
		}

		try
		{
			GalleryShipTextureAsset textureAsset = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase)
				? LoadGifTextureAsset(path)
				: LoadStaticTextureAsset(path);
			lock (TextureCacheSync)
			{
				TextureAssetCache[path] = textureAsset;
			}

			return textureAsset;
		}
		catch (Exception ex)
		{
			TryDeleteBrokenCacheFile(path);
			Log.Warn("[GalleryShip] Failed to load emote texture: " + ex.Message);
			return null;
		}
	}

	private static GalleryShipTextureAsset LoadStaticTextureAsset(string path)
	{
		Image image = Image.LoadFromFile(path);
		if (image.IsEmpty())
		{
			throw new InvalidDataException("Image file is empty.");
		}

		Texture2D texture = CreateTextureFromImage(image);
		return new GalleryShipTextureAsset
		{
			Texture = texture,
			Frames = new[] { texture },
			FrameDurations = new[] { 1d }
		};
	}

	private static GalleryShipTextureAsset LoadGifTextureAsset(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		(int width, int height, IReadOnlyList<GifFrameData> frames) = DecodeGifAnimation(bytes);
		if (frames.Count == 0)
		{
			throw new InvalidDataException("GIF has no frames.");
		}

		List<Texture2D> textures = new(frames.Count);
		List<double> durations = new(frames.Count);
		foreach (GifFrameData frame in frames)
		{
			textures.Add(CreateTextureFromPixels(width, height, frame.Pixels));
			durations.Add(frame.DurationSeconds);
		}

		Log.Info($"[GalleryShip] Loaded animated GIF '{Path.GetFileName(path)}' with {textures.Count} frames, total {durations.Sum():0.00}s");

		return new GalleryShipTextureAsset
		{
			Texture = textures[0],
			Frames = textures,
			FrameDurations = durations
		};
	}

	private static Texture2D CreateTextureFromImage(Image image)
	{
		if (!image.HasMipmaps())
		{
			image.GenerateMipmaps();
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static Texture2D CreateTextureFromPixels(int width, int height, byte[] pixels)
	{
		Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
		return CreateTextureFromImage(image);
	}

	private static async Task<GalleryShipPackSearchResult> SearchDcPacksAsync(string query)
	{
		try
		{
			await EnsureDcSessionAsync();
			List<GalleryShipEmotePack> packs = new();
			HashSet<string> seen = new(StringComparer.Ordinal);
			string escapedQuery = Uri.EscapeDataString(query.Trim());
			string[] paths = string.IsNullOrWhiteSpace(escapedQuery)
				? new[] { "/hot/1" }
				: new[] { "/hot/1/title/" + escapedQuery, "/new/1/title/" + escapedQuery };

			foreach (string path in paths)
			{
				using HttpRequestMessage request = CreateDcRequest(HttpMethod.Get, DcBaseUrl + path);
				using HttpResponseMessage response = await DcClient.SendAsync(request);
				response.EnsureSuccessStatusCode();
				string html = await response.Content.ReadAsStringAsync();
				foreach (GalleryShipEmotePack pack in ParseDcPacks(html))
				{
					if (seen.Add(pack.PackId))
					{
						packs.Add(pack);
					}
				}
			}

			return new GalleryShipPackSearchResult
			{
				Packs = packs
					.OrderBy(static pack => pack.Title, StringComparer.CurrentCultureIgnoreCase)
					.Take(36)
					.ToArray()
			};
		}
		catch
		{
			return new GalleryShipPackSearchResult
			{
				ErrorMessage = "디시콘을 불러오지 못했습니다. 잠시 후 다시 시도해 주세요."
			};
		}
	}

	private static IEnumerable<GalleryShipEmotePack> ParseDcPacks(string html)
	{
		foreach (Match match in DcPackRegex.Matches(html))
		{
			string id = match.Groups["id"].Value.Trim();
			string title = CleanHtml(match.Groups["title"].Value);
			string seller = CleanHtml(match.Groups["seller"].Value);
			string thumbnailUrl = NormalizeUrl(match.Groups["img"].Value);
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(thumbnailUrl))
			{
				continue;
			}

			yield return new GalleryShipEmotePack
			{
				Provider = GalleryShipEmoteProvider.DcCon,
				PackId = id,
				Title = string.IsNullOrWhiteSpace(title) ? "디시콘" : title,
				SellerName = seller,
				ThumbnailUrl = thumbnailUrl
			};
		}
	}

	private static async Task<GalleryShipPackItemsResult> FetchDcPackItemsAsync(GalleryShipEmotePack pack)
	{
		try
		{
			await EnsureDcSessionAsync();
			string csrf = GetDcCsrfToken();
			if (string.IsNullOrWhiteSpace(csrf))
			{
				throw new InvalidOperationException("Missing DC csrf cookie.");
			}

			using HttpRequestMessage request = CreateDcRequest(HttpMethod.Post, DcPackageDetailUrl);
			request.Headers.Referrer = new Uri(DcHomeUrl);
			request.Headers.TryAddWithoutValidation("Origin", DcBaseUrl);
			request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
			request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["ci_t"] = csrf,
				["package_idx"] = pack.PackId
			});

			using HttpResponseMessage response = await DcClient.SendAsync(request);
			response.EnsureSuccessStatusCode();
			string payload = await response.Content.ReadAsStringAsync();
			if (string.IsNullOrWhiteSpace(payload))
			{
				throw new InvalidOperationException("DC package detail payload is empty.");
			}

			using JsonDocument document = JsonDocument.Parse(payload);
			JsonElement root = document.RootElement;
			JsonElement info = root.GetProperty("info");
			string packTitle = info.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? pack.Title : pack.Title;
			string sellerName = info.TryGetProperty("seller_name", out JsonElement sellerElement) ? sellerElement.GetString() ?? pack.SellerName : pack.SellerName;
			List<GalleryShipEmoteItem> items = new();
			foreach (JsonElement detail in root.GetProperty("detail").EnumerateArray())
			{
				string itemId = detail.TryGetProperty("idx", out JsonElement itemIdElement) ? itemIdElement.GetString() ?? string.Empty : string.Empty;
				string itemTitle = detail.TryGetProperty("title", out JsonElement itemTitleElement) ? itemTitleElement.GetString() ?? itemId : itemId;
				string path = detail.TryGetProperty("path", out JsonElement pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty;
				string extension = detail.TryGetProperty("ext", out JsonElement extElement) ? extElement.GetString() ?? string.Empty : string.Empty;
				if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(path))
				{
					continue;
				}

				string normalizedTitle = string.IsNullOrWhiteSpace(itemTitle) ? string.Empty : itemTitle.Trim();
				if (normalizedTitle.Length <= 1)
				{
					normalizedTitle = (items.Count + 1).ToString(CultureInfo.InvariantCulture);
				}

				items.Add(new GalleryShipEmoteItem
				{
					Provider = GalleryShipEmoteProvider.DcCon,
					PackId = pack.PackId,
					PackTitle = packTitle,
					ItemId = itemId,
					Title = normalizedTitle,
					ImageUrl = DcImageBaseUrl + path,
					FileExtension = extension,
					SellerName = sellerName
				});
			}

			return new GalleryShipPackItemsResult
			{
				Items = items
			};
		}
		catch (Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to fetch DC pack detail: " + ex.Message);
			return new GalleryShipPackItemsResult
			{
				ErrorMessage = "디시콘 세부 항목을 불러오지 못했습니다."
			};
		}
	}

	private static async Task<GalleryShipPackSearchResult> SearchArcaPacksAsync(string query)
	{
		try
		{
			string url = string.IsNullOrWhiteSpace(query)
				? ArcaEmoteUrl
				: $"{ArcaEmoteUrl}?q={Uri.EscapeDataString(query.Trim())}";
			using HttpRequestMessage request = CreateArcaRequest(HttpMethod.Get, url);
			using HttpResponseMessage response = await ArcaClient.SendAsync(request);
			string html = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode || IsArcaChallengePage(html))
			{
				return new GalleryShipPackSearchResult
				{
					ErrorMessage = "아카콘은 아카라이브 보안 확인 때문에 게임 안에서 자동 검색이 막혀 있습니다."
				};
			}

			return new GalleryShipPackSearchResult
			{
				ErrorMessage = "아카콘 검색 구조가 바뀌어 지금은 자동 검색을 완료하지 못했습니다."
			};
		}
		catch
		{
			return new GalleryShipPackSearchResult
			{
				ErrorMessage = "아카콘은 아카라이브 보안 확인 때문에 게임 안에서 자동 검색이 막혀 있습니다."
			};
		}
	}

	private static Task<GalleryShipPackItemsResult> FetchArcaPackItemsAsync(GalleryShipEmotePack pack)
	{
		return Task.FromResult(new GalleryShipPackItemsResult
		{
			ErrorMessage = "아카콘 세부 항목은 아카라이브 보안 확인이 풀리면 이어서 붙일 수 있습니다."
		});
	}

	private static bool IsArcaChallengePage(string html)
	{
		return html.Contains("cf-turnstile-response", StringComparison.OrdinalIgnoreCase)
			|| html.Contains("challenge-error-text", StringComparison.OrdinalIgnoreCase)
			|| html.Contains("보안 확인 수행 중", StringComparison.OrdinalIgnoreCase)
			|| html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task EnsureDcSessionAsync()
	{
		Uri uri = new(DcHomeUrl);
		if (DcCookies.GetCookies(uri).Cast<Cookie>().Any(static cookie => string.Equals(cookie.Name, "ci_c", StringComparison.Ordinal)))
		{
			return;
		}

		using HttpRequestMessage request = CreateDcRequest(HttpMethod.Get, DcHomeUrl);
		using HttpResponseMessage response = await DcClient.SendAsync(request);
		response.EnsureSuccessStatusCode();
		await response.Content.ReadAsByteArrayAsync();
	}

	private static string GetDcCsrfToken()
	{
		CookieCollection cookies = DcCookies.GetCookies(new Uri(DcHomeUrl));
		foreach (Cookie cookie in cookies)
		{
			if (string.Equals(cookie.Name, "ci_c", StringComparison.Ordinal))
			{
				return cookie.Value;
			}
		}

		return string.Empty;
	}

	private static async Task<HttpResponseMessage> DownloadImageAsync(GalleryShipEmoteProvider provider, string imageUrl)
	{
		if (provider == GalleryShipEmoteProvider.DcCon)
		{
			await EnsureDcSessionAsync();
			HttpRequestMessage request = CreateDcRequest(HttpMethod.Get, NormalizeUrl(imageUrl));
			request.Headers.Referrer = new Uri(DcHomeUrl);
			return await DcClient.SendAsync(request);
		}

		HttpRequestMessage arcaRequest = CreateArcaRequest(HttpMethod.Get, NormalizeUrl(imageUrl));
		return await ArcaClient.SendAsync(arcaRequest);
	}

	private static HttpRequestMessage CreateDcRequest(HttpMethod method, string url)
	{
		HttpRequestMessage request = new(method, url);
		request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
		request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/png,*/*;q=0.8");
		request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
		return request;
	}

	private static HttpRequestMessage CreateArcaRequest(HttpMethod method, string url)
	{
		HttpRequestMessage request = new(method, url);
		request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
		request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
		request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
		return request;
	}

	private static HttpClient CreateClient(CookieContainer cookieContainer)
	{
		HttpClientHandler handler = new()
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
			CookieContainer = cookieContainer,
			UseCookies = true
		};

		return new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(20)
		};
	}

	private static string DetermineImageExtension(HttpContentHeaders headers, string extensionHint, string imageUrl)
	{
		string normalizedHint = NormalizeExtension(extensionHint);
		if (!string.IsNullOrWhiteSpace(normalizedHint))
		{
			return normalizedHint;
		}

		string? fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			string extension = Path.GetExtension(fileName.Trim('"'));
			if (!string.IsNullOrWhiteSpace(extension))
			{
				return NormalizeExtension(extension);
			}
		}

		if (!string.IsNullOrWhiteSpace(headers.ContentType?.MediaType))
		{
			string mediaType = headers.ContentType.MediaType;
			if (mediaType.EndsWith("/gif", StringComparison.OrdinalIgnoreCase))
			{
				return "gif";
			}

			if (mediaType.EndsWith("/png", StringComparison.OrdinalIgnoreCase))
			{
				return "png";
			}

			if (mediaType.EndsWith("/jpeg", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("/jpg", StringComparison.OrdinalIgnoreCase))
			{
				return "jpg";
			}

			if (mediaType.EndsWith("/webp", StringComparison.OrdinalIgnoreCase))
			{
				return "webp";
			}
		}

		string urlExtension = Path.GetExtension(new Uri(NormalizeUrl(imageUrl)).AbsolutePath);
		if (!string.IsNullOrWhiteSpace(urlExtension))
		{
			return NormalizeExtension(urlExtension);
		}

		return "png";
	}

	private static string NormalizeExtension(string extension)
	{
		return extension.Trim().TrimStart('.').ToLowerInvariant() switch
		{
			"jpeg" => "jpg",
			"" => string.Empty,
			string value => value
		};
	}

	private static string ComputeImageHash(string imageUrl)
	{
		return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(ImageCacheVersion + "|" + imageUrl)));
	}

	private readonly record struct GifFrameData(byte[] Pixels, double DurationSeconds);

	private static (int Width, int Height, IReadOnlyList<GifFrameData> Frames) DecodeGifAnimation(byte[] data)
	{
		if (data.Length < 13 || (data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F'))
		{
			throw new InvalidDataException("Invalid GIF header.");
		}

		int offset = 6;
		int logicalWidth = ReadUInt16LittleEndian(data, ref offset);
		int logicalHeight = ReadUInt16LittleEndian(data, ref offset);
		if (logicalWidth <= 0 || logicalHeight <= 0)
		{
			throw new InvalidDataException("GIF has invalid dimensions.");
		}

		byte packed = ReadByte(data, ref offset);
		bool hasGlobalColorTable = (packed & 0x80) != 0;
		int globalColorTableSize = 1 << ((packed & 0x07) + 1);
		ReadByte(data, ref offset);
		offset++;

		byte[]? globalColorTable = hasGlobalColorTable ? ReadColorTable(data, ref offset, globalColorTableSize) : null;
		byte[] canvas = new byte[logicalWidth * logicalHeight * 4];
		byte[]? backgroundColor = null;
		FillCanvas(canvas, backgroundColor);
		List<GifFrameData> frames = new();
		int transparentIndex = -1;
		int disposalMethod = 0;
		double frameDurationSeconds = 0.1d;
		while (offset < data.Length)
		{
			byte introducer = ReadByte(data, ref offset);
			switch (introducer)
			{
				case 0x21:
				{
					byte extensionLabel = ReadByte(data, ref offset);
					if (extensionLabel == 0xF9)
					{
						int blockSize = ReadByte(data, ref offset);
						if (blockSize != 4 || offset + blockSize > data.Length)
						{
							throw new InvalidDataException("GIF graphic control extension is invalid.");
						}

						byte graphicsPacked = data[offset];
						disposalMethod = (graphicsPacked >> 2) & 0x07;
						int frameDelayHundredths = data[offset + 1] | (data[offset + 2] << 8);
						frameDurationSeconds = Math.Max(0.05d, frameDelayHundredths / 100d);
						transparentIndex = (graphicsPacked & 0x01) != 0 ? data[offset + 3] : -1;
						offset += blockSize;
						if (ReadByte(data, ref offset) != 0)
						{
							throw new InvalidDataException("GIF graphic control terminator is missing.");
						}
					}
					else
					{
						SkipSubBlocks(data, ref offset);
					}

					break;
				}
				case 0x2C:
				{
					int frameLeft = ReadUInt16LittleEndian(data, ref offset);
					int frameTop = ReadUInt16LittleEndian(data, ref offset);
					int frameWidth = ReadUInt16LittleEndian(data, ref offset);
					int frameHeight = ReadUInt16LittleEndian(data, ref offset);
					if (frameWidth <= 0 || frameHeight <= 0)
					{
						throw new InvalidDataException("GIF frame has invalid dimensions.");
					}

					byte imagePacked = ReadByte(data, ref offset);
					bool hasLocalColorTable = (imagePacked & 0x80) != 0;
					bool interlaced = (imagePacked & 0x40) != 0;
					int localColorTableSize = 1 << ((imagePacked & 0x07) + 1);
					byte[]? colorTable = hasLocalColorTable ? ReadColorTable(data, ref offset, localColorTableSize) : globalColorTable;
					if (colorTable == null)
					{
						throw new InvalidDataException("GIF frame is missing a color table.");
					}

					byte[]? previousCanvas = disposalMethod == 3 ? canvas.ToArray() : null;
					int lzwMinimumCodeSize = ReadByte(data, ref offset);
					byte[] compressedData = ReadSubBlocks(data, ref offset);
					byte[] indices = DecodeGifIndices(compressedData, lzwMinimumCodeSize, frameWidth * frameHeight);
					ComposeGifFrame(indices, colorTable, transparentIndex, canvas, logicalWidth, logicalHeight, frameLeft, frameTop, frameWidth, frameHeight, interlaced);
					frames.Add(new GifFrameData(canvas.ToArray(), frameDurationSeconds));

					ApplyGifDisposal(disposalMethod, previousCanvas, canvas, logicalWidth, logicalHeight, frameLeft, frameTop, frameWidth, frameHeight, backgroundColor);
					disposalMethod = 0;
					transparentIndex = -1;
					frameDurationSeconds = 0.1d;
					break;
				}
				case 0x3B:
					if (frames.Count == 0)
					{
						throw new InvalidDataException("GIF ended before an image frame was found.");
					}

					return (logicalWidth, logicalHeight, frames);
				default:
					throw new InvalidDataException($"Unsupported GIF block: 0x{introducer:X2}");
			}
		}

		if (frames.Count > 0)
		{
			return (logicalWidth, logicalHeight, frames);
		}

		throw new InvalidDataException("GIF data ended unexpectedly.");
	}

	private static void ComposeGifFrame(
		byte[] indices,
		byte[] colorTable,
		int transparentIndex,
		byte[] canvas,
		int canvasWidth,
		int canvasHeight,
		int frameLeft,
		int frameTop,
		int frameWidth,
		int frameHeight,
		bool interlaced)
	{
		int sourcePixelIndex = 0;
		if (interlaced)
		{
			int[] starts = [0, 4, 2, 1];
			int[] steps = [8, 8, 4, 2];
			for (int pass = 0; pass < starts.Length; pass++)
			{
				for (int row = starts[pass]; row < frameHeight; row += steps[pass])
				{
					BlitGifRow(indices, colorTable, transparentIndex, canvas, canvasWidth, canvasHeight, frameLeft, frameTop, frameWidth, row, ref sourcePixelIndex);
				}
			}
		}
		else
		{
			for (int row = 0; row < frameHeight; row++)
			{
				BlitGifRow(indices, colorTable, transparentIndex, canvas, canvasWidth, canvasHeight, frameLeft, frameTop, frameWidth, row, ref sourcePixelIndex);
			}
		}
	}

	private static void BlitGifRow(
		byte[] indices,
		byte[] colorTable,
		int transparentIndex,
		byte[] canvas,
		int canvasWidth,
		int canvasHeight,
		int frameLeft,
		int frameTop,
		int frameWidth,
		int sourceRow,
		ref int sourcePixelIndex)
	{
		int destinationY = frameTop + sourceRow;
		for (int column = 0; column < frameWidth; column++)
		{
			if (sourcePixelIndex >= indices.Length)
			{
				return;
			}

			int colorIndex = indices[sourcePixelIndex++];
			int destinationX = frameLeft + column;
			if (destinationX < 0 || destinationX >= canvasWidth || destinationY < 0 || destinationY >= canvasHeight || colorIndex == transparentIndex)
			{
				continue;
			}

			int colorOffset = colorIndex * 4;
			if (colorOffset + 3 >= colorTable.Length)
			{
				continue;
			}

			int pixelOffset = (destinationY * canvasWidth + destinationX) * 4;
			canvas[pixelOffset] = colorTable[colorOffset];
			canvas[pixelOffset + 1] = colorTable[colorOffset + 1];
			canvas[pixelOffset + 2] = colorTable[colorOffset + 2];
			canvas[pixelOffset + 3] = 255;
		}
	}

	private static void ApplyGifDisposal(
		int disposalMethod,
		byte[]? previousCanvas,
		byte[] canvas,
		int canvasWidth,
		int canvasHeight,
		int frameLeft,
		int frameTop,
		int frameWidth,
		int frameHeight,
		byte[]? backgroundColor)
	{
		switch (disposalMethod)
		{
			case 2:
				FillCanvasRect(canvas, canvasWidth, canvasHeight, frameLeft, frameTop, frameWidth, frameHeight, backgroundColor);
				break;
			case 3:
				if (previousCanvas != null && previousCanvas.Length == canvas.Length)
				{
					Buffer.BlockCopy(previousCanvas, 0, canvas, 0, canvas.Length);
				}
				break;
		}
	}

	private static void FillCanvas(byte[] canvas, byte[]? color)
	{
		if (color == null)
		{
			Array.Clear(canvas);
			return;
		}

		for (int i = 0; i < canvas.Length; i += 4)
		{
			canvas[i] = color[0];
			canvas[i + 1] = color[1];
			canvas[i + 2] = color[2];
			canvas[i + 3] = color[3];
		}
	}

	private static void FillCanvasRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height, byte[]? color)
	{
		int startX = Math.Max(0, left);
		int startY = Math.Max(0, top);
		int endX = Math.Min(canvasWidth, left + width);
		int endY = Math.Min(canvasHeight, top + height);
		for (int y = startY; y < endY; y++)
		{
			for (int x = startX; x < endX; x++)
			{
				int pixelOffset = (y * canvasWidth + x) * 4;
				if (color == null)
				{
					canvas[pixelOffset] = 0;
					canvas[pixelOffset + 1] = 0;
					canvas[pixelOffset + 2] = 0;
					canvas[pixelOffset + 3] = 0;
				}
				else
				{
					canvas[pixelOffset] = color[0];
					canvas[pixelOffset + 1] = color[1];
					canvas[pixelOffset + 2] = color[2];
					canvas[pixelOffset + 3] = color[3];
				}
			}
		}
	}

	private static int CountOpaquePixels(byte[] canvas)
	{
		int count = 0;
		for (int i = 3; i < canvas.Length; i += 4)
		{
			if (canvas[i] != 0)
			{
				count++;
			}
		}

		return count;
	}

	private static byte[] ReadColorTable(byte[] data, ref int offset, int colorCount)
	{
		int byteCount = colorCount * 3;
		EnsureAvailable(data, offset, byteCount);
		byte[] table = new byte[colorCount * 4];
		for (int i = 0; i < colorCount; i++)
		{
			int sourceOffset = offset + (i * 3);
			int destinationOffset = i * 4;
			table[destinationOffset] = data[sourceOffset];
			table[destinationOffset + 1] = data[sourceOffset + 1];
			table[destinationOffset + 2] = data[sourceOffset + 2];
			table[destinationOffset + 3] = 255;
		}

		offset += byteCount;
		return table;
	}

	private static byte[] ReadSubBlocks(byte[] data, ref int offset)
	{
		List<byte> bytes = new();
		while (true)
		{
			int blockSize = ReadByte(data, ref offset);
			if (blockSize == 0)
			{
				return bytes.ToArray();
			}

			EnsureAvailable(data, offset, blockSize);
			bytes.AddRange(data.AsSpan(offset, blockSize).ToArray());
			offset += blockSize;
		}
	}

	private static void SkipSubBlocks(byte[] data, ref int offset)
	{
		while (true)
		{
			int blockSize = ReadByte(data, ref offset);
			if (blockSize == 0)
			{
				return;
			}

			EnsureAvailable(data, offset, blockSize);
			offset += blockSize;
		}
	}

	private static byte[] DecodeGifIndices(byte[] compressedData, int minimumCodeSize, int expectedPixelCount)
	{
		if (minimumCodeSize < 2 || minimumCodeSize > 8)
		{
			throw new InvalidDataException("GIF uses an unsupported LZW code size.");
		}

		int clearCode = 1 << minimumCodeSize;
		int endCode = clearCode + 1;
		int nextCode = clearCode + 2;
		int codeSize = minimumCodeSize + 1;
		int codeMask = (1 << codeSize) - 1;
		int[] prefixes = new int[4096];
		byte[] suffixes = new byte[4096];
		byte[] stack = new byte[4097];
		for (int i = 0; i < clearCode; i++)
		{
			suffixes[i] = (byte)i;
		}

		byte[] output = new byte[expectedPixelCount];
		int outputIndex = 0;
		int bits = 0;
		int datum = 0;
		int dataIndex = 0;
		int stackSize = 0;
		int previousCode = -1;
		int firstValue = 0;

		while (outputIndex < expectedPixelCount)
		{
			if (stackSize > 0)
			{
				output[outputIndex++] = stack[--stackSize];
				continue;
			}

			while (bits < codeSize)
			{
				if (dataIndex >= compressedData.Length)
				{
					return output;
				}

				datum |= compressedData[dataIndex++] << bits;
				bits += 8;
			}

			int code = datum & codeMask;
			datum >>= codeSize;
			bits -= codeSize;
			if (code == clearCode)
			{
				codeSize = minimumCodeSize + 1;
				codeMask = (1 << codeSize) - 1;
				nextCode = clearCode + 2;
				previousCode = -1;
				continue;
			}

			if (code == endCode)
			{
				break;
			}

			if (previousCode == -1)
			{
				if (code >= clearCode)
				{
					throw new InvalidDataException("GIF LZW stream started with an invalid code.");
				}

				output[outputIndex++] = suffixes[code];
				firstValue = suffixes[code];
				previousCode = code;
				continue;
			}

			int currentCode = code;
			if (currentCode >= nextCode)
			{
				stack[stackSize++] = (byte)firstValue;
				currentCode = previousCode;
			}

			while (currentCode > clearCode)
			{
				if (currentCode >= prefixes.Length)
				{
					throw new InvalidDataException("GIF LZW dictionary index is out of range.");
				}

				stack[stackSize++] = suffixes[currentCode];
				currentCode = prefixes[currentCode];
			}

			firstValue = suffixes[currentCode];
			stack[stackSize++] = (byte)firstValue;
			if (nextCode < 4096)
			{
				prefixes[nextCode] = previousCode;
				suffixes[nextCode] = (byte)firstValue;
				nextCode++;
				if (nextCode == (1 << codeSize) && codeSize < 12)
				{
					codeSize++;
					codeMask = (1 << codeSize) - 1;
				}
			}

			previousCode = code;
		}

		return output;
	}

	private static byte[] EncodePng(int width, int height, byte[] pixels)
	{
		byte[] raw = new byte[height * ((width * 4) + 1)];
		for (int row = 0; row < height; row++)
		{
			int rawOffset = row * ((width * 4) + 1);
			raw[rawOffset] = 0;
			Buffer.BlockCopy(pixels, row * width * 4, raw, rawOffset + 1, width * 4);
		}

		byte[] compressed;
		using (MemoryStream compressedStream = new())
		{
			using (ZLibStream zlib = new(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
			{
				zlib.Write(raw, 0, raw.Length);
			}

			compressed = compressedStream.ToArray();
		}

		using MemoryStream output = new();
		output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

		byte[] header = new byte[13];
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
		header[8] = 8;
		header[9] = 6;
		WritePngChunk(output, "IHDR", header);
		WritePngChunk(output, "IDAT", compressed);
		WritePngChunk(output, "IEND", []);
		return output.ToArray();
	}

	private static void WritePngChunk(Stream stream, string chunkType, byte[] data)
	{
		Span<byte> intBuffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(intBuffer, (uint)data.Length);
		stream.Write(intBuffer);

		byte[] typeBytes = Encoding.ASCII.GetBytes(chunkType);
		stream.Write(typeBytes, 0, typeBytes.Length);
		if (data.Length > 0)
		{
			stream.Write(data, 0, data.Length);
		}

		uint crc = ComputePngCrc32(typeBytes, data);
		BinaryPrimitives.WriteUInt32BigEndian(intBuffer, crc);
		stream.Write(intBuffer);
	}

	private static uint ComputePngCrc32(byte[] typeBytes, byte[] data)
	{
		uint crc = 0xFFFFFFFFu;
		UpdatePngCrc32(ref crc, typeBytes);
		UpdatePngCrc32(ref crc, data);
		return ~crc;
	}

	private static void UpdatePngCrc32(ref uint crc, ReadOnlySpan<byte> data)
	{
		foreach (byte value in data)
		{
			crc = PngCrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		}
	}

	private static uint[] CreatePngCrcTable()
	{
		uint[] table = new uint[256];
		for (uint i = 0; i < table.Length; i++)
		{
			uint value = i;
			for (int bit = 0; bit < 8; bit++)
			{
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			}

			table[i] = value;
		}

		return table;
	}

	private static int ReadUInt16LittleEndian(byte[] data, ref int offset)
	{
		EnsureAvailable(data, offset, 2);
		int value = data[offset] | (data[offset + 1] << 8);
		offset += 2;
		return value;
	}

	private static byte ReadByte(byte[] data, ref int offset)
	{
		EnsureAvailable(data, offset, 1);
		return data[offset++];
	}

	private static void EnsureAvailable(byte[] data, int offset, int count)
	{
		if (offset < 0 || count < 0 || offset + count > data.Length)
		{
			throw new InvalidDataException("GIF data ended unexpectedly.");
		}
	}

	private static string CleanHtml(string text)
	{
		return WebUtility.HtmlDecode(HtmlTagRegex.Replace(text, string.Empty)).Trim();
	}

	private static string NormalizeUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return string.Empty;
		}

		if (url.StartsWith("//", StringComparison.Ordinal))
		{
			return "https:" + url;
		}

		return url;
	}

	private static void TryDeleteBrokenCacheFile(string path)
	{
		try
		{
			lock (TextureCacheSync)
			{
				TextureAssetCache.Remove(path);
			}

			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
