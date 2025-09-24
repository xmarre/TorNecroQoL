// 1) Helfer unten in TorCoreApi oder in derselben Klasse einfügen:
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem.Party;

private static int GetPartySizeLimitCompat(MobileParty party)
{
    if (party == null) return int.MaxValue;

    // try MobileParty.PartySizeLimit
    var mpProp = typeof(MobileParty).GetProperty("PartySizeLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (mpProp != null)
    {
        var val = mpProp.GetValue(party, null);
        int lim;
        if (TryExplainedToInt(val, out lim)) return lim;
        if (val is int iv) return iv;
    }

    // try PartyBase.MainParty.PartySizeLimit
    var pb = PartyBase.MainParty;
    if (pb != null)
    {
        var pbProp = typeof(PartyBase).GetProperty("PartySizeLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (pbProp != null)
        {
            var val2 = pbProp.GetValue(pb, null);
            int lim2;
            if (TryExplainedToInt(val2, out lim2)) return lim2;
            if (val2 is int iv2) return iv2;
        }
    }

    // Fallback: kein Trim
    return int.MaxValue;
}

private static bool TryExplainedToInt(object val, out int result)
{
    result = 0;
    if (val == null) return false;
    var t = val.GetType();

    var pRes = t.GetProperty("ResultNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (pRes != null)
    {
        var v = pRes.GetValue(val, null);
        if (v is float f) { result = (int)Math.Floor(f); return true; }
        if (v is int i) { result = i; return true; }
    }

    var pVal = t.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (pVal != null)
    {
        var v2 = pVal.GetValue(val, null);
        if (v2 is float f2) { result = (int)Math.Floor(f2); return true; }
        if (v2 is int i2) { result = i2; return true; }
    }

    return false;
}
