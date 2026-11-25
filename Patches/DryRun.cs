namespace RdpsMeter.Patches;

/// <summary>
/// Whether a call into the damage funnel's internals is the real one. From 0.114.0, <c>CreatureCmd</c> works each hit
/// out in a dry run before applying it - and card damage previews run the same dry run with no hit following - so
/// <c>Creature.DamageBlockInternal</c> and <c>Creature.LoseHpInternal</c> gained a trailing <c>bool dryRun</c> and are
/// called twice per real hit. A patch that books on them must skip the dry call or count everything twice.
///
/// Read off Harmony's <c>__args</c> rather than bound by name, because naming a parameter the target lacks fails the
/// patch outright, and older builds (every call real) lack it. The flag is always the last parameter; nothing else in
/// either signature is a bool.
/// </summary>
internal static class DryRun
{
    public static bool Of(object[]? args)
    {
        return args is { Length: > 0 } && args[^1] is true;
    }
}
