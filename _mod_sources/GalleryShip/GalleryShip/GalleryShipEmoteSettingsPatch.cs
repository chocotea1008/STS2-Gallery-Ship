using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace GalleryShip;

[HarmonyPatch(typeof(NSettingsTabManager), nameof(NSettingsTabManager._Ready))]
internal static class GalleryShipEmoteSettingsPatch
{
	private static readonly FieldInfo TabsField = AccessTools.Field(typeof(NSettingsTabManager), "_tabs");
	private static readonly MethodInfo? SwitchTabMethod = AccessTools.Method(typeof(NSettingsTabManager), "SwitchTabTo");

	[HarmonyPostfix]
	private static void Postfix(NSettingsTabManager __instance)
	{
		if (__instance.GetNodeOrNull<NSettingsTab>("GalleryShipEmoteTab") != null)
		{
			return;
		}

		if (TabsField.GetValue(__instance) is not Dictionary<NSettingsTab, NSettingsPanel> tabs)
		{
			return;
		}

		NSettingsTab? templateTab = __instance.GetNodeOrNull<NSettingsTab>("Input") ?? tabs.Keys.LastOrDefault();
		NSettingsPanel? templatePanel = tabs.Values.FirstOrDefault();
		if (templateTab == null || templatePanel == null || templateTab.GetParent() is not Control tabParent || templatePanel.GetParent() is not Control panelParent)
		{
			return;
		}

		NSettingsTab newTab = (NSettingsTab)templateTab.Duplicate();
		newTab.Name = "GalleryShipEmoteTab";
		tabParent.AddChild(newTab);
		tabParent.MoveChild(newTab, templateTab.GetIndex() + 1);

		GalleryShipEmoteSettingsPanel newPanel = new()
		{
			Name = "GalleryShipEmotePanel",
			Visible = false
		};
		panelParent.AddChild(newPanel);
		panelParent.MoveChild(newPanel, panelParent.GetChildCount() - 1);

		tabs.Add(newTab, newPanel);
		Callable.From(() => FinalizeInjectedTab(__instance, newTab)).CallDeferred();
	}

	private static void FinalizeInjectedTab(NSettingsTabManager manager, NSettingsTab newTab)
	{
		if (!GodotObject.IsInstanceValid(manager) || !GodotObject.IsInstanceValid(newTab))
		{
			return;
		}

		if (TabsField.GetValue(manager) is not Dictionary<NSettingsTab, NSettingsPanel> tabs || !tabs.ContainsKey(newTab))
		{
			return;
		}

		try
		{
			newTab.Deselect();
			newTab.SetLabel("이모티콘");
			DisconnectReleasedSignals(newTab);
			RelayoutTabs(tabs.Keys.ToList());
			newTab.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
			{
				SwitchTabMethod?.Invoke(manager, new object[] { newTab });
			}));

			Log.Info("[GalleryShip] Emote settings tab injected.");
		}
		catch (System.Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to finalize emote settings tab: " + ex);
		}
	}

	private static void DisconnectReleasedSignals(NSettingsTab tab)
	{
		foreach (Godot.Collections.Dictionary connection in tab.GetSignalConnectionList(NClickableControl.SignalName.Released))
		{
			if (!connection.ContainsKey("callable"))
			{
				continue;
			}

			Callable callable = (Callable)connection["callable"];
			if (tab.IsConnected(NClickableControl.SignalName.Released, callable))
			{
				tab.Disconnect(NClickableControl.SignalName.Released, callable);
			}
		}
	}

	private static void RelayoutTabs(IReadOnlyList<NSettingsTab> orderedTabs)
	{
		if (orderedTabs.Count <= 1)
		{
			return;
		}

		List<NSettingsTab> positionedTabs = orderedTabs.OrderBy(tab => tab.Position.X).ToList();
		float minX = positionedTabs.Min(tab => tab.Position.X);
		float maxRight = positionedTabs.Max(tab => tab.Position.X + tab.Size.X);
		float y = positionedTabs[0].Position.Y;
		float height = positionedTabs[0].Size.Y;
		float gap = MeasureAverageGap(positionedTabs);
		float totalWidth = maxRight - minX;
		float tabWidth = Mathf.Max(150f, (totalWidth - gap * (orderedTabs.Count - 1)) / orderedTabs.Count);

		float totalNeededWidth = tabWidth * orderedTabs.Count + gap * (orderedTabs.Count - 1);
		float startX = minX + Mathf.Max(0f, (totalWidth - totalNeededWidth) * 0.5f);

		for (int i = 0; i < orderedTabs.Count; i++)
		{
			NSettingsTab tab = orderedTabs[i];
			tab.Position = new Vector2(startX + i * (tabWidth + gap), y);
			tab.CustomMinimumSize = new Vector2(tabWidth, height);
			tab.Size = new Vector2(tabWidth, height);
		}
	}

	private static float MeasureAverageGap(IReadOnlyList<NSettingsTab> tabs)
	{
		if (tabs.Count <= 1)
		{
			return 16f;
		}

		float totalGap = 0f;
		int gapCount = 0;
		for (int i = 0; i < tabs.Count - 1; i++)
		{
			float gap = tabs[i + 1].Position.X - (tabs[i].Position.X + tabs[i].Size.X);
			if (gap <= 0f)
			{
				continue;
			}

			totalGap += gap;
			gapCount++;
		}

		return gapCount > 0 ? totalGap / gapCount : 16f;
	}
}

[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
internal static class GalleryShipEmoteSettingsBackPatch
{
	private static readonly FieldInfo BackButtonField = AccessTools.Field(typeof(NSubmenu), "_backButton");
	private static readonly AccessTools.FieldRef<NSubmenu, NSubmenuStack> StackRef = AccessTools.FieldRefAccess<NSubmenu, NSubmenuStack>("_stack");

	[HarmonyPostfix]
	private static void Postfix(NSettingsScreen __instance)
	{
		if (BackButtonField.GetValue(__instance) is not NBackButton backButton)
		{
			return;
		}

		DisconnectReleasedSignals(backButton);
		backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => HandleBackPressed(__instance)));
	}

	private static void HandleBackPressed(NSettingsScreen screen)
	{
		try
		{
			if (screen.FindChild("GalleryShipEmotePanel", recursive: true, owned: false) is GalleryShipEmoteSettingsPanel emotePanel
				&& GodotObject.IsInstanceValid(emotePanel)
				&& emotePanel.Visible
				&& emotePanel.TryHandleBackNavigation())
			{
				return;
			}
		}
		catch (System.Exception ex)
		{
			Log.Warn("[GalleryShip] Failed to handle settings back navigation: " + ex);
		}

		StackRef((NSubmenu)screen).Pop();
	}

	private static void DisconnectReleasedSignals(NClickableControl button)
	{
		foreach (Godot.Collections.Dictionary connection in button.GetSignalConnectionList(NClickableControl.SignalName.Released))
		{
			if (!connection.ContainsKey("callable"))
			{
				continue;
			}

			Callable callable = (Callable)connection["callable"];
			if (button.IsConnected(NClickableControl.SignalName.Released, callable))
			{
				button.Disconnect(NClickableControl.SignalName.Released, callable);
			}
		}
	}
}
