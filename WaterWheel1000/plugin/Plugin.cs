using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Timberborn.MechanicalSystem;

namespace WaterWheel1000
{
    /// <summary>
    /// 水轮无线供能插件：
    /// 当地图上存在水轮建筑时，强制将全图所有机械节点合并为同一个能量网络，
    /// 使水轮无需传动轴即可向任意耗电建筑供能。
    /// 能量输出锁定 1000 马力由配套 JSON 蓝图 mod（WaterWheel1000）负责。
    /// </summary>
    [BepInPlugin("WaterWheel1000", "Water Wheel 1000", "1.0.0")]
    [BepInProcess("Timberborn.exe")]
    public class Plugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private bool _patched;

        private void Awake()
        {
        }

        private void Update()
        {
            TryPatch();
        }

        private void TryPatch()
        {
            if (_patched)
            {
                return;
            }

            var managerType = AccessTools.TypeByName("Timberborn.MechanicalSystem.MechanicalGraphManager");
            if (managerType == null)
            {
                return; // 游戏程序集尚未加载，下一帧重试
            }

            _patched = true;
            _harmony = new Harmony("WaterWheel1000");

            _harmony.Patch(
                AccessTools.Method(managerType, "AddNode"),
                postfix: new HarmonyMethod(typeof(GraphManagerPatches)
                    .GetMethod(nameof(GraphManagerPatches.AddNodePostfix), BindingFlags.Static | BindingFlags.Public)));
            _harmony.Patch(
                AccessTools.Method(managerType, "RemoveNode"),
                postfix: new HarmonyMethod(typeof(GraphManagerPatches)
                    .GetMethod(nameof(GraphManagerPatches.RemoveNodePostfix), BindingFlags.Static | BindingFlags.Public)));

            Logger.LogInfo("[WaterWheel1000] Wireless power patch loaded.");
        }
    }

    public static class GraphManagerPatches
    {
        private const string WaterWheelMarker = "WaterWheel";

        private static object _transputMap;
        private static bool _transputMapResolved;

        /// <summary>AddNode 后：新节点已入 TransputMap，合并全图。</summary>
        public static void AddNodePostfix(object __instance, object mechanicalNode)
        {
            EnsureTransputMap(__instance);
            MergeAll(__instance, null);
        }

        /// <summary>RemoveNode 后：节点即将从 TransputMap 移除，显式排除该节点再合并。</summary>
        public static void RemoveNodePostfix(object __instance, object mechanicalNode)
        {
            EnsureTransputMap(__instance);
            MergeAll(__instance, mechanicalNode as MechanicalNode);
        }

        private static void EnsureTransputMap(object manager)
        {
            if (_transputMapResolved)
            {
                return;
            }

            _transputMap = manager == null ? null : Traverse.Create(manager).Field("_transputMap").GetValue();
            _transputMapResolved = true;
        }

        private static void MergeAll(object manager, MechanicalNode excludeNode)
        {
            if (_transputMap == null || manager == null)
            {
                return;
            }

            var factory = Traverse.Create(manager).Field("_mechanicalGraphFactory").GetValue();
            if (factory == null)
            {
                return;
            }

            var nodes = CollectNodes(excludeNode);
            if (nodes.Count == 0 || !HasWaterWheel(nodes))
            {
                return;
            }

            // 若所有节点已处于同一个能量网络，无需重建（避免重复触发图事件）。
            var graph = factory.GetType().GetMethod("Create")?.Invoke(factory, null);
            if (graph == null)
            {
                return;
            }

            bool allInSameGraph = true;
            object referenceGraph = null;
            foreach (var node in nodes)
            {
                var nodeGraph = Traverse.Create(node).Property("Graph").GetValue();
                if (referenceGraph == null)
                {
                    referenceGraph = nodeGraph;
                }
                else if (!ReferenceEquals(nodeGraph, referenceGraph))
                {
                    allInSameGraph = false;
                    break;
                }
            }

            if (allInSameGraph)
            {
                return;
            }

            var addNode = graph.GetType().GetMethod("AddNode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (addNode == null)
            {
                return;
            }

            foreach (var node in nodes)
            {
                addNode.Invoke(graph, new object[] { node });
            }
        }

        /// <summary>从 TransputMap 内部三维数组收集当前地图上所有机械节点。</summary>
        private static System.Collections.Generic.HashSet<MechanicalNode> CollectNodes(MechanicalNode excludeNode)
        {
            var result = new System.Collections.Generic.HashSet<MechanicalNode>();
            var array = Traverse.Create(_transputMap).Field("_transputs").GetValue() as System.Array;
            if (array == null || array.Rank != 3)
            {
                return result;
            }

            int x = array.GetLength(0);
            int y = array.GetLength(1);
            int z = array.GetLength(2);
            for (int i = 0; i < x; i++)
            {
                for (int j = 0; j < y; j++)
                {
                    for (int k = 0; k < z; k++)
                    {
                        var list = array.GetValue(i, j, k) as System.Collections.Generic.List<Transput>;
                        if (list == null || list.Count == 0)
                        {
                            continue;
                        }

                        foreach (var transput in list)
                        {
                            if (transput != null && transput.ParentNode != null &&
                                !ReferenceEquals(transput.ParentNode, excludeNode))
                            {
                                result.Add(transput.ParentNode);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>判断当前地图是否存在水轮发电机（名称含 WaterWheel 且为发电机）。</summary>
        private static bool HasWaterWheel(System.Collections.Generic.HashSet<MechanicalNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node == null || !node.IsGenerator)
                {
                    continue;
                }

                var name = node.Name;
                if (name != null && name.IndexOf(WaterWheelMarker, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
