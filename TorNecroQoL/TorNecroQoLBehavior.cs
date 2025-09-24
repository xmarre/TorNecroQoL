// TorNecroQoLBehavior.cs  (C# 7.3)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace TorNecroQoL
{
    public sealed class TorNecroQoLBehavior : CampaignBehaviorBase
    {
        // enable to print exact gates that block graveyard raising
        private const bool DEBUG_RAISE_DIAGNOSTICS = false;
        private bool _graveyardPatched;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        }

        public override void SyncData(IDataStore dataStore) { _graveyardPatched = false; }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Do NOT add any new "Go to the graveyard" entry.
            // Only patch the existing TOR graveyard "raise" option consequence.
            _graveyardPatched = TryPatchExistingGraveyardRaiseOption();
        }

        private void OnTick(float dt)
        {
            if (_graveyardPatched) return;
            _graveyardPatched = TryPatchExistingGraveyardRaiseOption();
        }

        // --- Replace TOR's existing graveyard "raise" option consequence with our selection flow ---
        private bool TryPatchExistingGraveyardRaiseOption()
        {
            try
            {
                var gmm = Campaign.Current != null ? Campaign.Current.GameMenuManager : null;
                if (gmm == null) return false;

                var dict = GetMenusDictionary(gmm);
                if (dict == null) return false;

                foreach (var kv in dict)
                {
                    var menu = kv.Value;
                    if (menu == null) continue;

                    var options = GetMenuOptions(menu);
                    if (options == null) continue;

                    for (int i = 0; i < options.Count; i++)
                    {
                        var opt = options[i];
                        if (opt == null) continue;

                        var del = GetOptionConsequence(opt);
                        if (del == null || del.Method == null) continue;
                        if (del.Method.DeclaringType == GetType()) continue;

                        var owner = del.Method.DeclaringType != null ? del.Method.DeclaringType.FullName : "";
                        var mname = del.Method.Name ?? "";

                        bool isTorRaise =
                            owner.IndexOf("TOR_Core", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (owner.IndexOf("RaiseDead", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             mname.IndexOf("Raise", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             mname.IndexOf("Graveyard", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (!isTorRaise) continue;

                        var consType =
                            opt.GetType().GetField("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType
                            ?? opt.GetType().GetProperty("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType;
                        var myMi = GetType().GetMethod(nameof(InjectedGraveyardRaiseConsequence), BindingFlags.Instance | BindingFlags.NonPublic);
                        if (consType == null || myMi == null) continue;

                        var replacement = Delegate.CreateDelegate(consType, this, myMi);
                        if (SetOptionConsequence(opt, replacement))
                        {
                            if (DEBUG_RAISE_DIAGNOSTICS)
                                MBInformationManager.AddQuickInformation(new TextObject("Graveyard raise option hooked."));
                            return true; // patched
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage("[TorNecroQoL] Graveyard patch failed: " + ex.Message));
            }
            return false;
        }

        // Called when player presses the existing TOR "raise" option
        private void InjectedGraveyardRaiseConsequence(MenuCallbackArgs _)
        {
            _graveyardPatched = true;
            var s = Settlement.CurrentSettlement;
            if (s == null) return;

            var raised = TorCoreApi.TryCalculateRaiseDeadTroopsFromGraveyard(s, MobileParty.MainParty);
            if (raised == null || raised.TotalManCount == 0)
            {
                MBInformationManager.AddQuickInformation(new TextObject("{=tor_none_to_raise}No corpses answer your call."));
                if (DEBUG_RAISE_DIAGNOSTICS) TorCoreApi.DumpGraveyardRaiseDiagnostics(s);
                return;
            }

            ShowRaisedDeadSelection(s, raised);
        }

        private void ShowRaisedDeadSelection(Settlement where, TroopRoster raised)
        {
            var list = raised.GetTroopRoster();
            var elems = new List<InquiryElement>();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.Character == null || e.Number <= 0) continue;
                string label = e.Character.Name != null ? e.Character.Name.ToString() : e.Character.StringId;
                elems.Add(new InquiryElement(i, label + " x" + e.Number, null, true, e.Character.StringId));
            }

            int maxSel = elems.Count;

            var data = new MultiSelectionInquiryData(
                new TextObject("{=tor_pick_raised}Choose which risen to take").ToString(),
                new TextObject("{=tor_pick_raised_desc}Selected stacks join your party. Unselected stacks are sacrificed for Dark Energy.").ToString(),
                elems,
                true, 0, maxSel,
                GameTexts.FindText("str_done").ToString(),
                GameTexts.FindText("str_cancel").ToString(),
                // OK
                (selected) =>
                {
                    var kept = TroopRoster.CreateDummyTroopRoster();
                    var discarded = TroopRoster.CreateDummyTroopRoster();

                    var selIdx = new HashSet<int>();
                    for (int si = 0; si < selected.Count; si++)
                        selIdx.Add((int)selected[si].Identifier);

                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i];
                        if (e.Character == null || e.Number <= 0) continue;
                        if (selIdx.Contains(i)) kept.AddToCounts(e.Character, e.Number);
                        else discarded.AddToCounts(e.Character, e.Number);
                    }

                    var party = MobileParty.MainParty;
                    int limit = GetPartySizeLimitCompat(party);
                    int free = limit - party.MemberRoster.TotalManCount;
                    if (free < 0) free = 0;
                    if (free < kept.TotalManCount)
                    {
                        TrimRosterToCapacity(kept, free);
                        MBInformationManager.AddQuickInformation(new TextObject("{=tor_trim_cap}Some risen could not be taken due to party size limit."));
                    }

                    var keptList = kept.GetTroopRoster();
                    for (int i = 0; i < keptList.Count; i++)
                    {
                        var e = keptList[i];
                        if (e.Character != null && e.Number > 0)
                            party.MemberRoster.AddToCounts(e.Character, e.Number);
                    }

                    TorCoreApi.TrySacrificeForDarkEnergy(where, discarded, "graveyard_not_taken");
                    MBInformationManager.AddQuickInformation(new TextObject("+" + kept.TotalManCount + " risen joined; " + discarded.TotalManCount + " sacrificed."));
                    TorCoreApi.TryApplyGraveyardSideEffectsAfterRaise(where, MobileParty.MainParty);
                },
                // Cancel => sacrifice all
                (_negSelected) =>
                {
                    var discarded = TroopRoster.CreateDummyTroopRoster();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i];
                        if (e.Character != null && e.Number > 0)
                            discarded.AddToCounts(e.Character, e.Number);
                    }
                    TorCoreApi.TrySacrificeForDarkEnergy(Settlement.CurrentSettlement, discarded, "graveyard_cancelled");
                    TorCoreApi.TryApplyGraveyardSideEffectsAfterRaise(where, MobileParty.MainParty);
                },
                "", false);

            ShowMultiSelectionInquiryCompat(data);
        }

        private static void ShowMultiSelectionInquiryCompat(MultiSelectionInquiryData data)
        {
            var im = typeof(InformationManager);
            var mi = im.GetMethod("ShowMultiSelectionInquiry",
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                  null, new Type[] { typeof(MultiSelectionInquiryData), typeof(bool) }, null)
                  ?? im.GetMethod("ShowMultiSelectionInquiry",
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                  null, new Type[] { typeof(MultiSelectionInquiryData) }, null);
            if (mi != null)
            {
                var pars = mi.GetParameters();
                if (pars.Length == 2) mi.Invoke(null, new object[] { data, true });
                else mi.Invoke(null, new object[] { data });
            }
            else
            {
                MBInformationManager.AddQuickInformation(new TextObject("{=tor_ui_missing}Multi-selection UI not available in this build."));
            }
        }

        private static void TrimRosterToCapacity(TroopRoster roster, int capacity)
        {
            if (capacity <= 0) { roster.Clear(); return; }
            for (int i = roster.Count - 1; i >= 0 && roster.TotalManCount > capacity; i--)
            {
                var e = roster.GetElementCopyAtIndex(i);
                int toRemove = Math.Min(e.Number, roster.TotalManCount - capacity);
                roster.AddToCounts(e.Character, -toRemove);
            }
        }

        private bool PlayerQualifiesForNecromancy() { return true; }

        // ---- helpers for party limit and menu patching ----
        private static int GetPartySizeLimitCompat(MobileParty party)
        {
            if (party == null) return int.MaxValue;

            var mpProp = typeof(MobileParty).GetProperty("PartySizeLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mpProp != null)
            {
                var val = mpProp.GetValue(party, null);
                int lim;
                if (TryExplainedToInt(val, out lim)) return lim;
                if (val is int iv) return iv;
            }

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

        private static IDictionary<string, GameMenu> GetMenusDictionary(object gmm)
        {
            var t = gmm.GetType();
            // common private fields
            var f = t.GetField("_gameMenus", BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? t.GetField("gameMenus", BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? t.GetField("GameMenus",  BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(gmm) as IDictionary<string, GameMenu> : null;
        }

        private static IList<GameMenuOption> GetMenuOptions(GameMenu menu)
        {
            var t = typeof(GameMenu);
            // Options list often private
            var p = t.GetProperty("MenuOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return p.GetValue(menu, null) as IList<GameMenuOption>;
            var f = t.GetField("_menuOptions", BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? t.GetField("MenuOptions", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(menu) as IList<GameMenuOption> : null;
        }

        private static string GetOptionText(GameMenuOption opt)
        {
            var p = opt.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var to = p.GetValue(opt, null) as TextObject;
                if (to != null) return to.ToString();
            }
            return "";
        }

        private static string GetOptionId(GameMenuOption opt)
        {
            var p = opt.GetType().GetProperty("OptionId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 ?? opt.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var v = p != null ? p.GetValue(opt, null) : null;
            return v != null ? v.ToString() : "";
        }

        private static bool SetOptionConsequence(GameMenuOption opt, Delegate del)
        {
            var pf = opt.GetType().GetField("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pf != null) { pf.SetValue(opt, del); return true; }
            var pp = opt.GetType().GetProperty("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pp != null)
            {
                pp.SetValue(opt, del, null);
                return true;
            }
            return false;
        }

        private static Delegate GetOptionConsequence(GameMenuOption opt)
        {
            var f = opt.GetType().GetField("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(opt) as Delegate;
            var p = opt.GetType().GetProperty("OnConsequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null ? p.GetValue(opt, null) as Delegate : null;
        }
    }

    internal static class TorCoreApi
    {
        private static Assembly _torAsm;
        private static Type _raiseDeadStatic;
        private static Type _resMgrType;
        private static Type _customResType;

        private static Assembly GetTorAsm()
        {
            if (_torAsm == null)
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                _torAsm = asms.FirstOrDefault(a => a.GetName().Name == "TOR_Core");
            }
            return _torAsm;
        }

        // --- Graveyard raise with correct source binding ---
        public static TroopRoster TryCalculateRaiseDeadTroopsFromGraveyard(Settlement s, MobileParty party)
        {
            var tor = GetTorAsm();
            if (tor == null) return null;

            if (_raiseDeadStatic == null)
                _raiseDeadStatic = tor.GetType("TOR_Core.CampaignMechanics.RaiseDead");
            if (_raiseDeadStatic == null) return null;

            var ms = _raiseDeadStatic.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            // 1) Direct graveyard-named calculators
            for (int i = 0; i < ms.Length; i++)
            {
                var m = ms[i];
                var name = m.Name.ToLowerInvariant();
                if (!(name.Contains("grave") || name.Contains("graveyard"))) continue;
                if (!(name.Contains("calc") || name.Contains("get") || name.Contains("raise"))) continue;

                var args = BindArgs(m.GetParameters(), s, party);
                if (args == null) continue;
                try
                {
                    var ret = m.Invoke(null, args);
                    var tr = AsTroopRoster(ret);
                    if (tr != null && tr.TotalManCount > 0) return tr;
                }
                catch { }
            }

            // 2) Generic calculators + enum Source=Graveyard
            var generic = ms.Where(m => m.Name == "CalculateRaiseDeadTroops").ToArray();
            for (int i = 0; i < generic.Length; i++)
            {
                var m = generic[i];
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                bool ok = true;

                for (int j = 0; j < ps.Length; j++)
                {
                    var want = ps[j].ParameterType;

                    // Enum source -> pick Graveyard if present
                    if (want.IsEnum && want.Name.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var gv = EnumGetByNameContains(want, "Graveyard");
                        if (gv == null) gv = EnumGetByNameContains(want, "Grave");
                        if (gv != null) { args[j] = gv; continue; }
                    }

                    // Common bindings
                    object got =
                        (want.IsInstanceOfType(s) ? (object)s :
                        want.IsInstanceOfType(party) ? (object)party :
                        want.IsInstanceOfType(Hero.MainHero) ? (object)Hero.MainHero :
                        want.IsInstanceOfType(MobileParty.MainParty) ? (object)MobileParty.MainParty :
                        want.IsInstanceOfType(PartyBase.MainParty) ? (object)PartyBase.MainParty :
                        want.IsInstanceOfType(Settlement.CurrentSettlement) ? (object)Settlement.CurrentSettlement :
                        want.IsInstanceOfType(Campaign.Current) ? (object)Campaign.Current : null);

                    if (got == null) { ok = false; break; }
                    args[j] = got;
                }
                if (!ok) continue;

                try
                {
                    var ret = m.Invoke(null, args);
                    var tr = AsTroopRoster(ret);
                    if (tr != null && tr.TotalManCount > 0) return tr;
                }
                catch { }
            }

            return null;
        }

        private static TroopRoster AsTroopRoster(object ret)
        {
            if (ret == null) return null;
            if (ret is TroopRoster tr) return tr;

            var en = ret as IEnumerable;
            if (en == null) return null;

            var roster = TroopRoster.CreateDummyTroopRoster();
            foreach (var item in en)
            {
                var t = item.GetType();
                var k = t.GetProperty("Key") ?? t.GetProperty("Item1");
                var v = t.GetProperty("Value") ?? t.GetProperty("Item2");
                if (k == null || v == null) continue;
                var ch = k.GetValue(item, null) as CharacterObject;
                var numObj = v.GetValue(item, null);
                int n = (numObj is int) ? (int)numObj : (numObj is float) ? (int)Math.Round((float)numObj) : 0;
                if (ch != null && n > 0) roster.AddToCounts(ch, n);
            }
            return roster.TotalManCount > 0 ? roster : null;
        }

        private static object EnumGetByNameContains(Type enumType, string token)
        {
            var names = Enum.GetNames(enumType);
            for (int i = 0; i < names.Length; i++)
                if (names[i].IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return Enum.Parse(enumType, names[i]);
            return null;
        }

        private static object[] BindArgs(ParameterInfo[] ps, Settlement s, MobileParty party)
        {
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var want = ps[i].ParameterType;
                object got =
                    (want.IsEnum ? null :
                    want.IsInstanceOfType(s) ? (object)s :
                    want.IsInstanceOfType(party) ? (object)party :
                    want.IsInstanceOfType(Hero.MainHero) ? (object)Hero.MainHero :
                    want.IsInstanceOfType(MobileParty.MainParty) ? (object)MobileParty.MainParty :
                    want.IsInstanceOfType(PartyBase.MainParty) ? (object)PartyBase.MainParty :
                    want.IsInstanceOfType(Settlement.CurrentSettlement) ? (object)Settlement.CurrentSettlement :
                    want.IsInstanceOfType(Campaign.Current) ? (object)Campaign.Current : null);
                if (got == null) return null;
                args[i] = got;
            }
            return args;
        }

        // Sacrifice through TOR_Core CustomResourceManager
        public static void TrySacrificeForDarkEnergy(Settlement where, TroopRoster discarded, string context)
        {
            if (discarded == null || discarded.TotalManCount <= 0) return;

            var tor = GetTorAsm();
            if (tor == null) return;

            if (_resMgrType == null)
                _resMgrType = tor.GetType("TOR_Core.CampaignMechanics.CustomResources.CustomResourceManager");
            if (_customResType == null)
                _customResType = tor.GetType("TOR_Core.CampaignMechanics.CustomResources.CustomResource");
            if (_resMgrType == null || _customResType == null) return;

            object mgr = GetManagerInstance(_resMgrType);
            if (mgr == null) return;

            object darkRes = TryFindDarkEnergyResource(mgr);
            if (darkRes == null) return;

            int? gain = TryCalculateBattleStyleGain(mgr, discarded, where);
            if (gain == null || gain.Value <= 0) return;

            var add = _resMgrType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(mi => mi.Name == "AddCustomResource" &&
                                      mi.GetParameters().Length >= 2 &&
                                      mi.GetParameters()[0].ParameterType == _customResType);
            if (add == null) return;

            var pars = add.GetParameters();
            if (add.IsStatic)
            {
                if (pars.Length == 2) add.Invoke(null, new object[] { darkRes, gain.Value });
                else add.Invoke(null, new object[] { darkRes, gain.Value, true });
            }
            else
            {
                if (pars.Length == 2) add.Invoke(mgr, new object[] { darkRes, gain.Value });
                else add.Invoke(mgr, new object[] { darkRes, gain.Value, true });
            }

            MBInformationManager.AddQuickInformation(new TextObject("+ Dark Energy [" + context + "]: " + gain.Value));
        }

        public static void TryApplyGraveyardSideEffectsAfterRaise(Settlement s, MobileParty party)
        {
            var tor = GetTorAsm();
            if (tor == null || s == null) return;

            var rd = tor.GetType("TOR_Core.CampaignMechanics.RaiseDead");
            if (rd == null) return;

            var ms = rd.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string[] prefer =
            {
                "ApplyGraveyardRaise","CommitGraveyardRaise","ConsumeGraveyard","UseGraveyard",
                "StartGraveyardCooldown","BeginGraveyardCooldown","SetGraveyardCooldown",
                "StartCooldown","BeginCooldown","SetCooldown","CooldownGraveyard"
            };

            object[] pool = { s, party, Hero.MainHero, MobileParty.MainParty, PartyBase.MainParty, Campaign.Current };

            // First try side-effect methods without explicit hours
            for (int n = 0; n < prefer.Length; n++)
            {
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name.IndexOf(prefer[n], StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    bool ok = true;
                    for (int j = 0; j < ps.Length; j++)
                    {
                        var want = ps[j].ParameterType;

                        // If a numeric parameter looks like "hours", pass 8
                        if (want == typeof(int)) { args[j] = 8; continue; }
                        if (want == typeof(float)) { args[j] = 8f; continue; }

                        object got = null;
                        for (int k = 0; k < pool.Length; k++)
                        {
                            var cand = pool[k];
                            if (cand != null && want.IsInstanceOfType(cand)) { got = cand; break; }
                        }
                        if (got == null) { ok = false; break; }
                        args[j] = got;
                    }
                    if (!ok) continue;

                    try { m.Invoke(null, args); return; }
                    catch { }
                }
            }
        }

        // --- Diagnostics: tell you EXACTLY why graveyard raise fails (vamp gate, pool, cooldown, siege, etc.) ---
        public static void DumpGraveyardRaiseDiagnostics(Settlement s)
        {
            try
            {
                var tor = GetTorAsm();
                if (tor == null || s == null) return;

                // Try common checks exposed by TOR_Core.RaiseDead
                var rd = tor.GetType("TOR_Core.CampaignMechanics.RaiseDead");
                if (rd != null)
                {
                    TryLogBool(rd, "CanRaiseFromGraveyard", s, "CanRaiseFromGraveyard");
                    TryLogInt(rd, "GetGraveyardCorpseCount", s, "GraveyardCorpseCount");
                    TryLogBool(rd, "IsGraveyardOnCooldown", s, "GraveyardCooldown");
                }

                // Generic vampire gate
                var vampType = tor.GetType("TOR_Core.CampaignMechanics.Vampire.VampireManager")
                           ?? tor.GetType("TOR_Core.CampaignMechanics.VampireManager");
                if (vampType != null)
                    TryLogBool(vampType, "IsVampire", Hero.MainHero, "IsVampire(Hero)");

            }
            catch { /* silent */ }
        }

        private static void TryLogBool(Type t, string method, object arg, string label)
        {
            var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (mi == null) return;
            object inst = mi.IsStatic ? null : Activator.CreateInstance(t);
            var ok = mi.GetParameters().Length == 1 ? mi.Invoke(inst, new object[] { arg }) : null;
            if (ok is bool b)
                MBInformationManager.AddQuickInformation(new TextObject("[Diag] " + label + ": " + (b ? "true" : "false")));
        }

        private static void TryLogInt(Type t, string method, object arg, string label)
        {
            var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (mi == null) return;
            object inst = mi.IsStatic ? null : Activator.CreateInstance(t);
            var v = mi.GetParameters().Length == 1 ? mi.Invoke(inst, new object[] { arg }) : null;
            if (v is int iv)
                MBInformationManager.AddQuickInformation(new TextObject("[Diag] " + label + ": " + iv));
        }

        // --- internal helpers for custom resources ---
        private static object GetManagerInstance(Type mgrType)
        {
            var inst = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (inst != null) return inst.GetValue(null, null);
            inst = mgrType.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (inst != null) return inst.GetValue(null, null);
            var ctor = mgrType.GetConstructor(Type.EmptyTypes);
            return ctor != null ? Activator.CreateInstance(mgrType) : null;
        }

        private static object TryFindDarkEnergyResource(object mgr)
        {
            var typ = mgr.GetType();
            var getAll = typ.GetMethod("GetCustomResources", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (getAll == null) return null;

            var resEnum = getAll.IsStatic ? getAll.Invoke(null, null) : getAll.Invoke(mgr, null);
            var en = resEnum as System.Collections.IEnumerable;
            if (en == null) return null;

            foreach (var res in en)
            {
                string rid = GetStringProp(res, "StringId");
                if (rid == null) rid = GetStringProp(res, "Id");
                if (rid == null) rid = GetStringProp(res, "Name");
                if (rid == null) rid = res.ToString();

                if (rid != null &&
                    rid.IndexOf("dark", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    rid.IndexOf("energy", StringComparison.OrdinalIgnoreCase) >= 0)
                    return res;
            }
            return null;
        }

        private static string GetStringProp(object obj, string prop)
        {
            var p = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null ? (p.GetValue(obj, null)?.ToString()) : null;
        }

        private static int? TryCalculateBattleStyleGain(object mgr, TroopRoster discarded, Settlement where)
        {
            var typ = mgr.GetType();
            var calc = typ.GetMethod("CalculateCustomResourceGainFromBattles",
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (calc == null) return null;

            object[] pool = { MobileParty.MainParty, PartyBase.MainParty, Hero.MainHero, discarded, where, MapEvent.PlayerMapEvent, Campaign.Current };
            var ps = calc.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var want = ps[i].ParameterType;
                object got = null;
                for (int j = 0; j < pool.Length; j++)
                {
                    var cand = pool[j];
                    if (cand != null && want.IsInstanceOfType(cand)) { got = cand; break; }
                }
                if (got == null) return null;
                args[i] = got;
            }

            var result = calc.IsStatic ? calc.Invoke(null, args) : calc.Invoke(mgr, args);
            if (result == null) return null;

            var enumerable = result as System.Collections.IEnumerable;
            if (enumerable == null) return null;

            foreach (var item in enumerable)
            {
                var t = item.GetType();
                var keyProp = t.GetProperty("Key") ?? t.GetProperty("Item1");
                var valProp = t.GetProperty("Value") ?? t.GetProperty("Item2");
                if (keyProp == null || valProp == null) continue;

                var key = keyProp.GetValue(item, null);
                var val = valProp.GetValue(item, null);

                string rid = GetStringProp(key, "StringId");
                if (rid == null) rid = GetStringProp(key, "Id");
                if (rid == null) rid = GetStringProp(key, "Name");
                if (rid == null) rid = key != null ? key.ToString() : null;

                if (rid != null &&
                    rid.IndexOf("dark", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    rid.IndexOf("energy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (val is int iv) return Math.Max(0, iv);
                    if (val is float fv) return Math.Max(0, (int)Math.Round(fv));
                }
            }
            return null;
        }
    }
}
