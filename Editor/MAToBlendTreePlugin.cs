using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(MAToBlendTreePlugin))]

[RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
internal class MAToBlendTreePlugin : Plugin<MAToBlendTreePlugin>
{
    public override string QualifiedName => "com.puddingkc.ma2bt";
    public override string DisplayName => "MA2BT";
    public override Color? ThemeColor => new Color(0.55f, 0.2f, 0.85f, 1);

    protected override void Configure()
    {
        Sequence seq = InPhase(BuildPhase.Optimizing);
        seq.AfterPlugin("nadena.dev.modular-avatar");
        seq.WithRequiredExtension(typeof(AnimatorServicesContext), s =>
        {
            s.Run("MA2BT Optimize", ctx =>
            {
                var settings = ctx.AvatarRootObject.GetComponent<MAToBlendTree>();
                if (settings == null) return;

                var optimizer = new LayerToBlendTreeOptimizer(ctx, settings);
                optimizer.Process();

                Object.DestroyImmediate(settings, true);
            });
        });
    }
}

#region 数据

internal class AnalyzedLayer
{
    public VirtualLayer Layer;
    public bool IsConvertible;
    public string RejectReason;
    public string ParameterName;
    public bool IsInverted;
    public List<StateInfo> States = new();
    public int OriginalIndex;
    public bool IsExternalLayer;
}

internal class StateInfo
{
    public bool IsDefault;
    public float ThresholdLo = float.NaN;
    public float ThresholdHi = float.NaN;
    public VirtualMotion Motion;
    public List<SecondaryCondition> SecondaryConditions = new();
}

internal class SecondaryCondition
{
    public string ParameterName;
    public bool ActiveWhenGreater;
    public float Threshold = 0.5f;
}

internal class ResolvedState
{
    public StateInfo ActiveState;
    public StateInfo InactiveState;
    public List<SecondaryCondition> SecondaryConditions = new();
}

internal class ParameterGroup
{
    public string ParameterName;
    public List<AnalyzedLayer> Layers = new();
    public List<float> Thresholds = new();
}

#endregion


internal class LayerToBlendTreeOptimizer
{
    const string ROOT_PARAM = "zhz/1";
    const string BLEND_TREE_LAYER_NAME = "MA_To_BlendTree_Layer";
    const string MA_RESPONSIVE_PREFIX = "MA Responsive: ";

    readonly BuildContext _ctx;
    readonly MAToBlendTree _settings;
    readonly VirtualAnimatorController _fx;

    VirtualClip _sharedEmptyClip;

    public LayerToBlendTreeOptimizer(BuildContext ctx, MAToBlendTree settings)
    {
        _ctx = ctx;
        _settings = settings;
        var asc = ctx.Extension<AnimatorServicesContext>();
        _fx = asc.ControllerContext.Controllers[VRCAvatarDescriptor.AnimLayerType.FX];
    }

    public void Process()
    {
        var analyzedLayers = AnalyzeAllLayers();

        var convertibleLayers = analyzedLayers.Where(l => l.IsConvertible).ToList();
        if (convertibleLayers.Count == 0)
        {
            Debug.Log("[MA2BT] No convertible MA Responsive layers found, skipping.");
            return;
        }

        int rejectedCount = 0;
        foreach (var layer in analyzedLayers)
        {
            if (!layer.IsConvertible && layer.RejectReason != null)
            {
                Debug.Log($"[MA2BT] Keeping layer \"{layer.Layer.Name}\": {layer.RejectReason}");
                rejectedCount++;
            }
        }

        int externalCount = convertibleLayers.Count(l => l.IsExternalLayer);
        string externalNote = externalCount > 0 ? $" (including {externalCount} non-MA layers)" : "";
        Debug.Log($"[MA2BT] Found {convertibleLayers.Count} convertible layers{externalNote}, {rejectedCount} kept layers.");

        // 按参数分组
        var paramGroups = GroupByParameter(convertibleLayers);

        // 构建混合树
        var rootBlendTree = BuildRootBlendTree(paramGroups);

        // 注入到 FX
        EnsureFloatParameter(ROOT_PARAM, 1f);
        InjectBlendTreeLayer(rootBlendTree);

        // 移除转换后的层
        var layersToRemove = new HashSet<VirtualLayer>(convertibleLayers.Select(l => l.Layer));
        _fx.RemoveLayers(l => layersToRemove.Contains(l));

        Debug.Log($"[MA2BT] Done: merged {convertibleLayers.Count} layers into {paramGroups.Count} BlendTree nodes.");
        foreach (var group in paramGroups)
        {
            Debug.Log($"[MA2BT]   Parameter \"{group.ParameterName}\": " +
              $"{group.Layers.Count} layers > {group.Thresholds.Count} thresholds " +
              $"[{string.Join(", ", group.Thresholds)}]");
        }
    }

    #region 扫描

    List<AnalyzedLayer> AnalyzeAllLayers()
    {
        var results = new List<AnalyzedLayer>();
        int index = 0;

        foreach (var layer in _fx.Layers)
        {
            bool isMALayer = layer.Name != null && layer.Name.StartsWith(MA_RESPONSIVE_PREFIX);
            bool shouldAnalyze = isMALayer || _settings.scanAllLayers;

            if (shouldAnalyze && layer.Name != BLEND_TREE_LAYER_NAME)
            {
                var analyzed = AnalyzeLayer(layer, index);
                if (!isMALayer) analyzed.IsExternalLayer = true;
                results.Add(analyzed);
            }
            index++;
        }

        return results;
    }

    AnalyzedLayer AnalyzeLayer(VirtualLayer layer, int index)
    {
        var result = new AnalyzedLayer
        {
            Layer = layer,
            OriginalIndex = index,
            IsConvertible = false
        };

        var sm = layer.StateMachine;
        if (sm == null)
        {
            result.RejectReason = "No state machine";
            return result;
        }

        var states = sm.States;
        if (states.Count < 2)
        {
            result.RejectReason = $"Insufficient state count ({states.Count})";
            return result;
        }

        var defaultState = sm.DefaultState;
        if (defaultState == null)
        {
            result.RejectReason = "No default state";
            return result;
        }

        var conditionalStates = states.Where(cs => cs.State != defaultState).ToList();

        if (!_settings.convertMultiState && conditionalStates.Count > 1)
        {
            result.RejectReason = $"Multi-state layer ({conditionalStates.Count} conditional states), enable convertMultiState";
            return result;
        }

        var entryTransitions = sm.EntryTransitions;
        if (entryTransitions.Count == 0)
        {
            result.RejectReason = "No Entry Transition";
            return result;
        }

        string paramName = null;
        bool isInverted = false;
        var stateInfos = new List<StateInfo>();

        stateInfos.Add(new StateInfo
        {
            IsDefault = true,
            Motion = defaultState.Motion
        });

        foreach (var cs in conditionalStates)
        {
            var state = cs.State;

            var entryTrans = entryTransitions.Where(t => t.DestinationState == state).ToList();
            if (entryTrans.Count == 0)
            {
                result.RejectReason = $"State \"{state.Name}\" has no corresponding Entry Transition";
                return result;
            }

            var analysisResult = AnalyzeTransitionConditions(entryTrans, paramName);
            if (!analysisResult.Success)
            {
                result.RejectReason = analysisResult.Reason;
                return result;
            }

            if (paramName == null)
            {
                paramName = analysisResult.ParameterName;
                isInverted = analysisResult.IsInverted;
            }
            else if (paramName != analysisResult.ParameterName)
            {
                result.RejectReason = $"Multiple parameters detected: \"{paramName}\" and \"{analysisResult.ParameterName}\"";
                return result;
            }

            foreach (var t in state.Transitions)
            {
                if (t.Duration != 0 || t.ExitTime.HasValue)
                {
                    result.RejectReason = "Non-instant Transition";
                    return result;
                }
            }

            stateInfos.Add(new StateInfo
            {
                IsDefault = false,
                ThresholdLo = analysisResult.ThresholdLo,
                ThresholdHi = analysisResult.ThresholdHi,
                Motion = state.Motion,
                SecondaryConditions = analysisResult.SecondaryConditions
            });
        }

        if (string.IsNullOrEmpty(paramName))
        {
            result.RejectReason = "Failed to extract parameter name";
            return result;
        }

        result.IsConvertible = true;
        result.ParameterName = paramName;
        result.IsInverted = isInverted;
        result.States = stateInfos;
        return result;
    }

    TransitionAnalysisResult AnalyzeTransitionConditions(
        List<VirtualTransition> entryTransitions,
        string expectedParam)
    {

        if (entryTransitions.Count == 1)
        {
            var conditions = entryTransitions[0].Conditions;
            return AnalyzeSingleTransitionConditions(conditions, false);
        }
        else
        {
            var allConditions = new List<AnimatorCondition>();
            foreach (var t in entryTransitions)
            {
                if (t.Conditions.Count != 1)
                {
                    return TransitionAnalysisResult.Fail(
                        $"In inverted mode, Entry Transition has {t.Conditions.Count} conditions (expected 1)");
                }
                allConditions.Add(t.Conditions[0]);
            }

            var paramNames = allConditions.Select(c => c.parameter).Distinct().ToList();
            if (paramNames.Count != 1)
            {
                return TransitionAnalysisResult.Fail(
                    $"Multiple parameters in inverted mode: {string.Join(", ", paramNames)}");
            }

            var invertedConditions = allConditions.Select(c => new AnimatorCondition
            {
                parameter = c.parameter,
                mode = c.mode == AnimatorConditionMode.Greater
                    ? AnimatorConditionMode.Less
                    : AnimatorConditionMode.Greater,
                threshold = c.threshold
            }).ToImmutableList();

            return AnalyzeSingleTransitionConditions(invertedConditions, true);
        }
    }

    TransitionAnalysisResult AnalyzeSingleTransitionConditions(
        ImmutableList<AnimatorCondition> conditions,
        bool isInverted)
    {
        if (conditions.Count == 0)
            return TransitionAnalysisResult.Fail("No conditions");

        if (isInverted && conditions.Select(c => c.parameter).Distinct().Count() != 1)
        {
            return TransitionAnalysisResult.Fail(
                "Inverted multi-parameter AND conditions are not supported");
        }

        var groupedConditions = conditions.GroupBy(c => c.parameter).ToList();
        var parameterRanges = new List<(string ParameterName, float Lo, float Hi)>();

        foreach (var group in groupedConditions)
        {
            float lo = float.NegativeInfinity;
            float hi = float.PositiveInfinity;

            foreach (var cond in group)
            {
                switch (cond.mode)
                {
                    case AnimatorConditionMode.Greater:
                        lo = Math.Max(lo, cond.threshold);
                        break;
                    case AnimatorConditionMode.Less:
                        hi = Math.Min(hi, cond.threshold);
                        break;
                    case AnimatorConditionMode.Equals:
                    case AnimatorConditionMode.NotEqual:
                        return TransitionAnalysisResult.Fail(
                            $"Unsupported condition mode: {cond.mode}");
                    case AnimatorConditionMode.If:
                        lo = Math.Max(lo, 0.5f);
                        break;
                    case AnimatorConditionMode.IfNot:
                        hi = Math.Min(hi, 0.5f);
                        break;
                }
            }

            if (lo >= hi)
            {
                return TransitionAnalysisResult.Fail(
                    $"Invalid condition range for parameter \"{group.Key}\": {lo}..{hi}");
            }

            parameterRanges.Add((group.Key, lo, hi));
        }

        var mainRange = parameterRanges
            .Where(r => !r.ParameterName.StartsWith("__ActiveSelfProxy", StringComparison.Ordinal)
                && float.IsFinite(r.Lo)
                && float.IsFinite(r.Hi))
            .OrderByDescending(r => r.Hi - r.Lo)
            .FirstOrDefault();

        if (mainRange.ParameterName == null)
        {
            mainRange = parameterRanges
                .Where(r => float.IsFinite(r.Lo) && float.IsFinite(r.Hi))
                .OrderBy(r => r.ParameterName.StartsWith("__ActiveSelfProxy", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(r => r.Hi - r.Lo)
                .FirstOrDefault();
        }

        if (mainRange.ParameterName == null)
        {
            mainRange = parameterRanges
                .Where(r => float.IsFinite(r.Lo) || float.IsFinite(r.Hi))
                .OrderBy(r => r.ParameterName.StartsWith("__ActiveSelfProxy", StringComparison.Ordinal) ? 1 : 0)
                .FirstOrDefault();
        }

        if (mainRange.ParameterName == null)
        {
            return TransitionAnalysisResult.Fail("Failed to identify main parameter");
        }

        var secondaryConditions = new List<SecondaryCondition>();
        foreach (var range in parameterRanges)
        {
            if (range.ParameterName == mainRange.ParameterName) continue;

            bool hasLower = float.IsFinite(range.Lo);
            bool hasUpper = float.IsFinite(range.Hi);
            if (hasLower == hasUpper)
            {
                return TransitionAnalysisResult.Fail(
                    $"Secondary parameter \"{range.ParameterName}\" must be a simple boolean condition");
            }

            secondaryConditions.Add(new SecondaryCondition
            {
                ParameterName = range.ParameterName,
                ActiveWhenGreater = hasLower,
                Threshold = hasLower ? range.Lo : range.Hi
            });
        }

        return new TransitionAnalysisResult
        {
            Success = true,
            ParameterName = mainRange.ParameterName,
            ThresholdLo = mainRange.Lo,
            ThresholdHi = mainRange.Hi,
            IsInverted = isInverted,
            SecondaryConditions = secondaryConditions
        };
    }

    struct TransitionAnalysisResult
    {
        public bool Success;
        public string Reason;
        public string ParameterName;
        public float ThresholdLo;
        public float ThresholdHi;
        public bool IsInverted;
        public List<SecondaryCondition> SecondaryConditions;

        public static TransitionAnalysisResult Fail(string reason) =>
            new TransitionAnalysisResult { Success = false, Reason = reason };
    }

    #endregion

    #region 分组

    List<ParameterGroup> GroupByParameter(List<AnalyzedLayer> layers)
    {
        var groups = new Dictionary<string, ParameterGroup>();

        foreach (var layer in layers.OrderBy(l => l.OriginalIndex))
        {
            if (!groups.TryGetValue(layer.ParameterName, out var group))
            {
                group = new ParameterGroup { ParameterName = layer.ParameterName };
                groups[layer.ParameterName] = group;
            }
            group.Layers.Add(layer);
        }

        foreach (var group in groups.Values)
        {
            ComputeThresholds(group);
            EnsureFloatParameter(group.ParameterName);

            foreach (var secondaryParam in group.Layers
                .SelectMany(l => l.States)
                .SelectMany(s => s.SecondaryConditions)
                .Select(c => c.ParameterName)
                .Distinct())
            {
                EnsureFloatParameter(secondaryParam);
            }
        }

        return groups.Values.ToList();
    }

    void ComputeThresholds(ParameterGroup group)
    {
        var valueSet = new HashSet<float>();

        foreach (var layer in group.Layers)
        {
            foreach (var state in layer.States)
            {
                if (state.IsDefault) continue;

                float lo = state.ThresholdLo;
                float hi = state.ThresholdHi;

                if (float.IsFinite(lo) && float.IsFinite(hi))
                {
                    float center = (lo + hi) / 2f;
                    valueSet.Add(Mathf.Round(center));
                    AddNearbyThresholdSamples(valueSet, lo, hi);
                }
                else if (float.IsFinite(lo))
                {
                    valueSet.Add(Mathf.Round(lo + 0.5f));
                    valueSet.Add(Mathf.Round(lo - 0.5f));
                }
                else if (float.IsFinite(hi))
                {
                    valueSet.Add(Mathf.Round(hi - 0.5f));
                    valueSet.Add(Mathf.Round(hi + 0.5f));
                }
            }
        }

        valueSet.Add(0);

        valueSet.RemoveWhere(v => v < 0 || float.IsNaN(v) || float.IsInfinity(v));

        if (_settings.compactMode)
        {
            group.Thresholds = GetCompactThresholds(valueSet);
        }
        else
        {
            int max = (int)Math.Max(1, valueSet.Max());
            group.Thresholds = Enumerable.Range(0, max + 1)
                .Select(i => (float)i)
                .Union(valueSet)
                .ToList();
        }

        group.Thresholds.Sort();
    }

    void AddNearbyThresholdSamples(HashSet<float> valueSet, float lo, float hi)
    {
        if (float.IsFinite(lo))
        {
            valueSet.Add(Mathf.Round(lo + 0.5f));
            valueSet.Add(Mathf.Round(lo - 0.5f));
        }

        if (float.IsFinite(hi))
        {
            valueSet.Add(Mathf.Round(hi - 0.5f));
            valueSet.Add(Mathf.Round(hi + 0.5f));
        }
    }

    List<float> GetCompactThresholds(HashSet<float> existingKeys)
    {
        var sorted = existingKeys.OrderBy(x => x).ToList();
        var result = new List<float>();

        for (int idx = 0; idx < sorted.Count; idx++)
        {
            float current = sorted[idx];
            result.Add(current);

            if (idx < sorted.Count - 1)
            {
                float next = sorted[idx + 1];
                if (next - current > 1)
                {
                    result.Add(current + 1);
                    result.Add(next - 1);
                }
            }
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }

    #endregion

    #region 混合树生成

    VirtualBlendTree BuildRootBlendTree(List<ParameterGroup> paramGroups)
    {
        var rootTree = VirtualBlendTree.Create("RootBlendTree");
        rootTree.BlendType = BlendTreeType.Direct;
        rootTree.BlendParameter = ROOT_PARAM;
        rootTree.BlendParameterY = ROOT_PARAM;

        foreach (var group in paramGroups)
        {
            var paramTree = BuildParameterBlendTree(group);
            rootTree.Children = rootTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = paramTree,
                    DirectBlendParameter = ROOT_PARAM
                });
        }

        return rootTree;
    }

    VirtualBlendTree BuildParameterBlendTree(ParameterGroup group)
    {
        var paramTree = VirtualBlendTree.Create(SanitizeName(group.ParameterName));
        paramTree.BlendType = BlendTreeType.Simple1D;
        paramTree.BlendParameter = group.ParameterName;
        paramTree.UseAutomaticThresholds = false;

        foreach (float threshold in group.Thresholds)
        {
            var motion = BuildMotionForThreshold(group, threshold);

            paramTree.Children = paramTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = motion,
                    Threshold = threshold
                });
        }

        return paramTree;
    }

    VirtualMotion BuildMotionForThreshold(ParameterGroup group, float threshold)
    {
        var unconditionalClip = VirtualClip.Create($"{SanitizeName(group.ParameterName)}_t{threshold}");
        bool hasUnconditionalCurves = false;
        var gatedMotions = new List<(VirtualMotion ActiveMotion, VirtualMotion InactiveMotion, List<SecondaryCondition> Conditions)>();

        foreach (var layer in group.Layers)
        {
            var resolved = ResolveStateForThreshold(layer, threshold);
            if (resolved == null || resolved.ActiveState?.Motion == null) continue;

            if (resolved.SecondaryConditions.Count == 0)
            {
                if (MergeMotionIntoClip(unconditionalClip, resolved.ActiveState.Motion))
                    hasUnconditionalCurves = true;
            }
            else
            {
                gatedMotions.Add((
                    resolved.ActiveState.Motion,
                    resolved.InactiveState?.Motion ?? GetSharedEmptyClip(),
                    resolved.SecondaryConditions));
            }
        }

        var baseMotion = hasUnconditionalCurves ? (VirtualMotion)unconditionalClip : GetSharedEmptyClip();
        if (gatedMotions.Count == 0)
            return baseMotion;

        var directTree = VirtualBlendTree.Create($"{SanitizeName(group.ParameterName)}_t{threshold}_AND");
        directTree.BlendType = BlendTreeType.Direct;
        directTree.BlendParameter = ROOT_PARAM;
        directTree.BlendParameterY = ROOT_PARAM;
        directTree.Children = directTree.Children.Add(
            new VirtualBlendTree.VirtualChildMotion
            {
                Motion = baseMotion,
                DirectBlendParameter = ROOT_PARAM
            });

        int index = 0;
        foreach (var gated in gatedMotions)
        {
            var gatedMotion = BuildSecondaryConditionTree(
                gated.ActiveMotion,
                gated.InactiveMotion,
                gated.Conditions,
                $"{SanitizeName(group.ParameterName)}_t{threshold}_gate{index}");

            directTree.Children = directTree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = gatedMotion,
                    DirectBlendParameter = ROOT_PARAM
                });
            index++;
        }

        return directTree;
    }

    VirtualMotion BuildSecondaryConditionTree(
        VirtualMotion activeMotion,
        VirtualMotion inactiveMotion,
        List<SecondaryCondition> conditions,
        string name)
    {
        VirtualMotion current = activeMotion;

        for (int i = conditions.Count - 1; i >= 0; i--)
        {
            var condition = conditions[i];
            var tree = VirtualBlendTree.Create($"{name}_{SanitizeName(condition.ParameterName)}");
            tree.BlendType = BlendTreeType.Simple1D;
            tree.BlendParameter = condition.ParameterName;
            tree.UseAutomaticThresholds = false;

            var lowMotion = condition.ActiveWhenGreater ? inactiveMotion : current;
            var highMotion = condition.ActiveWhenGreater ? current : inactiveMotion;
            float lowThreshold = condition.ActiveWhenGreater ? Mathf.Min(0f, condition.Threshold) : Mathf.Min(0f, condition.Threshold - 0.001f);
            float highThreshold = condition.ActiveWhenGreater ? Mathf.Max(1f, condition.Threshold + 0.001f) : Mathf.Max(1f, condition.Threshold);

            tree.Children = tree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = lowMotion,
                    Threshold = lowThreshold
                });
            tree.Children = tree.Children.Add(
                new VirtualBlendTree.VirtualChildMotion
                {
                    Motion = highMotion,
                    Threshold = highThreshold
                });

            current = tree;
        }

        return current;
    }

    VirtualClip GetSharedEmptyClip()
    {
        if (_sharedEmptyClip == null)
            _sharedEmptyClip = VirtualClip.Create("Empty");
        return _sharedEmptyClip;
    }

    ResolvedState ResolveStateForThreshold(AnalyzedLayer layer, float threshold)
    {

        var defaultState = layer.States.FirstOrDefault(s => s.IsDefault);

        foreach (var state in layer.States)
        {
            if (state.IsDefault) continue;

            bool inRange = IsThresholdInRange(threshold, state.ThresholdLo, state.ThresholdHi);

            if (!layer.IsInverted)
            {
                if (inRange)
                {
                    return new ResolvedState
                    {
                        ActiveState = state,
                        InactiveState = defaultState,
                        SecondaryConditions = state.SecondaryConditions
                    };
                }
            }
            else
            {
                if (inRange)
                {
                    return new ResolvedState
                    {
                        ActiveState = defaultState,
                        InactiveState = state,
                        SecondaryConditions = state.SecondaryConditions
                    };
                }
            }
        }

        if (layer.IsInverted)
        {
            var lastConditional = layer.States.LastOrDefault(s => !s.IsDefault);
            return new ResolvedState
            {
                ActiveState = lastConditional ?? defaultState,
                InactiveState = defaultState,
                SecondaryConditions = lastConditional?.SecondaryConditions ?? new List<SecondaryCondition>()
            };
        }

        return new ResolvedState
        {
            ActiveState = defaultState,
            InactiveState = defaultState,
            SecondaryConditions = new List<SecondaryCondition>()
        };
    }

    bool IsThresholdInRange(float threshold, float lo, float hi)
    {
        bool aboveLo = float.IsNegativeInfinity(lo) || threshold > lo;
        bool belowHi = float.IsPositiveInfinity(hi) || threshold < hi;
        return aboveLo && belowHi;
    }

    bool MergeMotionIntoClip(VirtualClip target, VirtualMotion motion)
    {
        if (motion is VirtualClip sourceClip)
        {
            return MergeClipCurves(target, sourceClip);
        }
        else if (motion is VirtualBlendTree bt)
        {
            bool any = false;
            foreach (var child in bt.Children)
            {
                if (child.Motion is VirtualClip childClip)
                    any |= MergeClipCurves(target, childClip);
            }
            return any;
        }
        return false;
    }

    bool MergeClipCurves(VirtualClip target, VirtualClip source)
    {
        bool any = false;

        // Float
        foreach (var binding in source.GetFloatCurveBindings())
        {
            var curve = source.GetFloatCurve(binding);
            if (curve != null)
            {
                target.SetFloatCurve(binding, curve);
                any = true;
            }
        }

        // Object reference
        foreach (var binding in source.GetObjectCurveBindings())
        {
            var objCurve = source.GetObjectCurve(binding);
            if (objCurve != null)
            {
                target.SetObjectCurve(binding, objCurve);
                any = true;
            }
        }

        return any;
    }

    #endregion

    #region 注入

    void InjectBlendTreeLayer(VirtualBlendTree rootBlendTree)
    {
        VirtualLayer existingLayer = null;
        foreach (var l in _fx.Layers)
        {
            if (l.Name == BLEND_TREE_LAYER_NAME)
            {
                existingLayer = l;
                break;
            }
        }

        VirtualLayer layer;
        if (existingLayer != null)
        {
            layer = existingLayer;
        }
        else
        {
            layer = _fx.AddLayer(new LayerPriority(0), BLEND_TREE_LAYER_NAME);
        }

        layer.DefaultWeight = 1f;
        layer.BlendingMode = AnimatorLayerBlendingMode.Override;

        var sm = layer.StateMachine;
        sm.States = ImmutableList<VirtualStateMachine.VirtualChildState>.Empty;
        sm.DefaultState = null;
        sm.StateMachines = ImmutableList<VirtualStateMachine.VirtualChildStateMachine>.Empty;
        sm.AnyStateTransitions = ImmutableList<VirtualStateTransition>.Empty;
        sm.EntryTransitions = ImmutableList<VirtualTransition>.Empty;

        var rootState = sm.AddState("RootBlendTree", rootBlendTree);
        rootState.WriteDefaultValues = true;
        sm.DefaultState = rootState;
    }

    #endregion

    #region 工具

    void EnsureFloatParameter(string name, float defaultValue = 0f)
    {
        if (_fx.Parameters.TryGetValue(name, out var existing))
        {
            if (existing.type != AnimatorControllerParameterType.Float)
            {
                float preservedDefault = existing.type == AnimatorControllerParameterType.Int
                    ? existing.defaultInt
                    : existing.defaultBool ? 1f : 0f;
                var param = new AnimatorControllerParameter
                {
                    name = name,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = preservedDefault
                };
                _fx.Parameters = _fx.Parameters.SetItem(name, param);
            }
            return;
        }

        var newParam = new AnimatorControllerParameter
        {
            name = name,
            type = AnimatorControllerParameterType.Float,
            defaultFloat = defaultValue
        };
        _fx.Parameters = _fx.Parameters.Add(name, newParam);
    }

    static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unnamed";
        var chars = name.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray();
        var result = new string(chars).Trim().Trim('.');
        return string.IsNullOrEmpty(result) ? "Unnamed" : result;
    }

    #endregion
}
