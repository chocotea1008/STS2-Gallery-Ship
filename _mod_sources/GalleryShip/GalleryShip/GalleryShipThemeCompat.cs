using Godot;

namespace GalleryShip;

internal static class GalleryShipThemeCompat
{
	internal static class Label
	{
		internal static readonly StringName FontSize = "font_size";

		internal static readonly StringName Font = "font";

		internal static readonly StringName OutlineSize = "outline_size";
	}

	internal static class RichTextLabel
	{
		internal static readonly StringName NormalFontSize = "normal_font_size";

		internal static readonly StringName BoldFontSize = "bold_font_size";

		internal static readonly StringName ItalicsFontSize = "italics_font_size";
	}
}
