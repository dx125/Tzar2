using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Godot;
using Tzar2.Game.Scenario;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// The UI smoke test's way into the in-game dialogs (ADR 0200): opens each by name with sample contents, the way the
/// player's press or the scenario would, and closes them all again.
/// </summary>
public partial class GameScreen
{
    /// <summary>The in-game dialogs the UI smoke test opens, by name.</summary>
    public static readonly string[] AuditDialogs =
    [
        "game-menu", "options", "save", "load", "diplomacy", "tribute", "gamble", "exchange", "trade", "craft",
        "message", "story", "quests", "conversation", "help", "help-hotkeys", "scores",
    ];

    // Sample scenario texts, long enough to wrap (our own words).
    private const string SampleTitle = "The Burden of the Crown";
    private const string SampleText = "The old king is dead and his heirs quarrel over the crown. Gather your people, raise "
        + "an army and drive the raiders from the northern villages before the winter comes. Your scouts report a camp "
        + "of horsemen beyond the river; take the bridge and hold it until the reinforcements arrive.";

    /// <summary>Opens the in-game dialog <paramref name="name"/> (<see cref="AuditDialogs"/>); false when this match has none.</summary>
    public bool OpenForAudit(string name)
    {
        if (_hud is null)
        {
            return false;
        }

        switch (name)
        {
            case "game-menu":
                ShowMenu();
                return true;
            case "help":
                ShowHelp(Tzar2.Game.Ui.HelpCategory.Units);
                return true;
            case "help-hotkeys":
                ShowHelp(Tzar2.Game.Ui.HelpCategory.HotKeys);
                return true;
            case "scores":
                _scoreView = Tzar2.Game.Ui.ScoreView.ScoresAndClock; // F4's scoreboard over the view
                _scoresDrawn = null;
                return true;
            case "options":
                ShowOptions();
                return true;
            case "save":
                return ShowSaveDialog();
            case "load":
                return ShowLoadDialog();
            case "diplomacy":
                ShowDiplomacy();
                return _frame is not null;
            case "tribute":
                ShowTribute(back: false);
                return _frame is not null;
            case "gamble":
                OpenDialog(new GambleDialog(_ui, _rules.World.MaxGambleStake, _rules.World.GambleOdds.Length));
                return true;
            case "exchange":
                OpenDialog(new ExchangeDialog(_ui, _rules.World.ConvertRatio));
                return true;
            case "trade":
                OpenDialog(new TradeDialog(_ui));
                return true;
            case "craft":
                List<CraftChoice> items = _card.CraftOptions(CardNow()).Select(CraftChoiceOf).ToList();
                OpenDialog(new CraftDialog(_ui, items, _card.CraftPricePerPower, _card.CraftMaxPower));
                return true;
            case "message":
                ShowModal(new ScenarioNotice(ScenarioNoticeKind.Message, SampleTitle, SampleText, null, null, null, 0, false, 0, 0, true));
                return true;
            case "story":
                ShowModal(new ScenarioNotice(ScenarioNoticeKind.StoryScreen, SampleTitle, SampleText, null, null, null, 20000, false, 0, 0, true));
                return true;
            case "quests":
                AddAuditLayer(new QuestLogDialog(_ui, _hud.Layout.Size, [new QuestEntry(SampleTitle, SampleText, null), new QuestEntry("Hold the bridge", "Keep the bridge until dawn.", null), new QuestEntry("The third quest", "On the second page.", null)], _catalog.Text));
                return true;
            case "conversation":
                var view = new ConversationView(1, "Envoy", ImmutableArray.Create(
                    new PhraseChoice(0, "Envoy of the East", "My master offers peace and a tribute of gold, if you leave the eastern hills to him.", null, null),
                    new PhraseChoice(1, "You", "Tell your master the hills are mine.", null, null),
                    new PhraseChoice(2, "You", "Peace, then. Bring the gold before the next moon.", null, null)));
                OpenTalk(view);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Closes every dialog <see cref="OpenForAudit"/> may have opened and resumes the match.</summary>
    public void CloseAuditDialogs()
    {
        CloseDialog();
        CloseTalk();
        foreach (Node child in GetChildren())
        {
            if (child is CanvasLayer { Layer: 3 } layer)
            {
                RemoveChild(layer);
                layer.QueueFree();
            }
        }

        _open = null;
        _storyPause = false;
        _session.Paused = false;
        _scoreView = Tzar2.Game.Ui.ScoreView.None;
    }

    private void AddAuditLayer(Control dialog)
    {
        var layer = new CanvasLayer { Layer = 3, Name = "Audit" };
        layer.AddChild(dialog);
        AddChild(layer);
    }
}
