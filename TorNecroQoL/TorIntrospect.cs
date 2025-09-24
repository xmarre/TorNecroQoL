using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;

namespace TorNecroQoL
{
    internal static class TorIntrospect
    {
        private static bool _done;

        public static void RunOnce()
        {
            if (_done) return;
            _done = true;
            try
            {
                Logger.Info("[INTROSPECT] start");
                var asms = AppDomain.CurrentDomain.GetAssemblies();

                // 1) Alle Assemblies, die TOR/Core enthalten, auflisten
                foreach (var a in asms)
                {
                    var an = a.FullName ?? "";
                    if (an.IndexOf("TOR", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Realms", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Core", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    Logger.Info("[INTROSPECT] asm: " + an);

                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }

                    // 2) Enums mit Member, der "Dark" + "Energy" enthält
                    var deEnums = new List<Type>();
                    foreach (var t in types)
                    {
                        if (!t.IsEnum) continue;
                        string[] names;
                        try { names = Enum.GetNames(t); } catch { continue; }
                        for (int i = 0; i < names.Length; i++)
                        {
                            var nm = names[i];
                            var low = nm.ToLowerInvariant();
                            if (low.Contains("dark") && low.Contains("energy"))
                            {
                                deEnums.Add(t);
                                Logger.Info("[INTROSPECT] enum: " + t.FullName + " member: " + nm);
                            }
                        }
                    }

                    // 3) Methoden, die (Hero/Clan) + (DE-Enum) + (Zahl) akzeptieren
                    foreach (var t in types)
                    {
                        MethodInfo[] ms;
                        try
                        {
                            ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                        }
                        catch { continue; }

                        for (int m = 0; m < ms.Length; m++)
                        {
                            var mi = ms[m];
                            var ps = mi.GetParameters();

                            bool usesEnum = false, hasOwner = false, hasNum = false;
                            for (int p = 0; p < ps.Length; p++)
                            {
                                var pt = ps[p].ParameterType;
                                if (pt == typeof(Hero) || pt == typeof(Clan)) hasOwner = true;
                                if (pt == typeof(int) || pt == typeof(float) || pt == typeof(double)) hasNum = true;
                                for (int e = 0; e < deEnums.Count; e++)
                                    if (pt == deEnums[e]) { usesEnum = true; break; }
                            }

                            if (usesEnum && hasOwner && hasNum)
                            {
                                Logger.Info("[INTROSPECT] candidate: " +
                                    t.FullName + "." + mi.Name +
                                    " static=" + mi.IsStatic +
                                    " params=" + string.Join(", ", ps.Select(p => p.ParameterType.FullName + " " + p.Name).ToArray()));
                            }

                            // 4) Namens-Treffer (zur Sicherheit)
                            if (mi.Name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                mi.Name.IndexOf("Energy", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Logger.Info("[INTROSPECT] namehit: " +
                                    t.FullName + "." + mi.Name +
                                    " static=" + mi.IsStatic +
                                    " params=" + string.Join(", ", ps.Select(p => p.ParameterType.FullName + " " + p.Name).ToArray()));
                            }
                        }
                    }
                }

                Logger.Info("[INTROSPECT] done");
            }
            catch (Exception ex)
            {
                Logger.Info("[INTROSPECT EX] " + ex);
            }
        }
    }
}
