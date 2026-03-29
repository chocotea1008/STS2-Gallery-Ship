using System;

namespace GalleryShip;

internal sealed record GalleryShipListing(
	string ArticleId,
	string Title,
	string ArticleUrl,
	string? SteamUrl,
	string? Summary,
	string? ModSummary)
{
	public bool HasSteamUrl => !string.IsNullOrWhiteSpace(SteamUrl);

	public bool TryGetLobbyId(out ulong lobbyId)
	{
		lobbyId = 0;
		if (string.IsNullOrWhiteSpace(SteamUrl))
		{
			return false;
		}

		string[] parts = SteamUrl.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length < 5)
		{
			return false;
		}

		return ulong.TryParse(parts[2], out _) && ulong.TryParse(parts[3], out lobbyId);
	}
}
