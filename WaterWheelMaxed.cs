using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Timberborn.ModManagerScene;
using Timberborn.MechanicalSystem;
using UnityEngine;

namespace WaterWheelMaxed
{
    public class WaterWheelMaxedStarter : IModStarter
    {
        internal static string ConfigPath;
        internal bool Enabled = true;

        public void StartMod(IModEnvironment env)
        {
            ConfigPath = Path.Combine(env.ModPath, "powerwheelboost.cfg");
            LoadConfig();
            if (!Enabled) return;

            var harmony = new Harmony("com.hoshizora.waterwheelmaxed");

            try
            {
                var wpgType = Type.GetType("Timberborn.PowerGeneration.WaterPoweredGenerator, Timberborn.PowerGeneration");
                if (wpgType == null) { Debug.LogError("[WaterWheel] v18: WaterPoweredGenerator type not found!"); return; }

                // Patch Start: set MinRequiredOutflow=0 for efficiency=1.0
                var startMethod = wpgType.GetMethod("Start",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (startMethod != null)
                {
                    harmony.Patch(startMethod, postfix: new HarmonyMethod(typeof(Patch), "Start_Postfix"));
                    Debug.Log("[WaterWheel] v18: Start() postfix patched.");
                }

                // Patch UpdateGenerator: force multiplier=1.0, nominal=1000
                var updateMethod = wpgType.GetMethod("UpdateGenerator",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (updateMethod != null)
                {
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(typeof(Patch), "UpdateGenerator_Postfix"));
                    Debug.Log("[WaterWheel] v18: UpdateGenerator() postfix patched.");
                }

                Debug.Log("[WaterWheel] v18: All patches applied.");
            }
            catch (Exception ex) { Debug.LogError("[WaterWheel] v18 Error: " + ex.ToString()); }
        }

        void LoadConfig()
        {
            if (!File.Exists(ConfigPath)) { SaveConfig(); return; }
            try {
                foreach (string line in File.ReadAllLines(ConfigPath)) {
                    string t = line.Trim();
                    if (t.StartsWith("#") || string.IsNullOrEmpty(t)) continue;
                    int eq = t.IndexOf('='); if (eq < 0) continue;
                    if (t.Substring(0, eq).Trim() == "Enabled")
                        Enabled = !(t.Substring(eq + 1).Trim().ToLower() == "false" || t.Substring(eq + 1).Trim() == "0");
                }
            } catch { }
        }
        void SaveConfig() { File.WriteAllLines(ConfigPath, new[] { "## WaterWheelMaxed", "Enabled=true" }); }
    }

    static class Patch
    {
        private static int _postfixCount = 0;

        static void Start_Postfix(object __instance)
        {
            try
            {
                var wpgType = __instance.GetType();
                var specField = wpgType.GetField("_waterPoweredGeneratorSpec",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (specField == null) return;

                var spec = specField.GetValue(__instance);
                if (spec == null) return;

                var specType = spec.GetType();
                var outflowField = specType.GetField("<MinRequiredOutflow>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (outflowField != null)
                {
                    float old = (float)outflowField.GetValue(spec);
                    outflowField.SetValue(spec, 0f);
                    Debug.Log("[WaterWheel] v18 Start: MinRequiredOutflow " + old.ToString("F3") + " -> 0");
                }

                // Also set nominal to 1000 at Start for initial value
                var mechField = wpgType.GetField("_mechanicalNode", BindingFlags.NonPublic | BindingFlags.Instance);
                if (mechField != null)
                {
                    var node = mechField.GetValue(__instance);
                    if (node != null)
                    {
                        var nomField = typeof(MechanicalNode).GetField("_nominalPowerOutput",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (nomField != null)
                        {
                            int old = (int)nomField.GetValue(node);
                            nomField.SetValue(node, 1000);
                            Debug.Log("[WaterWheel] v18 Start: nominal " + old + " -> 1000");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[WaterWheel] v18 Start ERROR: " + ex.ToString());
            }
        }

        static void UpdateGenerator_Postfix(object __instance)
        {
            _postfixCount++;
            bool firstFew = _postfixCount <= 5;
            try
            {
                var wpgType = __instance.GetType();

                // 1. Force OutputMultiplier = 1.0
                var mechField = wpgType.GetField("_mechanicalNode",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (mechField == null) return;

                MechanicalNode node = mechField.GetValue(__instance) as MechanicalNode;
                if (node == null) return;

                var setMultMethod = typeof(MechanicalNode).GetMethod("SetOutputMultiplier",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (setMultMethod != null)
                {
                    setMultMethod.Invoke(node, new object[] { 1.0f });
                }

                // 2. Force nominal = 1000
                var nomField = typeof(MechanicalNode).GetField("_nominalPowerOutput",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (nomField != null)
                {
                    int oldNom = (int)nomField.GetValue(node);
                    nomField.SetValue(node, 1000);
                }

                // 3. Call UpdatePowerOutput to recalculate with new values
                var upoMethod = typeof(MechanicalNode).GetMethod("UpdatePowerOutput",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (upoMethod != null)
                {
                    upoMethod.Invoke(node, null);
                }

                // 4. Read back result for logging
                int powerOutput = 0;
                var actualsProp = typeof(MechanicalNode).GetProperty("Actuals");
                if (actualsProp != null)
                {
                    var actuals = actualsProp.GetValue(node);
                    var poField = typeof(MechanicalNodeActuals).GetField("<PowerOutput>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (poField != null) powerOutput = (int)poField.GetValue(actuals);
                }

                float eff = 0;
                var effProp = typeof(MechanicalNode).GetProperty("PowerEfficiency");
                if (effProp != null) eff = (float)effProp.GetValue(node);

                float mult = 0;
                var multProp = typeof(MechanicalNode).GetProperty("OutputMultiplier");
                if (multProp != null) mult = (float)multProp.GetValue(node);

                if (firstFew)
                    Debug.Log("[WaterWheel] v18 #" + _postfixCount +
                        ": output=" + powerOutput + " eff=" + eff.ToString("F3") +
                        " mult=" + mult.ToString("F3"));
            }
            catch (Exception ex)
            {
                if (_postfixCount <= 5)
                    Debug.LogError("[WaterWheel] v18 #" + _postfixCount + " ERR: " + ex.ToString());
            }
        }
    }
}
