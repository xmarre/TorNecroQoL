using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace TorNecroQoL
{
    internal static class TorResourceBridge
    {
        private static bool _resolved;
        private static Assembly _torAsm;

        private static MethodInfo _addCultureSpecific; // HeroExtensions.AddCultureSpecificCustomResource(Hero, float)
        private static MethodInfo _calcBattle;         // CustomResourceManager.CalculateCustomResourceGainFromBattles(MapEvent)
        private static MethodInfo _getCultureValue;    // HeroExtensions.GetCultureSpecificCustomResourceValue(Hero) - optional

        public static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                _torAsm = null;
                for (int i = 0; i < asms.Length; i++)
                {
                    var an = asms[i].GetName().Name;
                    if (an != null && an.IndexOf("TOR_Core", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _torAsm = asms[i];
                        break;
                    }
                }

                if (_torAsm == null)
                {
                    Logger.Info("[Resolve] TOR_Core not loaded.");
                    return;
                }

                Type heroExt = _torAsm.GetType("TOR_Core.Extensions.HeroExtensions");
                Type crm = _torAsm.GetType("TOR_Core.CampaignMechanics.CustomResources.CustomResourceManager");

                if (heroExt != null)
                {
                    _addCultureSpecific = heroExt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "AddCultureSpecificCustomResource") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 2 && ps[0].ParameterType == typeof(Hero) && IsNumeric(ps[1].ParameterType);
                        });

                    _getCultureValue = heroExt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "GetCultureSpecificCustomResourceValue") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 1 && ps[0].ParameterType == typeof(Hero);
                        });
                }

                if (crm != null)
                {
                    _calcBattle = crm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "CalculateCustomResourceGainFromBattles") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 1 && typeof(MapEvent).IsAssignableFrom(ps[0].ParameterType);
                        });
                }

                Logger.Info(string.Format(
                    "[Resolve] torLoaded={0} addCulture={1} calcBattle={2}",
                    (_torAsm != null),
                    (_addCultureSpecific != null),
                    (_calcBattle != null)));
            }
            catch (Exception ex)
            {
                Logger.Info("[Resolve EX] " + ex);
            }
        }

        private static bool IsNumeric(Type t)
        {
            return t == typeof(float) || t == typeof(double) || t == typeof(int);
        }

        public static bool TryAddDarkEnergy(Hero hero, float amount, out string reason)
        {
            reason = "";
            try
            {
                Resolve();
                if (hero == null) { reason = "no-hero"; return false; }
                if (_addCultureSpecific == null) { reason = "api-missing"; return false; }

                object amt = amount;
                var p1 = _addCultureSpecific.GetParameters()[1].ParameterType;
                if (p1 == typeof(int)) amt = (int)Math.Round(amount);
                else if (p1 == typeof(double)) amt = (double)amount;

                _addCultureSpecific.Invoke(null, new object[] { hero, amt });
                return true;
            }
            catch (Exception ex)
            {
                reason = "exception";
                Logger.Info("[TryAddDarkEnergy EX] " + ex);
                return false;
            }
        }

        public static bool TryCalcBattleGain(MapEvent mapEvent, out float amount, out string reason)
        {
            amount = 0f; reason = "";
            try
            {
                Resolve();
                if (mapEvent == null) { reason = "no-event"; return false; }

                // Prefer TOR_Core calculation if available
                if (_calcBattle != null)
                {
                    object target = _calcBattle.IsStatic ? null : Activator.CreateInstance(_calcBattle.DeclaringType);
                    object ret = _calcBattle.Invoke(target, new object[] { mapEvent });

                    if (ret == null) { reason = "calc-null"; }
                    else
                    {
                        // numeric directly
                        if (ret is int) { amount = (int)ret; return true; }
                        if (ret is float) { amount = (float)ret; return true; }
                        if (ret is double) { amount = (float)(double)ret; return true; }

                        // IDictionary<Hero, float> or similar
                        var dict = ret as IEnumerable;
                        if (dict != null)
                        {
                            foreach (var kv in dict)
                            {
                                var kvType = kv.GetType();
                                var keyProp = kvType.GetProperty("Key");
                                var valProp = kvType.GetProperty("Value");
                                if (keyProp != null && valProp != null)
                                {
                                    object key = keyProp.GetValue(kv, null);
                                    if (key == (object)Hero.MainHero)
                                    {
                                        object v = valProp.GetValue(kv, null);
                                        float? num = CastNumeric(v);
                                        if (num.HasValue) { amount = num.Value; return true; }
                                    }
                                }
                            }
                        }

                        // Object with TryGetValue(Hero, out float)
                        var tryGet = ret.GetType().GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (tryGet != null)
                        {
                            var ps = tryGet.GetParameters();
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(Hero))
                            {
                                object[] args;
                                if (ps[1].ParameterType == typeof(float).MakeByRefType())
                                {
                                    float outVal = 0f;
                                    args = new object[] { Hero.MainHero, outVal };
                                    bool ok = (bool)tryGet.Invoke(ret, args);
                                    if (ok) { amount = (float)args[1]; return true; }
                                }
                                else
                                {
                                    object outBox = Activator.CreateInstance(ps[1].ParameterType.GetElementType() ?? typeof(float));
                                    args = new object[] { Hero.MainHero, outBox };
                                    bool ok = (bool)tryGet.Invoke(ret, args);
                                    if (ok)
                                    {
                                        float? num = CastNumeric(args[1]);
                                        if (num.HasValue) { amount = num.Value; return true; }
                                    }
                                }
                            }
                        }

                        reason = "calc-unknown-return";
                    }
                }

                // Fallback: 1 DE per total death (both sides)
                amount = CountTotalDeaths(mapEvent);
                return true;
            }
            catch (Exception ex)
            {
                reason = "exception";
                Logger.Info("[TryCalcBattleGain EX] " + ex);
                return false;
            }
        }

        private static float? CastNumeric(object o)
        {
            try
            {
                if (o is int) return (int)o;
                if (o is float) return (float)o;
                if (o is double) return (float)(double)o;
                if (o == null) return null;
                var t = o.GetType();
                if (t == typeof(int)) return (int)o;
                if (t == typeof(float)) return (float)o;
                if (t == typeof(double)) return (float)(double)o;
                return null;
            }
            catch { return null; }
        }

        // ---------- helpers for fallback counting ----------
        public static bool PlayerWasInvolved(MapEvent me)
        {
            try
            {
                object[] sides = { GetProp(me, "AttackerSide"), GetProp(me, "DefenderSide") };
                for (int s = 0; s < sides.Length; s++)
                {
                    var side = sides[s];
                    if (side == null) continue;

                    var partiesObj = GetProp(side, "Parties");
                    if (partiesObj == null) partiesObj = GetProp(side, "ActiveParties");
                    if (partiesObj == null) partiesObj = GetProp(side, "AllParties");

                    var en = partiesObj as IEnumerable;
                    if (en == null) continue;

                    foreach (var entry in en)
                    {
                        object partyBase = entry;
                        var pb = GetProp(partyBase, "Party");
                        if (pb != null) partyBase = pb;
                        pb = GetProp(partyBase, "PartyBase");
                        if (pb != null) partyBase = pb;

                        var mobile = GetProp(partyBase, "MobileParty") as MobileParty;
                        if (mobile == MobileParty.MainParty) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static int CountTotalDeaths(MapEvent me)
        {
            int SumDeathsFromSide(object side)
            {
                int sum = 0;
                if (side == null) return 0;

                sum += SumIntMembers(side, "casual", "killed", "dead");

                var casualtiesList = GetProp(side, "Casualties");
                if (casualtiesList == null) casualtiesList = GetProp(side, "CasualtiesList");
                if (casualtiesList == null) casualtiesList = GetProp(side, "CasualtiesRoster");
                if (casualtiesList == null) casualtiesList = GetProp(side, "AllCasualties");

                sum += SumFromEnumerable(casualtiesList, "Killed", "Dead", "Deaths", "Casualties");

                var anyCas = side.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType) &&
                                         p.Name.IndexOf("casual", StringComparison.OrdinalIgnoreCase) >= 0);
                if (anyCas != null)
                    sum += SumFromEnumerable(anyCas.GetValue(side, null), "Killed", "Dead", "Deaths", "Casualties");

                return Math.Max(0, sum);
            }

            int a = SumDeathsFromSide(GetProp(me, "AttackerSide"));
            int d = SumDeathsFromSide(GetProp(me, "DefenderSide"));
            return a + d;
        }

        private static int SumIntMembers(object obj, params string[] nameHints)
        {
            int sum = 0;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var props = obj.GetType().GetProperties(flags);
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p.PropertyType == typeof(int))
                {
                    var nm = p.Name.ToLowerInvariant();
                    for (int h = 0; h < nameHints.Length; h++)
                    {
                        if (nm.IndexOf(nameHints[h], StringComparison.OrdinalIgnoreCase) >= 0)
                        { try { sum += (int)p.GetValue(obj, null); } catch { } break; }
                    }
                }
            }

            var fields = obj.GetType().GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.FieldType == typeof(int))
                {
                    var nm = f.Name.ToLowerInvariant();
                    for (int h = 0; h < nameHints.Length; h++)
                    {
                        if (nm.IndexOf(nameHints[h], StringComparison.OrdinalIgnoreCase) >= 0)
                        { try { sum += (int)f.GetValue(obj); } catch { } break; }
                    }
                }
            }
            return sum;
        }

        private static int SumFromEnumerable(object listObj, params string[] itemIntNames)
        {
            var en = listObj as IEnumerable;
            if (en == null) return 0;

            int sum = 0;
            foreach (var it in en)
            {
                var t = it.GetType();
                for (int n = 0; n < itemIntNames.Length; n++)
                {
                    var nm = itemIntNames[n];
                    var p = t.GetProperty(nm, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.PropertyType == typeof(int))
                    {
                        try { sum += (int)p.GetValue(it, null); } catch { }
                        break;
                    }
                    var f = t.GetField(nm, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(int))
                    {
                        try { sum += (int)f.GetValue(it); } catch { }
                        break;
                    }
                }
            }
            return sum;
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null ? p.GetValue(o, null) : null;
        }
    }
}
