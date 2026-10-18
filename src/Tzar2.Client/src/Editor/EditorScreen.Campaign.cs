using System;
using Godot;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;

namespace Tzar2.Client.Editor;

/// <summary>The editor's Campaign menu: the campaign editor window, editing a campaign's maps, playing the campaign.</summary>
public partial class EditorScreen
{
    private CampaignWindow? _campaignWindow;

    /// <summary>The campaign whose map is open in the editor (Ctrl+S saves the campaign), or null.</summary>
    public CampaignWindow? CampaignContext { get; set; }

    /// <summary>Raised when the user plays a campaign from the campaign editor (source, start map or null).</summary>
    public event Action<CampaignPlaytest, string?>? CampaignPlaytestRequested;

    /// <summary>Opens (or focuses) the campaign editor, optionally on a campaign.</summary>
    public CampaignWindow OpenCampaignEditor(CampaignDocument? campaign = null, string? path = null)
    {
        if (_campaignWindow is null || !IsInstanceValid(_campaignWindow))
        {
            _campaignWindow = new CampaignWindow(this, campaign, path);
            _campaignWindow.PlayRequested += (source, map) => CampaignPlaytestRequested?.Invoke(source, map);
            AddChild(_campaignWindow);
        }
        else if (campaign is not null)
        {
            _campaignWindow.SetCampaign(campaign, path);
        }

        _campaignWindow.GrabFocus();
        return _campaignWindow;
    }

    private void CampaignMenu(MenuBar bar)
    {
        PopupMenu menu = Menu(bar, "Campaign");
        Item(menu, "Campaign editor…", () => OpenCampaignEditor());
        Item(menu, "Open campaign (.t2camp)…", () => Files.Open(this, ["*.t2camp ; Tzar2 campaign"], paths => OpenCampaignEditor().OpenFile(paths[0]), many: false));
        Item(menu, "Save campaign", () => CampaignContext?.Save(saveAs: false));
        ConvertedCampaignOpened += item => OpenCampaignEditor(item.OpenCampaign(RulesFor));
    }

    // Ctrl+S on a campaign's map saves the campaign; otherwise the map.
    private bool SaveCampaignInstead()
    {
        if (CampaignContext is { } c && IsInstanceValid(c) && c.Campaign.Find(MapIdIn(c)) is not null)
        {
            c.Save(saveAs: false);
            return true;
        }

        return false;
    }

    private string MapIdIn(CampaignWindow c)
    {
        foreach (CampaignEntry m in c.Campaign.State.Maps)
        {
            if (ReferenceEquals(m.Document, Document))
            {
                return m.Id;
            }
        }

        return string.Empty;
    }
}
