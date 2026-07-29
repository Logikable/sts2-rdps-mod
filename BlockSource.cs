using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>What is granting a block gain, and to whose credit: a name for the row and the player it belongs to.</summary>
internal readonly record struct BlockGranter(string? Name, ulong? OwnerNetId);

/// <summary>
/// A power or relic putting block on somebody other than its own owner, for as long as it is doing so.
///
/// Everything else that grants block without a card either grants it to the creature that owns it - Plating, Rampart,
/// an orb's Frost - or is a potion, whose thrower <see cref="PotionSource"/> already knows. For those,
/// <see cref="BlockAttributionEngine"/> can credit the wearer and be right, which is exactly what it does when nothing
/// names an owner. Beacon of Hope is the case that breaks: it sits on one player and hands half of their block to their
/// teammates, so crediting the wearer credits the wrong person - the block was a gift, and the giver is who the Blocked
/// meter is meant to show.
///
/// The call stack cannot answer it. BlockSource's CallingModel recovers a *name* from the stack by matching
/// the frame's declaring type against the model database, which yields the prototype - and a prototype has no owner. The
/// live instance is only reachable from the hook itself, so a patch there records the owner while the grant runs.
///
/// One global stack rather than a dictionary per player, because the whole point is that the receiving player is not the
/// key: the credited player is what this supplies. LIFO is safe because the game awaits each listener's hook in turn, so
/// two players' Beacons are sequential rather than interleaved, and the pop happens only once the hook's own Task has
/// settled.
/// </summary>
internal static class ForeignBlockGrant
{
    private static readonly Stack<(string Effect, ulong GiverNetId)> Granting = new();
    private static readonly object Lock = new();

    public static void Push(string effect, ulong giverNetId)
    {
        lock (Lock)
        {
            Granting.Push((effect, giverNetId));
        }
    }

    public static void Pop()
    {
        lock (Lock)
        {
            if (Granting.Count > 0)
            {
                Granting.Pop();
            }
        }
    }

    public static (string Effect, ulong GiverNetId)? Current
    {
        get
        {
            lock (Lock)
            {
                return Granting.TryPeek(out (string Effect, ulong GiverNetId) top) ? top : null;
            }
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Granting.Clear();
        }
    }
}

/// <summary>
/// Names the relic, power or potion behind a block gain that no card explains, so the Blocked breakdown reads
/// "Orichalcum" or "Block Potion" rather than "(none)". A card is not this class's business: block from a card arrives
/// at Hook.ModifyBlock with its CardModel attached and names itself.
///
/// Everything else is anonymous by the time it gets there. CreatureCmd.GainBlock takes no choice context, so the game's
/// own executing-model stack (which <see cref="EffectSource"/> reads for damage) is out of reach, and the hooks these
/// grants fire from - BeforeSideTurnEnd for the end-of-turn relics, AfterSideTurnStart for Rampart and friends - are
/// among the ones the game never pushes a model onto that stack for anyway. What is still in reach is the call stack
/// itself, at the one moment it is intact: CreatureCmd.GainBlock is async, so only its synchronous entry still stands on
/// the granting model's frame - by the time the block is actually applied, several awaits later, that frame is gone.
/// So the name is captured in a prefix there and stashed until Hook.ModifyBlock settles the attribution.
///
/// A LIFO stack per creature rather than one slot, because a block hook can grant more block: the inner grant is named
/// and consumed inside the outer one, so pushes and pops nest exactly.
/// </summary>
internal static class BlockSource
{
    // A grant that gets as far as our prefix but never reaches Hook.ModifyBlock leaves its entry behind, so the stack is
    // capped: a stale name is only ever a wrong label, and the cap keeps a leak from growing without bound.
    private const int MaxDepth = 8;

    private static readonly Dictionary<Creature, Stack<BlockGranter>> ByCreature = new();
    private static readonly object Lock = new();

    /// <summary>
    /// Records what is granting this creature block. A potion outranks the call stack: a potion's own OnUse frame is the
    /// one the stack would name, and the thrower - who may not be the creature receiving the block - is the player to
    /// credit, which only <see cref="PotionSource"/> knows.
    ///
    /// Only ever called for a gain no card explains, which is also the only case <see cref="Take"/> is called for, so
    /// the two stay paired. That matters because Hook.ModifyBlock runs for card previews as well as for real gains - and
    /// a preview always carries the card it is previewing, so it never reaches either side of this stack.
    /// </summary>
    public static void Capture(Creature creature)
    {
        // A grant being made on someone else's behalf outranks both of the others, because it is the innermost of them
        // and the only one that knows the block is not the receiver's own. Drink a Block Potion with Beacon of Hope up
        // and both windows are open at once for the teammate's half: the potion named the gain that triggered Beacon,
        // Beacon is what granted this one.
        BlockGranter granter = ForeignBlockGrant.Current is (string effect, ulong giver)
            ? new BlockGranter(effect, giver)
            : PotionSource.Sole() is (ulong netId, string title)
            ? new BlockGranter(title, netId)
            : new BlockGranter(CallingModel(), null);

        lock (Lock)
        {
            if (!ByCreature.TryGetValue(creature, out Stack<BlockGranter>? stack))
            {
                stack = new Stack<BlockGranter>();
                ByCreature[creature] = stack;
            }

            if (stack.Count < MaxDepth)
            {
                stack.Push(granter);
            }
        }
    }

    /// <summary>The granter of the block gain now settling on this creature, consumed as it is read.</summary>
    public static BlockGranter Take(Creature creature)
    {
        lock (Lock)
        {
            if (!ByCreature.TryGetValue(creature, out Stack<BlockGranter>? stack) || stack.Count == 0)
            {
                return default;
            }

            BlockGranter granter = stack.Pop();
            if (stack.Count == 0)
            {
                ByCreature.Remove(creature);
            }

            return granter;
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByCreature.Clear();
        }
    }

    /// <summary>
    /// The name of the nearest model on the call stack. Every relic and power grants block by calling GainBlock from one
    /// of its own methods, so the first frame belonging to a model is the thing responsible. An async method's body runs
    /// inside a compiler-generated state machine nested in its declaring type, so a generated frame is walked out to the
    /// type that declares it. The model's prototype in the database carries the same title as the run's own copy, which
    /// spares us finding the live instance.
    /// </summary>
    private static string? CallingModel()
    {
        try
        {
            var trace = new StackTrace(fNeedFileInfo: false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                Type? type = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
                while (type is { DeclaringType: not null } && type.Name.StartsWith('<'))
                {
                    type = type.DeclaringType;
                }

                if (type == null || !type.IsSubclassOf(typeof(AbstractModel)) || !ModelDb.Contains(type))
                {
                    continue;
                }

                // GetByIdOrNull casts to its type argument without checking, so it is asked for the base type and the
                // concrete kind is recovered by matching - the same reason the fight labels ask for AbstractModel.
                return ModelDb.GetByIdOrNull<AbstractModel>(ModelDb.GetId(type)) switch
                {
                    PowerModel power => power.Title.GetFormattedText(),
                    RelicModel relic => relic.Title.GetFormattedText(),
                    PotionModel potion => potion.Title.GetFormattedText(),
                    OrbModel orb => orb.Title.GetFormattedText(),
                    CardModel card => card.TitleLocString.GetFormattedText(),
                    _ => null,
                };
            }
        }
        catch (Exception)
        {
            // A name is never worth breaking a block gain over.
        }

        return null;
    }
}
