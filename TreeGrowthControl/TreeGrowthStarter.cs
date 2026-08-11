using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace TreeGrowthControl
{
    public class TreeGrowthStarter : IModStarter
    {
        private Harmony _harmony;
        internal static TreeGrowthStarter Instance;
        internal Dictionary<string, float> SpeciesMultipliers;
        internal bool GlobalEnabled = true;
        internal static string ConfigPath;

        internal static readonly Dictionary<string, float> DefaultGrowthDays = new Dictionary<string, float>
        {
            { "Birch", 9f },
            { "Pine", 12f },
            { "Maple", 24f },
            { "Oak", 30f },
            { "Chestnut", 24f },
            { "Mangrove", 15f }
        };

        public void StartMod(IModEnvironment modEnvironment)
        {
            Instance = this;
            ConfigPath = Path.Combine(modEnvironment.ModPath, "treegrowthcontrol.cfg");

            LoadConfig();

            _harmony = new Harmony("com.hoshizora.treegrowthcontrol");

            try
            {
                Type growableType = Type.GetType("Timberborn.Growing.Growable, Timberborn.Growing");
                if (growableType != null)
                {
                    var method = growableType.GetProperty("GrowthTimeInDays").GetGetMethod();
                    Debug.Log("[TreeGrowth] Target: " + method.DeclaringType.FullName + "." + method.Name);
                    var patchType = typeof(GrowableGrowthTimePatch);
                    if (patchType == null) { Debug.LogError("[TreeGrowth] typeof returned null!"); return; }
                    Debug.Log("[TreeGrowth] patchType=" + patchType.FullName);
                    var postfix = patchType.GetMethod("Postfix",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (postfix == null)
                    {
                        Debug.LogError("[TreeGrowth] Postfix method is null! Available methods:");
                        foreach (var m in patchType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
                            Debug.LogError("[TreeGrowth]   " + m.Name);
                        return;
                    }
                    Debug.Log("[TreeGrowth] Postfix found: " + postfix.Name);
                    _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    Debug.Log("[TreeGrowth] Patch applied successfully!");
                }
                else
                {
                    Debug.LogError("[TreeGrowth] Growable type NOT found!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TreeGrowth] Patch failed: " + ex.ToString());
            }

            Debug.Log("[TreeGrowth] TreeGrowthControl loaded! Config: " + ConfigPath);
        }

        void LoadConfig()
        {
            SpeciesMultipliers = new Dictionary<string, float>();
            foreach (var kvp in DefaultGrowthDays)
            {
                SpeciesMultipliers[kvp.Key] = 1.0f;
            }

            if (!File.Exists(ConfigPath))
            {
                SaveConfig();
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("#") || string.IsNullOrEmpty(trimmed))
                        continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    string key = trimmed.Substring(0, eq).Trim();
                    string val = trimmed.Substring(eq + 1).Trim();

                    if (key == "Enabled")
                    {
                        float ev;
                        if (float.TryParse(val, out ev))
                            GlobalEnabled = ev != 0;
                    }
                    else if (SpeciesMultipliers.ContainsKey(key))
                    {
                        float mv;
                        if (float.TryParse(val, out mv))
                            SpeciesMultipliers[key] = Math.Max(0.1f, Math.Min(10.0f, mv));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TreeGrowth] Failed to load config: " + ex.Message);
            }
        }

        internal void SaveConfig()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("# TreeGrowthControl Configuration");
                lines.Add("# Set multiplier per species. 1.0 = normal, 3.0 = 3x faster, 0.5 = 2x slower");
                lines.Add("");
                lines.Add("Enabled=" + (GlobalEnabled ? "1" : "0"));
                lines.Add("");
                foreach (var kvp in SpeciesMultipliers)
                {
                    lines.Add(string.Format("{0}={1:0.#}", kvp.Key, kvp.Value));
                }
                File.WriteAllLines(ConfigPath, lines.ToArray());
                Debug.Log("[TreeGrowth] Config saved: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[TreeGrowth] Failed to save config: " + ex.Message);
            }
        }
    }

    public static class Patches
    {
        public static string GetSpeciesId(object growable)
        {
            try
            {
                // Growable is not a MonoBehaviour; use its Name property ("Birch(Clone)")
                var nameProp = growable.GetType().GetProperty("Name");
                if (nameProp != null)
                {
                    string name = nameProp.GetValue(growable, null) as string;
                    if (name != null)
                    {
                        name = name.ToLower();
                        foreach (string species in TreeGrowthStarter.DefaultGrowthDays.Keys)
                        {
                            if (name.Contains(species.ToLower()))
                                return species;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    [HarmonyPatch(typeof(System.Object), "get_GrowthTimeInDays")]
    static class GrowableGrowthTimePatch
    {
        static int _callCount = 0;

        static MethodBase TargetMethod()
        {
            Type growableType = Type.GetType(
                "Timberborn.Growing.Growable, Timberborn.Growing");
            if (growableType == null) return null;
            var method = growableType.GetProperty("GrowthTimeInDays").GetGetMethod();
            Debug.Log("[TreeGrowth] TargetMethod resolved: " + method.DeclaringType.FullName + "." + method.Name);
            return method;
        }

        static void Postfix(object __instance, ref float __result)
        {
            _callCount++;
            bool firstFew = _callCount <= 10;

            if (!TreeGrowthStarter.Instance.GlobalEnabled)
            {
                if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": GlobalEnabled=false, skipping");
                return;
            }
            string species = Patches.GetSpeciesId(__instance);
            if (species == null)
            {
                if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": species=null result=" + __result);
                return;
            }

            float mult;
            if (!TreeGrowthStarter.Instance.SpeciesMultipliers.TryGetValue(species, out mult))
            {
                if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": unknown species=" + species);
                return;
            }

            float original = __result;
            __result = original / mult;

            if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": species=" + species + " original=" + original + " mult=" + mult + " result=" + __result);
        }
    }
}
