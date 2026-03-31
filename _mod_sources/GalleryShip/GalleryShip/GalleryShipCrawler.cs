using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace GalleryShip;

internal static class GalleryShipCrawler
{
	private const int MaxArticleFetchConcurrency = 6;

	private const string BoardListUrl = "https://gall.dcinside.com/mgallery/board/lists/?id=slay&sort_type=N&search_head=180&page=1";

	private const string BoardBaseUrl = "https://gall.dcinside.com";

	private const string BodyFetchFallbackText = "\uBCF8\uBB38\uC744 \uBC1B\uC9C0 \uBABB\uD574 \uC6D0\uBB38 \uD398\uC774\uC9C0\uB85C \uC774\uB3D9\uD569\uB2C8\uB2E4.";

	private const string NoSteamLinkFallbackText = "\uCC38\uC5EC \uB9C1\uD06C\uB97C \uCC3E\uC9C0 \uBABB\uD574 \uAE00 \uD398\uC774\uC9C0\uB85C \uC774\uB3D9\uD569\uB2C8\uB2E4.";

	private static readonly Regex RowRegex = new(
		"<tr[^>]*class=\"[^\"]*ub-content[^\"]*us-post[^\"]*\"[^>]*data-no=\"(?<no>\\d+)\"[^>]*>(?<body>.*?)</tr>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex TitleCellRegex = new(
		"<td[^>]*class=\"[^\"]*gall_tit[^\"]*\"[^>]*>(?<body>.*?)</td>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex LinkRegex = new(
		"<a[^>]*href=\"(?<href>/mgallery/board/view/\\?[^\"]+)\"[^>]*>(?<text>.*?)</a>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex DateCellRegex = new(
		"<td[^>]*class=\"[^\"]*gall_date[^\"]*\"[^>]*title=\"(?<title>[^\"]+)\"[^>]*>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex WriteDivRegex = new(
		"<div[^>]*class=\"[^\"]*write_div[^\"]*\"[^>]*>(?<body>.*?)</div>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex SteamUrlRegex = new(
		"steam://joinlobby/\\d+/\\d+/\\d+",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex TagRegex = new(
		"<.*?>",
		RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly HttpClient Client = CreateClient();

	private static readonly object RejectedArticleLock = new();

	private static readonly Dictionary<string, RejectedArticleCacheEntry> RejectedArticleIds = new(StringComparer.Ordinal);

	private static readonly HashSet<string> PermanentRejectedArticleIds = new(StringComparer.Ordinal);

	private static bool _persistentRejectedLoaded;

	public static async Task<IReadOnlyList<GalleryShipListing>> FetchListingsAsync(CancellationToken cancellationToken)
	{
		string listHtml = await Client.GetStringAsync(BoardListUrl, cancellationToken);
		List<(string ArticleId, string Title, string ArticleUrl, DateTimeOffset? PostedAt)> candidates = ParseListPage(listHtml)
			.Where(candidate => !ShouldSkipArticleFetch(candidate.ArticleId))
			.Take(20)
			.ToList();
		GalleryShipListing[] listings = new GalleryShipListing[candidates.Count];

		using SemaphoreSlim gate = new(MaxArticleFetchConcurrency);
		Task[] tasks = candidates
			.Select((candidate, index) => FetchListingDetailsWithGateAsync(candidate, index, listings, gate, cancellationToken))
			.ToArray();
		await Task.WhenAll(tasks);
		return listings;
	}

	public static void MarkArticleRejected(string articleId, GalleryShipProbeOutcome outcome)
	{
		if (string.IsNullOrWhiteSpace(articleId))
		{
			return;
		}

		if (outcome == GalleryShipProbeOutcome.RunInProgress || outcome == GalleryShipProbeOutcome.Unavailable)
		{
			lock (RejectedArticleLock)
			{
				EnsurePersistentRejectedLoaded();
				if (PermanentRejectedArticleIds.Add(articleId))
				{
					SavePersistentRejectedArticleIds();
				}
			}
			return;
		}
	}

	private static bool ShouldSkipArticleFetch(string articleId)
	{
		if (string.IsNullOrWhiteSpace(articleId))
		{
			return false;
		}

		lock (RejectedArticleLock)
		{
			EnsurePersistentRejectedLoaded();
			if (PermanentRejectedArticleIds.Contains(articleId))
			{
				return true;
			}

			if (!RejectedArticleIds.TryGetValue(articleId, out RejectedArticleCacheEntry entry))
			{
				return false;
			}

			if (DateTime.UtcNow >= entry.ExpiresAtUtc)
			{
				RejectedArticleIds.Remove(articleId);
				return false;
			}

			return true;
		}
	}

	private static void EnsurePersistentRejectedLoaded()
	{
		if (_persistentRejectedLoaded)
		{
			return;
		}

		_persistentRejectedLoaded = true;
		try
		{
			string path = GetPersistentRejectedFilePath();
			if (!File.Exists(path))
			{
				return;
			}

			foreach (string line in File.ReadAllLines(path))
			{
				string trimmed = line.Trim();
				if (!string.IsNullOrWhiteSpace(trimmed))
				{
					PermanentRejectedArticleIds.Add(trimmed);
				}
			}
		}
		catch
		{
		}
	}

	private static void SavePersistentRejectedArticleIds()
	{
		try
		{
			string path = GetPersistentRejectedFilePath();
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllLines(path, PermanentRejectedArticleIds.OrderBy(id => id, StringComparer.Ordinal));
		}
		catch
		{
		}
	}

	private static string GetPersistentRejectedFilePath()
	{
		return ProjectSettings.GlobalizePath("user://galleryship_run_in_progress_articles.txt");
	}

	private static async Task FetchListingDetailsWithGateAsync(
		(string ArticleId, string Title, string ArticleUrl, DateTimeOffset? PostedAt) candidate,
		int index,
		GalleryShipListing[] listings,
		SemaphoreSlim gate,
		CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			listings[index] = await FetchListingOrFallbackAsync(candidate, cancellationToken);
		}
		finally
		{
			gate.Release();
		}
	}

	private static async Task<GalleryShipListing> FetchListingOrFallbackAsync(
		(string ArticleId, string Title, string ArticleUrl, DateTimeOffset? PostedAt) candidate,
		CancellationToken cancellationToken)
	{
		try
		{
			return await FetchListingDetailsAsync(candidate.ArticleId, candidate.Title, candidate.ArticleUrl, candidate.PostedAt, cancellationToken);
		}
		catch (Exception)
		{
			return new GalleryShipListing(
				candidate.ArticleId,
				candidate.Title,
				candidate.ArticleUrl,
				null,
				BodyFetchFallbackText,
				null,
				candidate.PostedAt);
		}
	}

	private static HttpClient CreateClient()
	{
		HttpClientHandler handler = new()
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
		};

		HttpClient client = new(handler)
		{
			Timeout = TimeSpan.FromSeconds(15)
		};
		client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
		client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://gall.dcinside.com/");
		client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
		client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
		return client;
	}

	private static List<(string ArticleId, string Title, string ArticleUrl, DateTimeOffset? PostedAt)> ParseListPage(string html)
	{
		List<(string ArticleId, string Title, string ArticleUrl, DateTimeOffset? PostedAt)> results = new();

		foreach (Match rowMatch in RowRegex.Matches(html))
		{
			string rowHtml = rowMatch.Groups["body"].Value;
			Match titleCellMatch = TitleCellRegex.Match(rowHtml);
			if (!titleCellMatch.Success)
			{
				continue;
			}

			Match linkMatch = LinkRegex.Match(titleCellMatch.Groups["body"].Value);
			if (!linkMatch.Success)
			{
				continue;
			}

			string articleId = rowMatch.Groups["no"].Value;
			string relativeUrl = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
			string articleUrl = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				? relativeUrl
				: BoardBaseUrl + relativeUrl;
			string title = CleanText(linkMatch.Groups["text"].Value);
			if (string.IsNullOrWhiteSpace(title))
			{
				continue;
			}

			results.Add((articleId, title, articleUrl, ParsePostedAt(rowHtml)));
		}

		return results;
	}

	private static async Task<GalleryShipListing> FetchListingDetailsAsync(
		string articleId,
		string title,
		string articleUrl,
		DateTimeOffset? postedAt,
		CancellationToken cancellationToken)
	{
		string articleHtml = await Client.GetStringAsync(articleUrl, cancellationToken);
		Match bodyMatch = WriteDivRegex.Match(articleHtml);
		string bodyHtml = bodyMatch.Success ? bodyMatch.Groups["body"].Value : articleHtml;
		string bodyText = CleanText(bodyHtml);
		string? steamUrl = SteamUrlRegex.Match(bodyHtml) is { Success: true } match ? match.Value : null;
		string? modSummary = ExtractModSummary(bodyText);
		string? summary = ExtractSummary(bodyText, steamUrl);

		return new GalleryShipListing(articleId, title, articleUrl, steamUrl, summary, modSummary, postedAt);
	}

	private static DateTimeOffset? ParsePostedAt(string rowHtml)
	{
		Match dateMatch = DateCellRegex.Match(rowHtml);
		if (!dateMatch.Success)
		{
			return null;
		}

		string raw = WebUtility.HtmlDecode(dateMatch.Groups["title"].Value).Trim();
		if (DateTimeOffset.TryParseExact(
			raw,
			"yyyy-MM-dd HH:mm:ss",
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeLocal,
			out DateTimeOffset postedAt))
		{
			return postedAt;
		}

		return null;
	}

	private static string? ExtractModSummary(string bodyText)
	{
		string[] lines = SplitLines(bodyText);
		foreach (string line in lines)
		{
			if (line.Contains("\uBAA8\uB4DC", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("mods", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("mod ", StringComparison.OrdinalIgnoreCase))
			{
				return Truncate(line, 90);
			}
		}

		return null;
	}

	private static string? ExtractSummary(string bodyText, string? steamUrl)
	{
		foreach (string line in SplitLines(bodyText))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			if (!string.IsNullOrWhiteSpace(steamUrl) && line.Contains(steamUrl, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			return Truncate(line, 100);
		}

		return steamUrl == null ? NoSteamLinkFallbackText : null;
	}

	private static string[] SplitLines(string text)
	{
		return text
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static line => !string.IsNullOrWhiteSpace(line))
			.ToArray();
	}

	private static string CleanText(string html)
	{
		string normalized = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
		normalized = TagRegex.Replace(normalized, string.Empty);
		normalized = WebUtility.HtmlDecode(normalized);
		normalized = normalized.Replace("\r", string.Empty);
		normalized = Regex.Replace(normalized, "[ \\t]+", " ");
		normalized = Regex.Replace(normalized, "\\n{3,}", "\n\n");
		return normalized.Trim();
	}

	private static string Truncate(string text, int maxLength)
	{
		if (text.Length <= maxLength)
		{
			return text;
		}

		return text[..(maxLength - 3)].TrimEnd() + "...";
	}

	private readonly record struct RejectedArticleCacheEntry(
		GalleryShipProbeOutcome Outcome,
		DateTime ExpiresAtUtc);
}
