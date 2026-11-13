using System;
using System.IO;
using Godot;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The editor's recent files and playtest recordings (ADR 0291): File → Open recent (maps, campaigns, recordings, newest
/// first), Play → save the last playtest as a recording and open one in the replay viewer.
/// </summary>
public partial class EditorScreen
{
    private RecentFiles _recent = RecentFiles.Empty;
    private PopupMenu? _recentMenu;

    /// <summary>The last playtest's recording (built when asked), or null before a playtest; set by the game mode.</summary>
    public Func<PlaytestRecording>? LastPlaytest { get; set; }

    /// <summary>Raised to watch a playtest recording (its path) once no work would be lost; the game mode shows it.</summary>
    public event Action<string>? RecordingOpenRequested;

    private static string RecentPath => Path.Combine(ProjectSettings.GlobalizePath("user://"), RecentFiles.FileName);

    /// <summary>Puts <paramref name="path"/> first in File → Open recent.</summary>
    public void Remember(string path)
    {
        _recent = _recent.Add(path);
        SaveRecent();
    }

    private void Forget(string path)
    {
        _recent = _recent.Remove(path);
        SaveRecent();
    }

    private void LoadRecent()
    {
        try
        {
            _recent = File.Exists(RecentPath) ? RecentFiles.Parse(File.ReadAllText(RecentPath)) : RecentFiles.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("Editor: cannot read the recent files: " + e.Message);
        }
    }

    private void SaveRecent()
    {
        try
        {
            File.WriteAllText(RecentPath, _recent.Write());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("Editor: cannot keep the recent files: " + e.Message);
        }
    }

    private void RecentMenu(PopupMenu file)
    {
        _recentMenu = new PopupMenu { Name = "Recent" };
        _recentMenu.IdPressed += id => OpenRecent((int)id);
        file.AddSubmenuNodeItem("Open recent", _recentMenu);
        file.AboutToPopup += FillRecent;
    }

    private void FillRecent()
    {
        if (_recentMenu is null)
        {
            return;
        }

        _recentMenu.Clear();
        for (int i = 0; i < _recent.Paths.Length; i++)
        {
            string path = _recent.Paths[i];
            string kind = RecentFiles.KindOf(path) switch { RecentKind.Campaign => "campaign", RecentKind.PlaytestRecording => "recording", _ => "map" };
            _recentMenu.AddItem($"{Path.GetFileName(path)}  ({kind}, {Path.GetDirectoryName(path)})", i);
        }

        if (_recent.Paths.IsEmpty)
        {
            _recentMenu.AddItem("(nothing yet)", -1);
            _recentMenu.SetItemDisabled(0, true);
        }
    }

    private void OpenRecent(int index)
    {
        if ((uint)index >= (uint)_recent.Paths.Length)
        {
            return;
        }

        string path = _recent.Paths[index];
        if (!File.Exists(path))
        {
            Forget(path);
            Status(path + " is gone; it left the recent files.");
            return;
        }

        switch (RecentFiles.KindOf(path))
        {
            case RecentKind.Campaign:
                OpenCampaignEditor().OpenFile(path);
                break;
            case RecentKind.PlaytestRecording:
                Guard(campaignToo: false, () => OpenRecording(path));
                break;
            default:
                Guard(campaignToo: false, () =>
                {
                    if (OpenFile(path))
                    {
                        Remember(path);
                    }
                });
                break;
        }
    }

    // Play → save the last playtest: the map as it was played, the settings and the timeline the tester ended on.
    private void SaveRecording()
    {
        if (LastPlaytest is not { } last)
        {
            Status("Play a test first: the recording is the last playtest of this editor session.");
            return;
        }

        Files.Save(this, MapPackage.Slug(Document.State.Name) + PlaytestRecording.Extension, path =>
        {
            string file = path.EndsWith(PlaytestRecording.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + PlaytestRecording.Extension;
            try
            {
                File.WriteAllBytes(file, last().ToBytes());
                Remember(file);
                Status("Saved the playtest recording " + file + ". Whoever opens it watches the playtest again on the map as it was played.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Status("Saving the recording failed: " + e.Message);
            }
        }, ["*" + PlaytestRecording.Extension + " ; Playtest recording"], Files.UserFolder("recordings"));
    }

    private void OpenRecording(string path)
    {
        Remember(path);
        RecordingOpenRequested?.Invoke(path);
    }

    private void RecordingMenu(PopupMenu play)
    {
        play.AddSeparator();
        Item(play, "Save the last playtest as a recording…", SaveRecording);
        Item(play, "Watch a playtest recording…", () => Guard(campaignToo: false, () =>
            Files.Open(this, ["*" + PlaytestRecording.Extension + " ; Playtest recording"], paths => OpenRecording(paths[0]), many: false, folder: Files.UserFolder("recordings"))));
    }
}
