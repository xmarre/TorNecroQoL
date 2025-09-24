// TorNecroQoLBehavior.cs  (C# 7.3)
using System;
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
        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Town → Graveyard
            starter.AddGameMenuOption("town", "tor_go_graveyard",
                "{=tor_go_grave}Go to the graveyard",
                Graveyard_Condition, _ => GameMenu.SwitchToMenu("tor_graveyard"), false, 2);

            // Graveyard menu
            starter.AddGameMenu("tor_graveyard",
                "{=tor_grave_desc}Weathered stones. Fresh soil. Whispers of the tethered.", null);

            starter.AddGameMenuOption("tor_graveyard", "tor_grave_raise",
                "{=tor_grave_raise}Raise dead from the corpses in the ground",
                _ => true, GraveyardRaise_Consequence, false, 1);

            starter.AddGameMenuOption("tor_graveyard", "tor_grave_leave",
                "{=str_leave}Leave", null, _ => GameMenu.SwitchToMenu("town"), true, 3);
        }

        private bool Graveyard_Condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Submenu;
            var s = Settlement.CurrentSettlement;
            return s != null && s.IsTown && PlayerQualifiesForNecromancy();
        }

        private void GraveyardRaise_Consequence(MenuCallbackArgs _)
        {
            var s = Settlement.CurrentSettlement;
            if (s == null) return;

            var raised = TorCoreApi.TryCalculateRaiseDeadTroops(s, MobileParty.MainParty);
            if (raised == null || raised.TotalManCount == 0)
            {
                MBInformationManager.AddQuickInformation(new TextObject("{=tor_none_to_raise}No corpses answer your call."));
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
                elems.Add(new InquiryElement(i, $"{label} x{e.Number}", null, true, e.Character.StringId));
            }

            int maxSel = elems.Count;

            var data = new MultiSelectionInquiryData(
                new TextObject("{=tor_pick_raised}Choose which risen to take").ToString(),
                new TextObject("{=tor_pick_raised_desc}Selected stacks join your party. Unselected stacks are sacrificed for Dark Energy.").ToString(),
                elems,
                true,                    // isExitShown
                0,                       // minSelectable
                maxSel,                  // maxSelectable
                GameTexts.FindText("str_done").ToString(),
                GameTexts.FindText("str_cancel").ToString(),
                // Affirmative
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
                        MBInformationManager.AddQuickInformation(
                            new TextObject("{=tor_trim_cap}Some risen could not be taken due to party size limit."));
                    }

                    var keptList = kept.GetTroopRoster();
                    for (int i = 0; i < keptList.Count; i++)
                    {
                        var e = keptList[i];
                        if (e.Character != null && e.Number > 0)
                            party.MemberRoster.AddToCounts(e.Character, e.Number);
                    }

                    TorCoreApi.TrySacrificeForDarkEnergy(where, discarded, "graveyard_not_taken");

                    MBInformationManager.AddQuickInformation(
                        new TextObject($"+{kept.TotalManCount} risen joined; {discarded.TotalManCount} sacrificed."));
                    GameMenu.SwitchToMenu("tor_graveyard");
                },
                // Negative → alles opfern
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
                    GameMenu.SwitchToMenu("tor_graveyard");
                },
                "",  // soundEventPath
                false);

            ShowMultiSelectionInquiryCompat(data);
        }

        private static void ShowMultiSelectionInquiryCompat(MultiSelectionInquiryData data)
        {
            var im = typeof(InformationManager);
            var mi = im.GetMethod("ShowMultiSelectionInquiry",
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                  null,
                                  new Type[] { typeof(MultiSelectionInquiryData), typeof(bool) },
                                  null);
            if (mi != null) { mi.Invoke(null, new object[] { data, true }); return; }

            mi = im.GetMethod("ShowMultiSelectionInquiry",
                              BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                              null,
                              new Type[] { typeof(MultiSelectionInquiryData) },
                              null);
            if (mi != null) { mi.Invoke(null, new object[] { data }); return; }

            MBInformationManager.AddQuickInformation(new TextObject("{=tor_ui_missing}Multi-selection UI not available in this build."));
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

        private bool PlayerQualifiesForNecromancy()
        {
            return true;
        }

        // -------- Helper hier als KLASSENMETHODEN, nicht top-level / nicht lokal --------
        private static int GetPartySizeLimitCompat(MobileParty party)
        {
            if (party == null) return int.MaxValue;

            // MobileParty.PartySizeLimit
            var mpProp = typeof(MobileParty).GetProperty("PartySizeLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mpProp != null)
            {
                var val = mpProp.GetValue(party, null);
                int lim;
                if (TryExplainedToInt(val, out lim)) return lim;
                if (val is int iv) return iv;
            }

            // PartyBase.MainParty.PartySizeLimit
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

        // --------- Raise Dead (graveyard) ----------
        public static TroopRoster TryCalculateRaiseDeadTroops(Settlement s, MobileParty party)
        {
            var tor = GetTorAsm();
            if (tor == null) return null;

            if (_raiseDeadStatic == null)
                _raiseDeadStatic = tor.GetType("TOR_Core.CampaignMechanics.RaiseDead");

            if (_raiseDeadStatic == null) return null;

            var methods = _raiseDeadStatic.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var list = new List<MethodInfo>();
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].Name == "CalculateRaiseDeadTroops") list.Add(methods[i]);
            if (list.Count == 0) return null;

            object[] pool = { s, party, Hero.MainHero, MobileParty.MainParty, PartyBase.MainParty, Settlement.CurrentSettlement, Campaign.Current };

            for (int k = 0; k < list.Count; k++)
            {
                var m = list[k];
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var want = ps[i].ParameterType;
                    object got = null;
                    for (int j = 0; j < pool.Length; j++)
                    {
                        var cand = pool[j];
                        if (cand != null && want.IsInstanceOfType(cand)) { got = cand; break; }
                    }
                    if (got == null) { ok = false; break; }
                    args[i] = got;
                }
                if (!ok) continue;

                var ret = m.Invoke(null, args);
                if (ret is TroopRoster tr) return tr;
            }
            return null;
        }

        // --------- Dark Energy (sacrifice) ----------
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

            var addMeths = _resMgrType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            MethodInfo addMeth = null;
            for (int i = 0; i < addMeths.Length; i++)
            {
                var mi = addMeths[i];
                if (mi.Name != "AddCustomResource") continue;
                var pars = mi.GetParameters();
                if ((pars.Length == 2 || pars.Length == 3) && pars[0].ParameterType == _customResType)
                { addMeth = mi; break; }
            }
            if (addMeth == null) return;

            var p = addMeth.GetParameters();
            if (addMeth.IsStatic)
            {
                if (p.Length == 2) addMeth.Invoke(null, new object[] { darkRes, gain.Value });
                else addMeth.Invoke(null, new object[] { darkRes, gain.Value, true });
            }
            else
            {
                if (p.Length == 2) addMeth.Invoke(mgr, new object[] { darkRes, gain.Value });
                else addMeth.Invoke(mgr, new object[] { darkRes, gain.Value, true });
            }

            MBInformationManager.AddQuickInformation(new TextObject($"+ Dark Energy [{context}]: {gain.Value}"));
        }

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
