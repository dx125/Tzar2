using System;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The mission goal wizard (ADR 0290): a goal of <see cref="MissionGoals"/> - destroy the enemies, bring an object to a
/// named area, hold out, gather, keep an object alive - added to the scenario as ordinary events, actions and rules in
/// one undoable step. The object is the editor's selection; the area one of the map's named areas.
/// </summary>
public partial class GoalWizard : ConfirmationDialog
{
    private static readonly string[] Kinds = ["Destroy the enemies", "Bring an object to an area", "Hold out for a time", "Gather a resource", "Keep an object alive (else lose)"];
    private readonly MapDocument _document;
    private readonly int _objectId;
    private readonly NamedArea[] _areas;
    private GoalKind _kind;
    private string _quest = string.Empty;
    private int _kingdom = 1;
    private int _area;
    private int _minutes = 10;
    private int _resource;
    private int _amount = 1000;
    private string _next = string.Empty;

    /// <summary>
    /// Opens the wizard over <paramref name="document"/>; <paramref name="objectId"/> is the selected object, or -1;
    /// <paramref name="campaignMaps"/> the names the next map may take.
    /// </summary>
    public GoalWizard(MapDocument document, int objectId, string objectLabel, System.Collections.Generic.IReadOnlyList<string>? campaignMaps = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _objectId = objectId;
        _areas = [.. document.State.Extras.Areas];
        Title = "Add a mission goal";
        OkButtonText = "Add";
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        GridContainer g = Forms.Grid(box);
        Forms.Choice(g, "Goal", Kinds, 0, i => _kind = (GoalKind)i);
        Forms.Text(g, "Quest text", _quest, v => _quest = v);
        Forms.Number(g, "Player's kingdom", _kingdom, 1, 8, v => _kingdom = v);
        Forms.Label(g, "Object");
        Forms.Label(g, objectId >= 0 ? objectLabel : "(select one in the map first)");
        Forms.Choice(g, "Area", _areas.Length > 0 ? [.. _areas.Select(a => a.Name)] : ["(no named area: draw one in Areas & groups)"], 0, i => _area = i);
        Forms.Number(g, "Minutes", _minutes, 1, 600, v => _minutes = v);
        Forms.Choice(g, "Resource", MissionGoals.Resources, 0, i => _resource = i);
        Forms.Number(g, "Amount", _amount, 1, 1_000_000, v => _amount = v);
        Pickers.Suggest(g, "Next campaign map", _next, campaignMaps ?? [], v => _next = v); // ADR 0291: the campaign's maps
        Forms.Label(box, "The goal becomes ordinary rules (\"Goal N ...\") to edit in the rule editor.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(box);
        Confirmed += Add;
        Canceled += QueueFree;
    }

    /// <summary>Tells the editor what happened (the status line).</summary>
    public event Action<string>? Done;

    /// <inheritdoc/>
    public override void _Ready() => Forms.ShowCentered(this);

    private void Add()
    {
        var goal = new MissionGoal(_kind, _quest.Trim(), _kingdom, _objectId, _areas.Length > 0 ? _areas[_area].Area : null, _minutes, MissionGoals.Resources[_resource], _amount, _next.Trim().Length > 0 ? _next.Trim() : null);
        try
        {
            _document.Apply("Add mission goal", s => s.WithScenario(m => MissionGoals.Add(m, goal)));
            Done?.Invoke($"Added the goal \"{Kinds[(int)_kind]}\".");
        }
        catch (ArgumentException e)
        {
            Done?.Invoke("The goal was not added: " + e.Message);
        }

        QueueFree();
    }
}
