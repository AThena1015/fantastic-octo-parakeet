using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TreeGrowthControl
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class TreeGrowthControlPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.hoshizora.treegrowthcontrol";
        public const string PluginName = "Tree Growth Control";
        public const string PluginVersion = "1.0.0";

        private Harmony _harmony;
        internal static TreeGrowthControlPlugin Instance;
        internal ConfigFile ModConfig;
        internal Dictionary<string, float> SpeciesMultipliers;
        internal bool GlobalEnabled;

        // 默认树种的原始生长天数
        internal static readonly Dictionary<string, float> DefaultGrowthDays = new Dictionary<string, float>
        {
            { "Birch", 9f },
            { "Pine", 12f },
            { "Maple", 24f },
            { "Oak", 30f },
            { "Chestnut", 24f },
            { "Mangrove", 15f }
        };

        void Awake()
        {
            Instance = this;
            string configPath = Path.Combine(Paths.ConfigPath, "treegrowthcontrol.cfg");
            ModConfig = new ConfigFile(configPath, true);

            var globalToggle = ModConfig.Bind("General", "Enabled", true,
                "Enable/disable tree growth modification globally");
            GlobalEnabled = globalToggle.Value;
            globalToggle.SettingChanged += (sender, e) => { GlobalEnabled = globalToggle.Value; };

            SpeciesMultipliers = new Dictionary<string, float>();
            foreach (var kvp in DefaultGrowthDays)
            {
                string species = kvp.Key;
                string desc = "Growth speed multiplier for " + species +
                    " (1.0 = normal, 3.0 = 3x faster)";
                var cfg = ModConfig.Bind("Species", species, 1.0f,
                    new ConfigDescription(desc, new AcceptableValueRange<float>(0.1f, 10.0f)));
                SpeciesMultipliers[species] = cfg.Value;
                cfg.SettingChanged += (sender, e) =>
                {
                    SpeciesMultipliers[species] = cfg.Value;
                };
            }

            _harmony = new Harmony(PluginGuid);

            // Manually patch: PatchAll doesn't work with TargetMethod in nested classes
            try
            {
                Type growableType = Type.GetType("Timberborn.Growing.Growable, Timberborn.Growing");
                if (growableType != null)
                {
                    var method = growableType.GetProperty("GrowthTimeInDays").GetGetMethod();
                    Logger.LogInfo("Target: " + method.DeclaringType.FullName + "." + method.Name);
                    var postfix = typeof(Patches).GetNestedType("GrowableGrowthTimePatch",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        .GetMethod("Postfix",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    Logger.LogInfo("Patch applied successfully!");
                }
                else
                {
                    Logger.LogError("Growable type NOT found!");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Patch failed: " + ex.ToString());
            }

            Logger.LogInfo("TreeGrowthControl loaded!");
        }

        void OnDestroy()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }

    public static class Patches
    {
        public static string GetSpeciesId(object growable)
        {
            try
            {
                var mb = growable as MonoBehaviour;
                if (mb == null) return null;
                var go = mb.gameObject;
                if (go == null) return null;

                // 尝试通过 SpawnableResource 获取物种 ID
                Type srType = Type.GetType(
                    "Timberborn.NaturalResources.SpawnableResource, Timberborn.NaturalResources");
                if (srType != null)
                {
                    var sr = go.GetComponent(srType);
                    if (sr != null)
                    {
                        var idProp = sr.GetType().GetProperty("Id");
                        if (idProp != null)
                        {
                            return idProp.GetValue(sr, null) as string;
                        }
                    }
                }

                // 尝试通过名称匹配
                string name = go.name.ToLower();
                foreach (string species in TreeGrowthControlPlugin.DefaultGrowthDays.Keys)
                {
                    if (name.Contains(species.ToLower()))
                        return species;
                }
            }
            catch { }
            return null;
        }
    }

    [HarmonyPatch(
        typeof(System.Object),  // placeholder, replaced in static ctor
        "get_GrowthTimeInDays")]
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
            bool firstFew = _callCount <= 5;

            if (!TreeGrowthControlPlugin.Instance.GlobalEnabled)
            {
                if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": GlobalEnabled=false, skipping");
                return;
            }
            string species = Patches.GetSpeciesId(__instance);
            if (species == null)
            {
                if (firstFew) Debug.Log("[TreeGrowth] Postfix #" + _callCount + ": species=null for " + __instance.GetType().Name);
                return;
            }

            float mult;
            if (!TreeGrowthControlPlugin.Instance.SpeciesMultipliers.TryGetValue(species, out mult))
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