using System;
using System.Collections.Generic;

namespace GalleryShip;

internal sealed record GalleryShipProbeResult(
	GalleryShipProbeOutcome Outcome,
	IReadOnlyList<GalleryShipListingPlayer> Players)
{
	public static GalleryShipProbeResult FromOutcome(GalleryShipProbeOutcome outcome)
	{
		return new GalleryShipProbeResult(outcome, Array.Empty<GalleryShipListingPlayer>());
	}
}
