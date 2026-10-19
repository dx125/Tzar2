using System;
using System.Linq;
using Godot;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Import.Maps;

namespace Tzar2.Client.Editor;

/// <summary>The legacy converter's entry points in the editor (TDD §18.5 REQ-EDT-04): the File menu and drag and drop.</summary>
public partial class EditorScreen
{
    private ImportWindow? _import;

    /// <summary>Raised when the user plays a converted campaign from the report (the game mode starts it).</summary>
    public event Action<ICampaignSource>? CampaignPlayRequested;

    /// <summary>Raised when the user opens a converted campaign in the campaign editor.</summary>
    public event Action<Tzar2.Import.Editing.ImportItem>? ConvertedCampaignOpened;

    /// <summary>Opens (or focuses) the import window.</summary>
    public ImportWindow OpenImport()
    {
        if (_import is null || !IsInstanceValid(_import))
        {
            _import = new ImportWindow(this, source => CampaignPlayRequested?.Invoke(source));
            _import.OpenCampaign += item => ConvertedCampaignOpened?.Invoke(item);
            AddChild(_import);
        }

        _import.GrabFocus();
        return _import;
    }

    /// <summary>Shows where a finding is (map tile, object, kingdom, trigger part).</summary>
    public void LocateFinding(EditorLocation at) => Locate(at);

    private void ImportMenu(PopupMenu file)
    {
        Item(file, "Import legacy map/campaign (.wmp/.wcm)…", () => OpenImport());
    }

    private void HookImport()
    {
        GetWindow().FilesDropped += files =>
        {
            string[] legacy = files.Where(f => LegacyCorpus.IsLegacyFile(f) || System.IO.Directory.Exists(f)).ToArray();
            if (legacy.Length > 0)
            {
                OpenImport().AddPaths(legacy);
            }
            else if (files.FirstOrDefault(f => f.EndsWith(".t2map", StringComparison.OrdinalIgnoreCase)) is { } map)
            {
                OpenFile(map);
            }
        };
    }
}
