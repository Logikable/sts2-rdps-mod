using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;

namespace RdpsMeter.Patches;

/// <summary>
/// Books the damage a pet soaks on its owner's behalf onto the Blocked meter, named after the pet - Osty, for the
/// Necrobinder, which is the only pet the game gives this power to today.
///
/// It belongs on the Blocked meter for the same reason block does: it is damage that was coming for you and did not
/// arrive. Summon is the Necrobinder's defensive stat the way Defend is everyone else's, and without this a Necrobinder
/// who spends the fight summoning instead of blocking reads as having mitigated nothing.
///
/// The mechanic is a redirect, not a pool. OstyCmd.Summon sets the pet's max HP and hangs DieForYouPower on it; that
/// power overrides ModifyUnblockedDamageTarget, so in the damage funnel - *after* the owner's block has been spent -
/// the unblocked remainder is retargeted from the owner onto the pet, which loses the HP in their place.
///
/// Three consequences worth knowing, all of them the game's rules rather than choices made here:
///
///  - It only fires for attacks. The redirect is gated on props.IsPoweredAttack(), so poison, burn and every other
///    non-attack HP loss goes straight through to the owner and never reaches this meter.
///  - A hit bigger than the pet carries on through. LoseHpInternal reports only the HP the pet actually had as
///    UnblockedDamage and the excess as OverkillDamage, which the funnel then deals to the owner for real. So the
///    absorbed amount is UnblockedDamage alone - the same line the damage side draws, where overkill is not damage
///    done, and for the same reason: mitigation the pet was never big enough to provide is not mitigation.
///  - Only *redirected* damage counts. A pet sits in CombatState.Allies, so an enemy sweep across the whole side hits
///    it as a target in its own right; that damage was never headed for the owner's HP, and crediting it would pad
///    Blocked with harm the owner never stood to take. Keying off the redirect excludes it by construction rather
///    than by a list of exceptions - which also excludes a pet the owner spends deliberately, like Sacrifice.
///
/// Two patches are needed because no single point knows both facts. ModifyUnblockedDamageTarget knows a redirect
/// happened but not what it will be worth; LoseHpInternal knows what was absorbed but not who it was absorbed for. So
/// the first notes the expectation and the second settles it, the same two-stage shape BlockPatches uses to carry an
/// attribution from where the modifiers are live to where the block actually lands.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyUnblockedDamageTarget))]
internal static class PetRedirectPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature originalTarget, Creature __result)
    {
        // A redirect, and one onto this very player's own pet: DieForYouPower only ever diverts its owner's owner, but
        // reading that off the result rather than trusting the power keeps this true of any future pet that does the
        // same. Enemy pets are not metered, so the owner must be a player.
        if (!ReferenceEquals(__result, originalTarget)
            && originalTarget.Player != null
            && ReferenceEquals(__result.PetOwner?.Creature, originalTarget)
            && CombatManager.Instance is { IsInProgress: true })
        {
            PetAbsorption.Expect(__result, originalTarget.Player);
        }
    }
}

/// <summary>
/// Where the pet's HP actually goes, and so where the absorbed amount is finally known. Every HP loss on every creature
/// comes through here, which is why the expectation filed by <see cref="PetRedirectPatch"/> is what makes one of them a
/// mitigation: without a pending redirect this is just a creature getting hurt.
/// </summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
internal static class PetAbsorptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance, DamageResult __result)
    {
        PetAbsorption.Settle(__instance, __result);
    }
}

/// <summary>
/// The redirects in flight, from the moment the funnel picks the pet to the moment the pet loses the HP.
///
/// A queue rather than a slot because the two ends are separated by an awaited hook (AfterModifyingHpLostAfterOsty), so
/// in principle a second hit can be routed at the pet before the first has settled; a queue keeps them paired in order
/// instead of letting the later one overwrite the earlier. Entries are consumed even when nothing was absorbed, so a
/// redirect onto a pet with no HP left cannot leave one behind for an unrelated hit to claim.
/// </summary>
internal static class PetAbsorption
{
    private static readonly Dictionary<Creature, Queue<Player>> Pending = new();
    private static readonly object Lock = new();

    public static void Expect(Creature pet, Player saved)
    {
        lock (Lock)
        {
            if (!Pending.TryGetValue(pet, out Queue<Player>? queue))
            {
                queue = new Queue<Player>();
                Pending[pet] = queue;
            }

            queue.Enqueue(saved);
        }
    }

    public static void Settle(Creature pet, DamageResult result)
    {
        Player? saved;
        lock (Lock)
        {
            if (!Pending.TryGetValue(pet, out Queue<Player>? queue) || queue.Count == 0)
            {
                return;
            }

            saved = queue.Dequeue();
            if (queue.Count == 0)
            {
                Pending.Remove(pet);
            }
        }

        // UnblockedDamage is the HP the pet really had to give. Anything past that is OverkillDamage, which the funnel
        // goes on to deal to the owner for real, so it mitigated nothing.
        decimal absorbed = result.UnblockedDamage;
        if (absorbed <= 0m)
        {
            return;
        }

        CombatLedger.Name(saved.NetId, PlayerIdentity.Name(saved));

        // Whose HP was it? Usually the owner's own, but an Osty can be built out of a teammate's Legion of Bone or a
        // Bone Brew thrown at you, so the absorb is split by who paid for the pet rather than by who stood behind it.
        // A pet with nothing recorded against it is the pre-PetPool behaviour: all of it to the owner.
        string name = NameOf(pet);
        IReadOnlyList<(ulong NetId, decimal Fraction)>? shares = PetPool.Shares(pet);
        if (shares == null)
        {
            CombatLedger.RecordBlock(saved.NetId, new[] { new BlockStrand(saved.NetId, name, absorbed) });
            return;
        }

        var strands = new List<BlockStrand>(shares.Count);
        foreach ((ulong netId, decimal fraction) in shares)
        {
            CombatLedger.Name(netId, PlayerIdentity.Name(netId));
            strands.Add(new BlockStrand(netId, name, absorbed * fraction));
        }

        CombatLedger.RecordBlock(saved.NetId, strands);
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Pending.Clear();
        }
    }

    /// <summary>
    /// What the row is called. The game's own name for the creature, so this reads "Osty" without the mod shipping the
    /// word at all - and reads whatever the player's language calls it in every other locale, the way card rows already
    /// take their name from the card. The id is the fallback rather than "(none)", since it is a real name and the only
    /// way to get here is a loc table that has lost the entry.
    /// </summary>
    private static string NameOf(Creature pet)
    {
        if (pet.Monster is not { } monster)
        {
            return AttributionEngine.UnknownSource;
        }

        string title = monster.Title.GetFormattedText();
        return string.IsNullOrWhiteSpace(title) ? monster.Id.Entry : title;
    }
}
