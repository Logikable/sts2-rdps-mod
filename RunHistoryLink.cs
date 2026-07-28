using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace RdpsMeter;

/// <summary>
/// Ties a map point on the run history page back to the combat the ledger filed for it.
///
/// The two sides have no key in common: the ledger files a combat under where the run was standing (act, map
/// coordinate, room), while a saved run history keeps only what happened - map point types, the rooms in them and their
/// monsters, with no coordinates at all. What they do share is order. Both list an act's rooms in the order they were
/// entered, so the nth fight of act N on the page is the nth combat the ledger recorded for act N. Counting per act
/// rather than across the whole run keeps a missing early fight (the mod installed mid-run, say) from shifting every
/// later act's numbering as well.
///
/// A run other than the one the ledger has loaded never resolves to a combat, however well the positions line up:
/// its numbers are simply not in memory, and showing the loaded run's damage under an older run's fight would be
/// worse than showing none.
/// </summary>
internal static class RunHistoryLink
{
    /// <summary>
    /// The fight at one map point, or null if that point is not a fight at all (a shop, a rest site, an event that
    /// never came to blows) and so has nothing for the meter to switch to.
    /// </summary>
    public static HistoryFight? Locate(RunHistory history, MapPointHistoryEntry point)
    {
        try
        {
            for (int act = 0; act < history.MapPointHistory.Count; act++)
            {
                int ordinal = 0;
                foreach (MapPointHistoryEntry candidate in history.MapPointHistory[act])
                {
                    if (ReferenceEquals(candidate, point))
                    {
                        MapPointRoomHistoryEntry? room = candidate.Rooms.FirstOrDefault(r => IsCombat(r.RoomType));
                        return room == null ? null : Describe(history, act, ordinal, room);
                    }

                    // A map point can hold more than one room - an event that turns into a fight, a boss with a
                    // treasure behind it - and the ledger files one combat per room, so count rooms, not points.
                    ordinal += candidate.Rooms.Count(r => IsCombat(r.RoomType));
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Could not match a run history map point to a fight: {ex}");
            return null;
        }
    }

    private static bool IsCombat(RoomType type)
    {
        return type is RoomType.Monster or RoomType.Elite or RoomType.Boss;
    }

    private static HistoryFight Describe(RunHistory history, int act, int ordinal, MapPointRoomHistoryEntry room)
    {
        CombatInfo? recorded = history.Seed == RunLedger.LoadedRunId ? RunLedger.FightInAct(act, ordinal) : null;

        // The ledger's own name for the fight wins when it has one: it is what the picker calls that fight everywhere
        // else in the meter. Otherwise the page's own record names it, which is all there is for a run we never saw.
        return recorded is CombatInfo fight && !string.IsNullOrEmpty(fight.Label)
            ? new HistoryFight(fight.Label, fight.Key)
            : new HistoryFight(NameOf(room), recorded?.Key);
    }

    // What to call a fight we have no tally for. Mirrors how a live combat is named - the encounter's own title first,
    // then the roster - except that a saved history carries no HP, so a mixed fight is named after the monster listed
    // first rather than the toughest one.
    private static string NameOf(MapPointRoomHistoryEntry room)
    {
        if (room.ModelId is ModelId id && Model(id) is EncounterModel encounter)
        {
            string title = encounter.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        var names = new List<string>();
        foreach (ModelId monsterId in room.MonsterIds)
        {
            if (Model(monsterId) is MonsterModel monster && monster.Title.GetFormattedText() is { } name
                && !string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return FightLabel.From(names);
    }

    // Looked up as the base model and pattern-matched afterwards: asking ModelDb for a specific subtype casts blindly,
    // so a room whose model id is an event rather than an encounter would throw rather than simply not match.
    private static AbstractModel? Model(ModelId id)
    {
        try
        {
            return ModelDb.GetByIdOrNull<AbstractModel>(id);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
