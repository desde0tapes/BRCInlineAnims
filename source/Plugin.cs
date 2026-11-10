using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using Reptile;
using UnityEngine;
using UnityEngine.Animations;
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BRCInlineAnims
{
    [BepInPlugin("com.desdezerotapes.brcinlineanims", "BRCInlineAnims", "1.7.8")]
    [BepInDependency("com.Dragsun.BunchOfEmotes", BepInDependency.DependencyFlags.SoftDependency)]
    public class InlinePlugin : BaseUnityPlugin
    {
        public static InlinePlugin Instance;
        public static ManualLogSource Log;

        // 1. General
        public static ConfigEntry<bool> ModEnabled;
        public static ConfigEntry<float> YOffset;
        public static ConfigEntry<bool> UnequipOnStairs;

        // 2. Idle Inline
        public static ConfigEntry<string> TargetIdleAnim;
        public static ConfigEntry<string> TargetIdleFidgetAnim;
        public static ConfigEntry<float> IdleStartDelay;

        // 3. Running Inline
        public static ConfigEntry<bool> UseVanillaRunning;
        public static ConfigEntry<string> TargetRunAnim;
        public static ConfigEntry<float> RunStartDelay;

        // 4. Boost Inline
        public static ConfigEntry<string> TargetBoostAnim;
        public static ConfigEntry<float> BoostStartDelay;

        // 5. On Foot Running
        public static ConfigEntry<bool> UseVanillaRunningOnFoot;
        public static ConfigEntry<string> TargetStartRunAnimOnFoot;
        public static ConfigEntry<string> TargetRunAnimOnFoot;

        // Timers & States
        private float _inlineIdleTimer = 0f;
        private float _inlineRunTimer = 0f;
        private float _inlineBoostTimer = 0f;
        private float _onFootRunTimer = 0f;

        private bool _wasLanding = false;
        private bool _wasBoosting = false;
        private bool _wasTricking = false;

        // New flag for smooth trick transitions
        private bool _wasGroundedLastFrame = true;

        // Boost Override State
        public bool IsBoostVisualOverrideActive = false;
        private RuntimeAnimatorController _originalBoostController;
        private RuntimeAnimatorController _customBoostController;
        private Animator _boostAnimator;
        private bool _boostOriginalRootMotion;
        private bool _boostOriginalFeetIK;
        private MonoBehaviour _boostCharVisComponent;

        // Constraint Offset Caches for Parent-Constrained Particles/Objects Fix
        private static Dictionary<UnityEngine.Object, Vector3[]> _originalParentConstraintOffsets = new Dictionary<UnityEngine.Object, Vector3[]>();
        private static Dictionary<UnityEngine.Object, Vector3> _originalPositionConstraintOffsets = new Dictionary<UnityEngine.Object, Vector3>();

        // Safe Controller Caches to prevent Custom Controller Leakage
        public static RuntimeAnimatorController CachedVanillaInlineController;
        public static RuntimeAnimatorController CachedVanillaOnFootController;

        // Dynamic Controller Merge for Inline
        public static AnimatorOverrideController MergedInlineController;
        public static bool IsTransitioningEmote = false;

        public static class ReflectionUtility
        {
            public static Type GetTypeByName(string name)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType(name);
                    if (type != null) return type;
                }
                return null;
            }
        }

        public static bool IsNoBrakezInstalled()
        {
            foreach (var key in Chainloader.PluginInfos.Keys)
            {
                if (key.IndexOf("nobrake", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // INLINE CONFIGS
            ModEnabled = Config.Bind("1. General", "Enabled", true, "Enable or disable the mod.");
            YOffset = Config.Bind("1. General", "YOffset", 0.097f, "Height offset to prevent floor clipping for Inline.");
            UnequipOnStairs = Config.Bind("1. General", "UnequipOnStairs", true, "If true, unequips movestyle on stairs (vanilla behavior). If false, allows using movestyles on stairs.");

            TargetIdleAnim = Config.Bind("2. Idle Inline", "AnimationName", "idle", "Anim when standing still. Plays first.");
            TargetIdleFidgetAnim = Config.Bind("2. Idle Inline", "AnimationName(Fidget)", "idleFidget1", "Anim that loops after the first idle animation. Set same as AnimationName to disable.");
            IdleStartDelay = Config.Bind("2. Idle Inline", "StartDelay", 0.38f, "Seconds of stillness before idle triggers.");

            UseVanillaRunning = Config.Bind("3. Running Inline", "UseVanillaRunning", false, "Enable to bypass custom running animations.");
            TargetRunAnim = Config.Bind("3. Running Inline", "AnimationName", "run", "Anim when moving. If set to 'run', behaves as vanilla.");
            RunStartDelay = Config.Bind("3. Running Inline", "StartDelay", 0.0f, "Seconds of movement before run triggers.");

            TargetBoostAnim = Config.Bind("4. Boost Inline", "AnimationName(Match it with Newtrix!)", "boostRun", "Anim when boosting.");
            BoostStartDelay = Config.Bind("4. Boost Inline", "StartDelay", 0.29f, "Seconds of boosting before anim triggers.");

            // ON FOOT CONFIGS
            UseVanillaRunningOnFoot = Config.Bind("5. On Foot Running", "UseVanillaRunning", false, "Enable to bypass custom running animations and use vanilla on-foot running.");
            TargetStartRunAnimOnFoot = Config.Bind("5. On Foot Running", "StartRunAnimationName", "startRun", "Anim played briefly before the main on-foot run loop.");
            TargetRunAnimOnFoot = Config.Bind("5. On Foot Running", "AnimationName", "run", "Anim when moving on foot.");

            if (Chainloader.PluginInfos.ContainsKey("com.Dragsun.BunchOfEmotes"))
            {
                BunchOfEmotesSupport.Initialize();
            }

            Log.LogInfo("BRCInlineAnims: Running Unified Module (Inline + OnFoot). Smart Blending Active.");

            // Initialize Harmony
            Harmony harmony = new Harmony("com.desdezerotapes.brcinlineanims");
            harmony.PatchAll();
        }

        public static void ApplyYOffsetToCharacterVisual(MonoBehaviour charVis, float targetYOffset)
        {
            if (charVis == null) return;

            if (Mathf.Approximately(targetYOffset, 0f))
            {
                RestoreConstraintOffsets(charVis);
                return;
            }

            var parentConstraints = charVis.GetComponentsInChildren<ParentConstraint>(true);
            foreach (var pc in parentConstraints)
            {
                if (pc == null) continue;
                if (!_originalParentConstraintOffsets.ContainsKey(pc))
                {
                    int count = pc.sourceCount;
                    Vector3[] offsets = new Vector3[count];
                    for (int i = 0; i < count; i++)
                    {
                        offsets[i] = pc.GetTranslationOffset(i);
                    }
                    _originalParentConstraintOffsets[pc] = offsets;
                }

                int srcCount = pc.sourceCount;
                for (int i = 0; i < srcCount; i++)
                {
                    Vector3 origOffset = _originalParentConstraintOffsets[pc][i];
                    pc.SetTranslationOffset(i, new Vector3(origOffset.x, origOffset.y + targetYOffset, origOffset.z));
                }
            }

            var posConstraints = charVis.GetComponentsInChildren<PositionConstraint>(true);
            foreach (var poc in posConstraints)
            {
                if (poc == null) continue;
                if (!_originalPositionConstraintOffsets.ContainsKey(poc))
                {
                    _originalPositionConstraintOffsets[poc] = poc.translationOffset;
                }

                Vector3 origOffset = _originalPositionConstraintOffsets[poc];
                poc.translationOffset = new Vector3(origOffset.x, origOffset.y + targetYOffset, origOffset.z);
            }
        }

        public static void RestoreConstraintOffsets(MonoBehaviour charVis)
        {
            if (charVis == null) return;

            var parentConstraints = charVis.GetComponentsInChildren<ParentConstraint>(true);
            foreach (var pc in parentConstraints)
            {
                if (pc != null && _originalParentConstraintOffsets.TryGetValue(pc, out var offsets))
                {
                    int count = pc.sourceCount;
                    for (int i = 0; i < count && i < offsets.Length; i++)
                    {
                        pc.SetTranslationOffset(i, offsets[i]);
                    }
                }
            }
            _originalParentConstraintOffsets.Clear();

            var posConstraints = charVis.GetComponentsInChildren<PositionConstraint>(true);
            foreach (var poc in posConstraints)
            {
                if (poc != null && _originalPositionConstraintOffsets.TryGetValue(poc, out var origOffset))
                {
                    poc.translationOffset = origOffset;
                }
            }
            _originalPositionConstraintOffsets.Clear();
        }

        public static RuntimeAnimatorController GetProperCustomController(MoveStyle style)
        {
            RuntimeAnimatorController boeController = BunchOfEmotesSupport.GetController();
            if (boeController == null) return null;

            if (style == MoveStyle.INLINE)
            {
                if (MergedInlineController != null) return MergedInlineController;

                if (CachedVanillaInlineController is AnimatorOverrideController inlineOverride)
                {
                    MergedInlineController = new AnimatorOverrideController(boeController);
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    inlineOverride.GetOverrides(overrides);

                    var cleanOverrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    foreach (var pair in overrides)
                    {
                        if (pair.Key != null) cleanOverrides.Add(pair);
                    }

                    MergedInlineController.ApplyOverrides(cleanOverrides);
                    return MergedInlineController;
                }
                return boeController;
            }

            return boeController;
        }

        public static void SwapAnimatorControllerSafely(Animator animator, RuntimeAnimatorController targetController, int? overrideLayer0Hash = null, float overrideLayer0Time = 0f)
        {
            if (animator == null || targetController == null || animator.runtimeAnimatorController == targetController)
                return;

            int layerCount = animator.layerCount;
            var layerStates = new AnimatorStateInfo[layerCount];
            var layerWeights = new float[layerCount];

            for (int i = 0; i < layerCount; i++)
            {
                layerStates[i] = animator.GetCurrentAnimatorStateInfo(i);
                if (i > 0) layerWeights[i] = animator.GetLayerWeight(i);
            }

            animator.runtimeAnimatorController = targetController;
            animator.Rebind();

            // Restore Layer 0
            int layer0Hash = overrideLayer0Hash ?? layerStates[0].fullPathHash;
            float layer0Time = overrideLayer0Hash.HasValue ? overrideLayer0Time : layerStates[0].normalizedTime;

            if (animator.HasState(0, layer0Hash))
                animator.Play(layer0Hash, 0, layer0Time);
            else
                animator.Play(Animator.StringToHash("idle"), 0, 0f);

            for (int i = 1; i < animator.layerCount && i < layerCount; i++)
            {
                animator.SetLayerWeight(i, layerWeights[i]);
                if (animator.HasState(i, layerStates[i].fullPathHash))
                    animator.Play(layerStates[i].fullPathHash, i, layerStates[i].normalizedTime);
            }

            animator.Update(0f);
        }

        private void Update()
        {
            if (!ModEnabled.Value) return;
            Player player = WorldHandler.instance?.GetCurrentPlayer();
            if (player == null) return;

            MoveStyle style = (MoveStyle)GetMemberValue(player, "moveStyle");

            object charVisCacheObj = GetMemberValue(player, "characterVisual");
            if (charVisCacheObj != null)
            {
                Animator cacheAnim = (Animator)GetMemberValue(charVisCacheObj, "anim");
                if (cacheAnim != null)
                {
                    RuntimeAnimatorController currentCtrl = cacheAnim.runtimeAnimatorController;
                    RuntimeAnimatorController baseBoE = BunchOfEmotesSupport.GetController();

                    if (currentCtrl != baseBoE && currentCtrl != MergedInlineController)
                    {
                        if (style == MoveStyle.INLINE && CachedVanillaInlineController == null)
                        {
                            CachedVanillaInlineController = currentCtrl;
                            Log.LogInfo("[BRCInlineAnims] Proactively cached Vanilla Inline Controller: " + currentCtrl.name);
                        }
                        else if (style == MoveStyle.ON_FOOT && CachedVanillaOnFootController == null)
                        {
                            CachedVanillaOnFootController = currentCtrl;
                            Log.LogInfo("[BRCInlineAnims] Proactively cached Vanilla OnFoot Controller: " + currentCtrl.name);
                        }
                    }
                }
            }

            bool isLanding = false;
            object landingObj = GetMemberValue(player, "isLanding");
            if (landingObj is bool b) isLanding = b;

            bool isJumpPressed = false;
            object jumpObj = GetMemberValue(player, "jumpButtonNew");
            if (jumpObj is bool j) isJumpPressed = j;

            bool isSwitchPressed = false;
            object switchObj = GetMemberValue(player, "switchStyleButtonNew");
            if (switchObj is bool sw) isSwitchPressed = sw;

            bool isGrounded = IsPlayerGrounded(player);
            bool justLanded = !_wasGroundedLastFrame && isGrounded;
            _wasGroundedLastFrame = isGrounded;

            int curAnim = 0;
            object curAnimObj = GetMemberValue(player, "curAnim");
            if (curAnimObj is int c) curAnim = c;

            int targetAnim = 0;
            object targetAnimObj = GetMemberValue(player, "targetAnim");
            if (targetAnimObj is int t) targetAnim = t;

            int landRunHash = Animator.StringToHash("landRun");
            int landHash = Animator.StringToHash("land");
            bool isPlayingLandingAnim = (curAnim == landRunHash || curAnim == landHash || targetAnim == landRunHash || targetAnim == landHash);
            bool shouldBreak = (isLanding || justLanded || !isGrounded || isJumpPressed || isSwitchPressed || isPlayingLandingAnim);

            if (shouldBreak)
            {
                ResetAllTimers();
                StopBoostVisualOverride(player);

                object currentAb = GetMemberValue(player, "ability");
                if (currentAb is UnifiedEmoteAbility emoteAbility)
                {
                    emoteAbility.isBreakingOut = true;
                    player.StopCurrentAbility();
                }

                if (justLanded || isPlayingLandingAnim || isLanding) _wasLanding = true;
                else if (!isGrounded || isJumpPressed) _wasLanding = false;

                return;
            }

            Vector3 moveInput = (Vector3)GetMemberValue(player, "moveInput");
            bool isMoving = moveInput.sqrMagnitude > 0.01f;

            if (style == MoveStyle.INLINE)
            {
                _onFootRunTimer = 0f;
                HandleInlineLogic(player, isMoving);
            }
            else if (style == MoveStyle.ON_FOOT)
            {
                ResetInlineTimers();
                StopBoostVisualOverride(player);
                HandleOnFootLogic(player, isMoving);
            }
            else
            {
                ResetAllTimers();
                StopBoostVisualOverride(player);
            }
        }

        private void HandleInlineLogic(Player player, bool isMoving)
        {
            object currentAbility = GetMemberValue(player, "ability");
            bool isCustomEmoteAbility = (currentAbility is UnifiedEmoteAbility);
            string abTypeName = currentAbility?.GetType().Name ?? "";

            bool isHyperBoost = abTypeName.Contains("HyperBoostAbility");
            bool isBoostAbility = abTypeName.Contains("BoostAbility") && !isHyperBoost;
            bool isTrickAbility = abTypeName.Contains("TrickAbility");

            if (isBoostAbility)
            {
                int boostBrakeHash = Animator.StringToHash("boostBrake");
                int brakeHash = Animator.StringToHash("brake");
                int curAnimHash = 0;
                object curAnimObj = GetMemberValue(player, "curAnim");
                if (curAnimObj is int c) curAnimHash = c;

                int targetAnimHash = 0;
                object targetAnimObj = GetMemberValue(player, "targetAnim");
                if (targetAnimObj is int t) targetAnimHash = t;

                bool isBoostBraking = (curAnimHash == boostBrakeHash || targetAnimHash == boostBrakeHash || curAnimHash == brakeHash || targetAnimHash == brakeHash);

                if (!isBoostBraking)
                {
                    object stateObj = GetMemberValue(currentAbility, "state");
                    if (stateObj != null && stateObj.ToString() == "BRAKE")
                    {
                        isBoostBraking = true;
                    }
                }

                // If player is moving and NoBrakez mod is present, suppress brake transition completely
                if (isMoving && IsNoBrakezInstalled())
                {
                    isBoostBraking = false;
                }

                if (!isBoostBraking)
                {
                    _wasBoosting = true;
                    _inlineIdleTimer = 0f;
                    _inlineRunTimer = 0f;
                    _inlineBoostTimer += Time.deltaTime;

                    if (_inlineBoostTimer >= BoostStartDelay.Value && !IsBoostVisualOverrideActive)
                    {
                        StartBoostVisualOverride(player, TargetBoostAnim.Value);
                    }
                }
                else
                {
                    if (IsBoostVisualOverrideActive)
                    {
                        StopBoostVisualOverride(player, false, true, boostBrakeHash);
                    }
                    _inlineBoostTimer = 0f;
                    _wasBoosting = true;
                }
            }
            else if (currentAbility == null || isCustomEmoteAbility)
            {
                _inlineBoostTimer = 0f;
                bool isVanillaRun = string.Equals(TargetRunAnim.Value, "run", StringComparison.OrdinalIgnoreCase);
                bool isVanillaIdle = string.Equals(TargetIdleAnim.Value, "idle", StringComparison.OrdinalIgnoreCase);

                bool wasCustomIdle = isCustomEmoteAbility && !(currentAbility as UnifiedEmoteAbility).isRunningMode && !isVanillaIdle;

                bool willTriggerCustomRun = false;
                bool willTriggerCustomIdle = false;

                if (isMoving && !UseVanillaRunning.Value && !isVanillaRun)
                {
                    if (_inlineRunTimer + Time.deltaTime >= RunStartDelay.Value)
                        willTriggerCustomRun = true;
                }
                else if (!isMoving)
                {
                    if (_inlineIdleTimer + Time.deltaTime >= IdleStartDelay.Value)
                        willTriggerCustomIdle = true;
                }

                bool immediateCustomTransition = willTriggerCustomRun || willTriggerCustomIdle;
                StopBoostVisualOverride(player, immediateCustomTransition);

                if (isMoving)
                {
                    _inlineIdleTimer = 0f;

                    if (UseVanillaRunning.Value || isVanillaRun)
                    {
                        if (isCustomEmoteAbility) player.StopCurrentAbility();
                        _inlineRunTimer = 0f;
                    }
                    else
                    {
                        _inlineRunTimer += Time.deltaTime;

                        if (_inlineRunTimer >= RunStartDelay.Value)
                        {
                            float blend = 0.15f;
                            string startAnim = null;
                            bool transitioningFromCustomIdle = false;

                            if (_wasLanding) blend = 0.3f;
                            else if (_wasBoosting)
                            {
                                blend = 0.15f;
                                startAnim = isVanillaRun ? "halfStartRun" : null;
                            }
                            else if (_wasTricking) blend = 0.35f;
                            else if (wasCustomIdle && !isVanillaRun)
                            {
                                blend = 0.15f;
                                startAnim = "startRun";
                                transitioningFromCustomIdle = true;
                            }

                            TriggerCustomEmote(player, TargetRunAnim.Value, startAnim, true, MoveStyle.INLINE, blend, transitioningFromCustomIdle);

                            _wasLanding = false;
                            _wasBoosting = false;
                            _wasTricking = false;
                        }
                    }
                }
                else
                {
                    _inlineRunTimer = 0f;
                    _inlineIdleTimer += Time.deltaTime;

                    if (_inlineIdleTimer >= IdleStartDelay.Value)
                    {
                        int currentAnimHash = 0;
                        object cAnimObj = GetMemberValue(player, "curAnim");
                        if (cAnimObj is int c) currentAnimHash = c;

                        bool isStoppingAnim = (currentAnimHash == Animator.StringToHash("stopRun") ||
                                               currentAnimHash == Animator.StringToHash("boostBrake") ||
                                               currentAnimHash == Animator.StringToHash("brake"));

                        if (!isCustomEmoteAbility && isStoppingAnim)
                        {
                            // Hold transition until vanilla stop completes
                        }
                        else
                        {
                            float blend = 0.15f;

                            if (_wasLanding) blend = 0.31f;
                            else if (_wasBoosting) blend = 0.25f;
                            else if (_wasTricking) blend = 0.3f;

                            string idleStart = TargetIdleAnim.Value;
                            string idleFidget = TargetIdleFidgetAnim.Value;

                            if (string.Equals(idleStart, idleFidget, StringComparison.OrdinalIgnoreCase))
                            {
                                TriggerCustomEmote(player, idleStart, null, false, MoveStyle.INLINE, blend);
                            }
                            else
                            {
                                TriggerCustomEmote(player, idleFidget, idleStart, false, MoveStyle.INLINE, blend);
                            }

                            _wasLanding = false;
                            _wasBoosting = false;
                            _wasTricking = false;
                        }
                    }
                }
            }
            else
            {
                if (isTrickAbility) _wasTricking = true;

                ResetInlineTimers();
                StopBoostVisualOverride(player);
            }
        }

        private void HandleOnFootLogic(Player player, bool isMoving)
        {
            object currentAbility = GetMemberValue(player, "ability");
            bool isCustomEmoteAbility = (currentAbility is UnifiedEmoteAbility);
            bool isVanillaRunOnFoot = string.Equals(TargetRunAnimOnFoot.Value, "run", StringComparison.OrdinalIgnoreCase);

            if (currentAbility == null || isCustomEmoteAbility)
            {
                if (isMoving)
                {
                    if (UseVanillaRunningOnFoot.Value || isVanillaRunOnFoot)
                    {
                        _onFootRunTimer = 0f;
                        if (isCustomEmoteAbility) player.StopCurrentAbility();
                    }
                    else
                    {
                        float targetStartDelay = _wasLanding ? ((TargetRunAnimOnFoot.Value == "run") ? 0.3f : 0.12f) : 0f;
                        _onFootRunTimer += Time.deltaTime;

                        if (_onFootRunTimer >= targetStartDelay)
                        {
                            string startAnim = _wasLanding ? "" : TargetStartRunAnimOnFoot.Value;
                            float blend = _wasLanding ? 0.38f : 0.05f;

                            TriggerCustomEmote(player, TargetRunAnimOnFoot.Value, startAnim, true, MoveStyle.ON_FOOT, blend);
                            _wasLanding = false;
                        }
                    }
                }
                else
                {
                    _onFootRunTimer = 0f;
                    if (isCustomEmoteAbility) player.StopCurrentAbility();
                }
            }
            else
            {
                _onFootRunTimer = 0f;
            }
        }

        private void LateUpdate()
        {
            if (!ModEnabled.Value) return;
            Player player = WorldHandler.instance?.GetCurrentPlayer();
            if (player == null) return;

            MoveStyle currentStyle = (MoveStyle)GetMemberValue(player, "moveStyle");
            object currentAbility = GetMemberValue(player, "ability");

            bool isInlineCustomEmote = (currentAbility is UnifiedEmoteAbility emoteAbility && emoteAbility.triggerStyle == MoveStyle.INLINE);
            bool isHyperBoost = currentAbility != null && currentAbility.GetType().Name.Contains("HyperBoostAbility");

            if (isInlineCustomEmote || (isHyperBoost && currentStyle == MoveStyle.INLINE))
            {
                object charVisObj = GetMemberValue(player, "characterVisual");
                if (charVisObj != null)
                {
                    MonoBehaviour charVis = charVisObj as MonoBehaviour;
                    if (charVis != null)
                    {
                        Vector3 currentPos = charVis.transform.localPosition;
                        charVis.transform.localPosition = new Vector3(currentPos.x, YOffset.Value, currentPos.z);
                        ApplyYOffsetToCharacterVisual(charVis, YOffset.Value);
                    }
                }
            }

            if (IsBoostVisualOverrideActive && _boostCharVisComponent != null)
            {
                Vector3 currentPos = _boostCharVisComponent.transform.localPosition;
                _boostCharVisComponent.transform.localPosition = new Vector3(currentPos.x, YOffset.Value, currentPos.z);
                ApplyYOffsetToCharacterVisual(_boostCharVisComponent, YOffset.Value);

                if (_boostAnimator != null && _customBoostController != null)
                {
                    if (_boostAnimator.runtimeAnimatorController != _customBoostController)
                    {
                        SwapAnimatorControllerSafely(_boostAnimator, _customBoostController);
                    }
                }
            }

            if (!IsBoostVisualOverrideActive) return;

            Vector3 moveInput = (Vector3)GetMemberValue(player, "moveInput");
            if (moveInput.sqrMagnitude > 0.01f && player.motor != null)
            {
                SetMemberValue(player, "dir", moveInput.normalized);

                Vector3 groundNormal = Vector3.up;
                object normalObj = GetMemberValue(player.motor, "groundNormal");
                if (normalObj is Vector3 n) groundNormal = n;

                Vector3 forwardDir = player.motor.transform.forward;
                Vector3 slopeForward = Vector3.ProjectOnPlane(forwardDir, groundNormal).normalized;
                if (slopeForward == Vector3.zero) slopeForward = forwardDir;

                Quaternion targetVisualRot = Quaternion.LookRotation(slopeForward, groundNormal);
                player.SetVisualRot(targetVisualRot, false);
            }
        }

        private void ResetAllTimers()
        {
            ResetInlineTimers();
            _onFootRunTimer = 0f;
            _wasTricking = false;
        }

        private void ResetInlineTimers()
        {
            _inlineIdleTimer = 0f;
            _inlineRunTimer = 0f;
            _inlineBoostTimer = 0f;
            _wasBoosting = false;
        }

        private void StartBoostVisualOverride(Player player, string animName)
        {
            object charVisObj = GetMemberValue(player, "characterVisual");
            if (charVisObj == null) return;

            _boostCharVisComponent = charVisObj as MonoBehaviour;
            _boostAnimator = (Animator)GetMemberValue(charVisObj, "anim");
            if (_boostAnimator == null) return;

            _boostOriginalRootMotion = _boostAnimator.applyRootMotion;
            _boostAnimator.applyRootMotion = false;

            var currentController = _boostAnimator.runtimeAnimatorController;
            RuntimeAnimatorController baseBoE = BunchOfEmotesSupport.GetController();

            if (currentController != baseBoE && currentController != CachedVanillaInlineController && currentController != MergedInlineController)
            {
                _originalBoostController = currentController;
                CachedVanillaInlineController = currentController;
            }
            else
            {
                _originalBoostController = CachedVanillaInlineController;
            }

            _customBoostController = GetProperCustomController(MoveStyle.INLINE);

            if (_customBoostController != null)
            {
                SwapAnimatorControllerSafely(_boostAnimator, _customBoostController);
            }

            object ikObj = GetMemberValue(charVisObj, "feetIK");
            if (ikObj is bool b) _boostOriginalFeetIK = b;
            SetMemberValue(charVisObj, "feetIK", false);

            _boostCharVisComponent.transform.localPosition = new Vector3(_boostCharVisComponent.transform.localPosition.x, YOffset.Value, _boostCharVisComponent.transform.localPosition.z);
            ApplyYOffsetToCharacterVisual(_boostCharVisComponent, YOffset.Value);

            int targetHash = 0;
            if (!BunchOfEmotesSupport.TryGetGameAnimationForCustomAnimation(animName, out targetHash))
                targetHash = Animator.StringToHash(animName);

            if (_boostAnimator.HasState(0, targetHash))
                _boostAnimator.CrossFade(targetHash, 0.15f, 0, 0f);
            else
                _boostAnimator.CrossFade(Animator.StringToHash("idle"), 0.15f, 0, 0f);

            IsBoostVisualOverrideActive = true;
        }

        public void StopBoostVisualOverride(Player player, bool immediateCustomTransition = false, bool yieldToExternalSystem = false, int? specificYieldHash = null)
        {
            if (!IsBoostVisualOverrideActive) return;

            if (_boostAnimator != null)
            {
                try
                {
                    if (!immediateCustomTransition && _originalBoostController != null && _customBoostController != null)
                    {
                        if (_boostAnimator.runtimeAnimatorController == _customBoostController)
                        {
                            Vector3 moveInput = (Vector3)GetMemberValue(player, "moveInput");
                            bool isMoving = moveInput.sqrMagnitude > 0.01f;
                            bool noBrakez = IsNoBrakezInstalled();

                            int boostBrakeHash = Animator.StringToHash("boostBrake");
                            int brakeHash = Animator.StringToHash("brake");

                            if (yieldToExternalSystem)
                            {
                                int yieldHash = Animator.StringToHash("idle");

                                if (specificYieldHash.HasValue && specificYieldHash.Value != 0)
                                {
                                    yieldHash = specificYieldHash.Value;
                                }
                                else
                                {
                                    object tAnimObj = GetMemberValue(player, "targetAnim");
                                    object cAnimObj = GetMemberValue(player, "curAnim");
                                    if (tAnimObj is int t && t != 0) yieldHash = t;
                                    else if (cAnimObj is int c && c != 0) yieldHash = c;
                                }

                                if (isMoving && (noBrakez || yieldHash == boostBrakeHash || yieldHash == brakeHash))
                                {
                                    yieldHash = Animator.StringToHash("run");
                                }

                                float yieldTime = _boostAnimator != null ? _boostAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f : 0f;

                                int bridgeHash = Animator.StringToHash("boostRun");

                                SwapAnimatorControllerSafely(_boostAnimator, _originalBoostController, bridgeHash, yieldTime);
                                _boostAnimator.applyRootMotion = _boostOriginalRootMotion;

                                if (_boostAnimator.HasState(0, yieldHash))
                                    _boostAnimator.CrossFade(yieldHash, 0.15f, 0, 0f);
                                else
                                    _boostAnimator.Play(yieldHash, 0, 0f);
                            }
                            else
                            {
                                bool isJumpPressed = false;
                                object jumpObj = GetMemberValue(player, "jumpButtonNew");
                                if (jumpObj is bool j) isJumpPressed = j;

                                bool isGrounded = IsPlayerGrounded(player);

                                int targetHash = Animator.StringToHash("idle");
                                float blendTime = 0.15f;

                                object targetAnimObj = GetMemberValue(player, "targetAnim");
                                int vanillaTarget = targetAnimObj is int t ? t : 0;

                                int bridgeHash = Animator.StringToHash("boostRun");
                                float bridgeTime = _boostAnimator != null ? _boostAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f : 0f;
                                int finalPlayerStateHash = Animator.StringToHash("idle");

                                if (isJumpPressed || !isGrounded)
                                {
                                    targetHash = isJumpPressed ? Animator.StringToHash("jump") : Animator.StringToHash("fall");
                                    blendTime = 0.05f;
                                    finalPlayerStateHash = targetHash;
                                    bridgeHash = targetHash;
                                }
                                else if (isMoving)
                                {
                                    targetHash = (vanillaTarget != 0 && vanillaTarget != boostBrakeHash && vanillaTarget != brakeHash) ? vanillaTarget : Animator.StringToHash("run");
                                    blendTime = 0.20f;
                                    finalPlayerStateHash = Animator.StringToHash("run");
                                    bridgeHash = Animator.StringToHash("boostRun");
                                }
                                else
                                {
                                    if (noBrakez)
                                    {
                                        targetHash = Animator.StringToHash("idle");
                                        blendTime = 0.15f;
                                        finalPlayerStateHash = targetHash;
                                        bridgeHash = Animator.StringToHash("boostRun");
                                    }
                                    else
                                    {
                                        targetHash = (vanillaTarget != 0) ? vanillaTarget : Animator.StringToHash("boostBrake");
                                        blendTime = 0.15f;
                                        finalPlayerStateHash = targetHash;
                                        bridgeHash = Animator.StringToHash("boostRun");
                                    }
                                }

                                SwapAnimatorControllerSafely(_boostAnimator, _originalBoostController, bridgeHash, bridgeTime);
                                _boostAnimator.applyRootMotion = _boostOriginalRootMotion;

                                if (_boostAnimator.HasState(0, targetHash))
                                    _boostAnimator.CrossFade(targetHash, blendTime, 0, 0f);
                                else
                                    _boostAnimator.Play(Animator.StringToHash("idle"), 0, 0f);

                                SetMemberValue(player, "curAnim", finalPlayerStateHash);
                                SetMemberValue(player, "targetAnim", finalPlayerStateHash);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("Failed to resync vanilla boost animations: " + e.Message);
                }
            }

            object charVisObj = GetMemberValue(player, "characterVisual");

            if (charVisObj != null)
            {
                if (!immediateCustomTransition)
                    SetMemberValue(charVisObj, "feetIK", _boostOriginalFeetIK);

                if (_boostCharVisComponent != null)
                {
                    ApplyYOffsetToCharacterVisual(_boostCharVisComponent, 0f);
                    _boostCharVisComponent.transform.localPosition = new Vector3(_boostCharVisComponent.transform.localPosition.x, 0f, _boostCharVisComponent.transform.localPosition.z);
                }
            }

            IsBoostVisualOverrideActive = false;
        }

        public static bool IsPlayerGrounded(Player p)
        {
            if (p.motor == null) return false;
            return p.motor.isGrounded;
        }

        private void TriggerCustomEmote(Player player, string animName, string startAnimName, bool isRunning, MoveStyle style, float blendTime = 0.15f, bool fromCustomIdle = false)
        {
            object currentAbility = GetMemberValue(player, "ability");

            if (currentAbility is UnifiedEmoteAbility existing)
            {
                if (existing.animString == animName && existing.triggerStyle == style) return;
            }

            int targetHash = 0;

            if (!BunchOfEmotesSupport.TryGetGameAnimationForCustomAnimation(animName, out targetHash))
                targetHash = Animator.StringToHash(animName);

            int startHash = 0;
            bool hasStartAnim = !string.IsNullOrEmpty(startAnimName);

            if (hasStartAnim)
            {
                if (!BunchOfEmotesSupport.TryGetGameAnimationForCustomAnimation(startAnimName, out startHash))
                    startHash = Animator.StringToHash(startAnimName);
            }

            UnifiedEmoteAbility emoteAbility = new UnifiedEmoteAbility(player, isRunning, animName, style, blendTime, fromCustomIdle);

            emoteAbility.targetAnimHash = targetHash;
            emoteAbility.targetStartAnimHash = startHash;
            emoteAbility.useStartAnim = hasStartAnim;

            IsTransitioningEmote = true;
            player.StopCurrentAbility();
            player.ActivateAbility(emoteAbility);
            IsTransitioningEmote = false;
        }

        public static object GetMemberValue(object obj, string memberName)
        {
            var type = obj.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);

            PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop?.GetValue(obj);
        }

        public static void SetMemberValue(object obj, string memberName, object value)
        {
            var type = obj.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                field.SetValue(obj, value);
                return;
            }

            PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
        }
    }

    public class UnifiedEmoteAbility : Ability
    {
        public int targetAnimHash;
        public int targetStartAnimHash;
        public bool useStartAnim;
        public string animString;
        public MoveStyle triggerStyle;
        public bool isRunningMode;

        public float blendTime;
        public float startAnimDuration = 0f;
        public float startAnimToMainBlend = 0.25f;

        private RuntimeAnimatorController originalController;
        private RuntimeAnimatorController customController;
        private Animator abilityAnimator;

        private MonoBehaviour charVisComponent;
        private object charVisObj;

        private bool mainAnimationTriggered = false;
        private float abilityTimer = 0f;

        private bool originalFeetIK = true;
        private bool originalRootMotion = false;

        public bool isBreakingOut = false;
        public bool fromCustomIdle = false;
        public bool isTrickBridge = false;

        public UnifiedEmoteAbility(Player player, bool isRunning, string animName, MoveStyle style, float blend = 0.15f, bool fromCustomIdle = false) : base(player)
        {
            this.isRunningMode = isRunning;
            this.animString = animName;
            this.triggerStyle = style;
            this.blendTime = blend;
            this.fromCustomIdle = fromCustomIdle;
            this.isTrickBridge = false;

            this.startAnimDuration = style == MoveStyle.ON_FOOT ? 0.3f : 0.35f;
            this.startAnimToMainBlend = style == MoveStyle.ON_FOOT ? 0.3f : 0.25f;

            this.normalMovement = true;
            this.normalRotation = true;
            this.allowNormalJump = true;
            this.normalAnimation = false;
            this.treatPlayerAsSortaGrounded = true;
            this.canStartGrind = true;
        }

        public override void OnStartAbility()
        {
            base.OnStartAbility();

            mainAnimationTriggered = false;
            abilityTimer = 0f;
            isBreakingOut = false;
            isTrickBridge = false;

            charVisObj = InlinePlugin.GetMemberValue(p, "characterVisual");

            if (charVisObj != null)
            {
                charVisComponent = charVisObj as MonoBehaviour;

                object ikObj = InlinePlugin.GetMemberValue(charVisObj, "feetIK");
                if (ikObj is bool b) originalFeetIK = b;
                InlinePlugin.SetMemberValue(charVisObj, "feetIK", false);

                if (charVisComponent != null && triggerStyle == MoveStyle.INLINE)
                {
                    Vector3 currentPos = charVisComponent.transform.localPosition;
                    charVisComponent.transform.localPosition = new Vector3(currentPos.x, InlinePlugin.YOffset.Value, currentPos.z);
                    InlinePlugin.ApplyYOffsetToCharacterVisual(charVisComponent, InlinePlugin.YOffset.Value);
                }

                abilityAnimator = (Animator)InlinePlugin.GetMemberValue(charVisObj, "anim");

                if (abilityAnimator != null)
                {
                    originalRootMotion = abilityAnimator.applyRootMotion;
                    abilityAnimator.applyRootMotion = false;

                    var currentController = abilityAnimator.runtimeAnimatorController;
                    RuntimeAnimatorController baseBoE = BunchOfEmotesSupport.GetController();

                    if (currentController != baseBoE && currentController != InlinePlugin.MergedInlineController)
                    {
                        originalController = currentController;

                        if (triggerStyle == MoveStyle.INLINE) InlinePlugin.CachedVanillaInlineController = currentController;
                        else if (triggerStyle == MoveStyle.ON_FOOT) InlinePlugin.CachedVanillaOnFootController = currentController;
                    }
                    else
                    {
                        originalController = triggerStyle == MoveStyle.INLINE ?
                            InlinePlugin.CachedVanillaInlineController : InlinePlugin.CachedVanillaOnFootController;
                    }

                    customController = InlinePlugin.GetProperCustomController(triggerStyle);

                    if (customController != null)
                    {
                        AnimatorStateInfo state = abilityAnimator.GetCurrentAnimatorStateInfo(0);

                        int currentShortHash = state.shortNameHash;
                        int hashToPlay = state.fullPathHash;
                        float initialNormalizedTime = state.normalizedTime;

                        if (triggerStyle == MoveStyle.INLINE)
                        {
                            int boostRunHash = Animator.StringToHash("boostRun");
                            int startBoostHash = Animator.StringToHash("startBoost");
                            int boostBrakeHash = Animator.StringToHash("boostBrake");
                            int brakeHash = Animator.StringToHash("brake");
                            int runNEWSkatesHash = Animator.StringToHash("runNEWSkates");
                            int stopRunSkatesHash = Animator.StringToHash("stopRunSkates");
                            int stopRunHash = Animator.StringToHash("stopRun");
                            int idleSkatesHash = Animator.StringToHash("idleSkates");

                            bool isFromBoostOrSkateState = (currentShortHash == boostRunHash || currentShortHash == startBoostHash ||
                                                            currentShortHash == boostBrakeHash || currentShortHash == brakeHash ||
                                                            currentShortHash == runNEWSkatesHash || currentShortHash == stopRunSkatesHash ||
                                                            currentShortHash == idleSkatesHash || currentShortHash == stopRunHash);

                            if (isFromBoostOrSkateState)
                            {
                                if (isRunningMode)
                                {
                                    hashToPlay = Animator.StringToHash("run");
                                    initialNormalizedTime = 0.1f;
                                }
                                else
                                {
                                    hashToPlay = Animator.StringToHash("idle");
                                    initialNormalizedTime = 0.0f;
                                }
                            }
                            else if (isRunningMode)
                            {
                                bool isCustomRunningAnim = !string.Equals(animString, "run", StringComparison.OrdinalIgnoreCase);

                                if (isCustomRunningAnim)
                                {
                                    int[] landRunBridgeHashes = {
                                        Animator.StringToHash("land"), Animator.StringToHash("landRun")
                                    };

                                    int[] trickBridgeHashes = {
                                        Animator.StringToHash("groundTrick0"), Animator.StringToHash("groundTrick1"), Animator.StringToHash("groundTrick2"), Animator.StringToHash("groundTrick3"),
                                        Animator.StringToHash("groundBoostTrick0"), Animator.StringToHash("groundBoostTrick1"), Animator.StringToHash("groundBoostTrick2")
                                     };

                                    int[] fallBridgeHashes = {
                                        Animator.StringToHash("fall"), Animator.StringToHash("jump"), Animator.StringToHash("airTrick0"),
                                        Animator.StringToHash("airTrick1"), Animator.StringToHash("airTrick2"), Animator.StringToHash("airTrick3"),
                                        Animator.StringToHash("airDash")
                                    };

                                    foreach (int h in landRunBridgeHashes)
                                    {
                                        if (currentShortHash == h)
                                        {
                                            hashToPlay = Animator.StringToHash("land");
                                            initialNormalizedTime = 0.40f;
                                            isTrickBridge = true;
                                            break;
                                        }
                                    }

                                    if (!isTrickBridge)
                                    {
                                        foreach (int h in trickBridgeHashes)
                                        {
                                            if (currentShortHash == h)
                                            {
                                                hashToPlay = Animator.StringToHash("crouchingIdle");
                                                initialNormalizedTime = 0.00f;
                                                isTrickBridge = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (!isTrickBridge)
                                    {
                                        foreach (int h in fallBridgeHashes)
                                        {
                                            if (currentShortHash == h)
                                            {
                                                hashToPlay = Animator.StringToHash("fall");
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (fromCustomIdle && !isTrickBridge)
                                {
                                    hashToPlay = Animator.StringToHash("startRun");
                                    initialNormalizedTime = 0.0f;
                                }
                            }
                            else
                            {
                                int[] stopBridgeHashes = {
                                    Animator.StringToHash("run"), Animator.StringToHash("landRun"), Animator.StringToHash("land"),
                                    Animator.StringToHash("groundTrick0"), Animator.StringToHash("groundTrick1"), Animator.StringToHash("groundTrick2"),
                                     Animator.StringToHash("groundTrick3"), Animator.StringToHash("stopRun"), Animator.StringToHash("boostBrake"),
                                     Animator.StringToHash("brake")
                                };

                                foreach (int h in stopBridgeHashes)
                                {
                                    if (currentShortHash == h)
                                    {
                                        hashToPlay = Animator.StringToHash("idle");
                                        initialNormalizedTime = 0.0f;
                                        break;
                                    }
                                }
                            }
                        }

                        InlinePlugin.SwapAnimatorControllerSafely(abilityAnimator, customController, hashToPlay, initialNormalizedTime);

                        if (useStartAnim)
                        {
                            if (targetStartAnimHash == Animator.StringToHash("halfStartRun"))
                            {
                                startAnimDuration = 0.35f;
                                startAnimToMainBlend = 0.20f;
                            }
                            else if (targetStartAnimHash == Animator.StringToHash("startRun"))
                            {
                                startAnimDuration = 0.22f;
                                startAnimToMainBlend = 0.15f;
                            }
                            abilityAnimator.CrossFade(targetStartAnimHash, blendTime, 0, 0f);
                        }
                        else if (isTrickBridge)
                        {
                            mainAnimationTriggered = false;
                            startAnimDuration = 0.04f;
                            startAnimToMainBlend = 0.10f;
                        }
                        else
                        {
                            abilityAnimator.CrossFade(targetAnimHash, blendTime, 0, 0f);
                            mainAnimationTriggered = true;
                        }
                    }
                }
            }
        }

        public override void FixedUpdateAbility()
        {
            base.FixedUpdateAbility();

            if (abilityAnimator != null && customController != null && abilityAnimator.runtimeAnimatorController != customController)
            {
                InlinePlugin.SwapAnimatorControllerSafely(abilityAnimator, customController);
            }

            abilityTimer += Time.deltaTime;

            if (charVisComponent != null && triggerStyle == MoveStyle.INLINE)
            {
                Vector3 currentPos = charVisComponent.transform.localPosition;
                charVisComponent.transform.localPosition = new Vector3(currentPos.x, InlinePlugin.YOffset.Value, currentPos.z);
                InlinePlugin.ApplyYOffsetToCharacterVisual(charVisComponent, InlinePlugin.YOffset.Value);
            }

            MoveStyle currentStyle = (MoveStyle)InlinePlugin.GetMemberValue(p, "moveStyle");

            if (!p.motor.isGrounded || currentStyle != triggerStyle) { p.StopCurrentAbility(); return; }

            bool breakToJump = false;
            bool breakToTrick = false;
            bool breakToSlide = false;
            bool breakToBoost = false;
            bool breakToEquip = false;

            if (InlinePlugin.GetMemberValue(p, "jumpButtonNew") is bool jNew && jNew) breakToJump = true;
            if (InlinePlugin.GetMemberValue(p, "trick1ButtonNew") is bool t1 && t1) breakToTrick = true;
            if (InlinePlugin.GetMemberValue(p, "trick2ButtonNew") is bool t2 && t2) breakToTrick = true;
            if (InlinePlugin.GetMemberValue(p, "trick3ButtonNew") is bool t3 && t3) breakToTrick = true;
            if (InlinePlugin.GetMemberValue(p, "slideButtonNew") is bool sNew && sNew) breakToSlide = true;
            if (InlinePlugin.GetMemberValue(p, "boostButtonNew") is bool bNew && bNew) breakToBoost = true;
            if (InlinePlugin.GetMemberValue(p, "boostButtonHeld") is bool bHeld && bHeld) breakToBoost = true;
            if (InlinePlugin.GetMemberValue(p, "switchStyleButtonNew") is bool swNew && swNew) breakToEquip = true;

            if (breakToJump)
            {
                isBreakingOut = true;
                p.StopCurrentAbility(); return;
            }
            if (breakToEquip)
            {
                isBreakingOut = true;
                Ability switchAb = (Ability)InlinePlugin.GetMemberValue(p, "switchMoveStyleAbility") ?? (Ability)InlinePlugin.GetMemberValue(p, "switchStyleAbility");
                if (switchAb != null) p.ActivateAbility(switchAb);
                else p.StopCurrentAbility();
                return;
            }
            if (breakToTrick)
            {
                isBreakingOut = true;
                Ability trickAb = p.motor.isGrounded ?
                    (Ability)InlinePlugin.GetMemberValue(p, "groundTrickAbility") :
                    (Ability)InlinePlugin.GetMemberValue(p, "airTrickAbility");

                if (trickAb != null) p.ActivateAbility(trickAb);
                else p.StopCurrentAbility();
                return;
            }
            if (breakToSlide)
            {
                isBreakingOut = true;
                Ability slideAb = InlinePlugin.GetMemberValue(p, "slideAbility") as Ability;
                if (slideAb != null) p.ActivateAbility(slideAb);
                else p.StopCurrentAbility();
                return;
            }
            if (breakToBoost)
            {
                isBreakingOut = true;
                p.StopCurrentAbility(); return;
            }

            Vector3 moveInput = (Vector3)InlinePlugin.GetMemberValue(p, "moveInput");
            bool isMovingNow = moveInput.sqrMagnitude > 0.01f;

            if (isRunningMode && isMovingNow && p.motor.isGrounded)
            {
                Vector3 currentDir = Vector3.forward;

                object dirObj = InlinePlugin.GetMemberValue(p, "dir");
                if (dirObj is Vector3 d) currentDir = d;

                if (Vector3.Dot(currentDir, moveInput) <= -0.3f)
                {
                    isBreakingOut = true;
                    p.StopCurrentAbility();
                    return;
                }
            }

            if (isMovingNow)
            {
                InlinePlugin.SetMemberValue(p, "dir", moveInput.normalized);
                this.customTargetRot = moveInput;

                if (p.motor != null)
                {
                    Vector3 groundNormal = Vector3.up;
                    object normalObj = InlinePlugin.GetMemberValue(p.motor, "groundNormal");
                    if (normalObj is Vector3 n) groundNormal = n;

                    Vector3 forwardDir = p.motor.transform.forward;
                    Vector3 slopeForward = Vector3.ProjectOnPlane(forwardDir, groundNormal).normalized;
                    if (slopeForward == Vector3.zero) slopeForward = forwardDir;

                    Quaternion targetVisualRot = Quaternion.LookRotation(slopeForward, groundNormal);
                    p.SetVisualRot(targetVisualRot, false);
                }
            }

            if ((useStartAnim || isTrickBridge) && !mainAnimationTriggered)
            {
                bool shouldTransition = false;

                if (!isRunningMode && useStartAnim)
                {
                    if (abilityTimer > 0.1f)
                    {
                        if (abilityAnimator != null)
                        {
                            AnimatorStateInfo state = abilityAnimator.GetCurrentAnimatorStateInfo(0);

                            if (state.shortNameHash == targetStartAnimHash || state.fullPathHash == targetStartAnimHash)
                            {
                                if (state.normalizedTime >= 0.90f)
                                {
                                    shouldTransition = true;
                                }
                            }
                            else if (abilityTimer > 2.5f)
                            {
                                shouldTransition = true;
                            }
                        }
                    }
                }
                else
                {
                    if (abilityTimer >= startAnimDuration)
                    {
                        shouldTransition = true;
                    }
                }

                if (shouldTransition)
                {
                    if (abilityAnimator != null) abilityAnimator.CrossFade(targetAnimHash, startAnimToMainBlend, 0, 0f);
                    mainAnimationTriggered = true;
                }
            }

            if (isRunningMode != isMovingNow) p.StopCurrentAbility();
        }

        public override void OnStopAbility()
        {
            base.OnStopAbility();

            if (abilityAnimator != null && !InlinePlugin.IsTransitioningEmote)
            {
                try
                {
                    if (originalController != null && customController != null)
                    {
                        if (abilityAnimator.runtimeAnimatorController == customController)
                        {
                            if (!isBreakingOut)
                            {
                                Vector3 moveInput = (Vector3)InlinePlugin.GetMemberValue(p, "moveInput");
                                bool isMoving = moveInput.sqrMagnitude > 0.01f;
                                bool isGrounded = InlinePlugin.IsPlayerGrounded(p);

                                int targetHash = Animator.StringToHash("idle");
                                float stopBlendTime = 0.15f;

                                int bridgeHash = Animator.StringToHash("idle");
                                float bridgeTime = 0f;

                                int finalPlayerStateHash = Animator.StringToHash("idle");

                                if (!isGrounded)
                                {
                                    targetHash = Animator.StringToHash("fall");
                                    stopBlendTime = 0.05f;
                                    bridgeHash = targetHash;
                                    finalPlayerStateHash = targetHash;
                                }
                                else if (isMoving)
                                {
                                    if (!isRunningMode)
                                    {
                                        targetHash = Animator.StringToHash("startRun");
                                        stopBlendTime = 0.15f;
                                        bridgeHash = Animator.StringToHash("idle");
                                        bridgeTime = 0f;
                                        finalPlayerStateHash = Animator.StringToHash("run");
                                    }
                                    else
                                    {
                                        targetHash = Animator.StringToHash("halfStartRun");
                                        stopBlendTime = 0.20f;
                                        bridgeHash = Animator.StringToHash("run");
                                        bridgeTime = abilityAnimator != null ? abilityAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f : 0f;
                                        finalPlayerStateHash = Animator.StringToHash("landRun");
                                    }
                                }
                                else
                                {
                                    if (isRunningMode)
                                    {
                                        targetHash = Animator.StringToHash("stopRun");
                                        stopBlendTime = 0.15f;
                                        bridgeHash = Animator.StringToHash("run");
                                        bridgeTime = abilityAnimator != null ? abilityAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f : 0f;
                                        finalPlayerStateHash = targetHash;
                                    }
                                    else
                                    {
                                        targetHash = Animator.StringToHash("idle");
                                        stopBlendTime = 0.15f;
                                        bridgeHash = targetHash;
                                        finalPlayerStateHash = targetHash;
                                    }
                                }

                                InlinePlugin.SwapAnimatorControllerSafely(abilityAnimator, originalController, bridgeHash, bridgeTime);
                                abilityAnimator.applyRootMotion = originalRootMotion;

                                if (abilityAnimator.HasState(0, targetHash))
                                    abilityAnimator.CrossFade(targetHash, stopBlendTime, 0, 0f);
                                else
                                    abilityAnimator.Play(Animator.StringToHash("idle"), 0, 0f);

                                InlinePlugin.SetMemberValue(p, "curAnim", finalPlayerStateHash);
                                InlinePlugin.SetMemberValue(p, "targetAnim", finalPlayerStateHash);
                            }
                            else
                            {
                                int targetHashToPlay = Animator.StringToHash("idle");
                                object targetAnimObj = InlinePlugin.GetMemberValue(p, "targetAnim");

                                if (targetAnimObj is int targetHash && targetHash != 0)
                                {
                                    targetHashToPlay = targetHash;
                                }

                                InlinePlugin.SwapAnimatorControllerSafely(abilityAnimator, originalController, targetHashToPlay, 0f);
                                abilityAnimator.applyRootMotion = originalRootMotion;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    InlinePlugin.Log.LogWarning("Failed to resync vanilla emote animations: " + e.Message);
                }
            }

            if (!InlinePlugin.IsTransitioningEmote)
            {
                if (charVisObj != null) InlinePlugin.SetMemberValue(charVisObj, "feetIK", originalFeetIK);

                if (charVisComponent != null)
                {
                    InlinePlugin.ApplyYOffsetToCharacterVisual(charVisComponent, 0f);
                    charVisComponent.transform.localPosition = new Vector3(charVisComponent.transform.localPosition.x, 0f, charVisComponent.transform.localPosition.z);
                }
            }
        }
    }

    public static class BunchOfEmotesSupport
    {
        public static bool Installed { get; private set; } = false;

        private static object BoEPluginInstance = null;
        private static Type BoEPluginType = null;

        private static FieldInfo CustomAnimsField = null;
        private static FieldInfo ControllerField = null;

        private static bool Cached = false;

        private static Dictionary<string, int> GameAnimationByCustomAnimation = new();

        private static RuntimeAnimatorController CachedController = null;

        public static void Initialize()
        {
            Installed = true;

            BoEPluginInstance = Chainloader.PluginInfos["com.Dragsun.BunchOfEmotes"].Instance;
            BoEPluginType = InlinePlugin.ReflectionUtility.GetTypeByName("BunchOfEmotes.BunchOfEmotesPlugin");

            CustomAnimsField = BoEPluginType.GetField("myCustomAnims2") ?? BoEPluginType.GetField("myCustomAnims");
            ControllerField = BoEPluginType.GetField("myAnim", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        }

        public static RuntimeAnimatorController GetController()
        {
            if (!Installed || ControllerField == null || BoEPluginInstance == null) return null;

            if (CachedController == null)
            {
                CachedController = ControllerField.GetValue(BoEPluginInstance) as RuntimeAnimatorController;
            }
            return CachedController;
        }

        public static void CacheAnimationsIfNecessary()
        {
            if (Cached) return;

            var boeDictionary = CustomAnimsField?.GetValue(BoEPluginInstance) as Dictionary<int, string>;
            if (boeDictionary != null && boeDictionary.Count > 0) Cached = true;
            else return;

            foreach (var customAnim in boeDictionary)
            {
                GameAnimationByCustomAnimation[customAnim.Value] = customAnim.Key;
            }
        }

        public static bool TryGetGameAnimationForCustomAnimation(string customAnim, out int gameAnim)
        {
            if (!Installed)
            {
                gameAnim = 0;
                return false;
            }

            CacheAnimationsIfNecessary();

            if (GameAnimationByCustomAnimation.TryGetValue(customAnim, out gameAnim)) return true;

            gameAnim = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player))]
    internal class PlayerStairsPatch
    {
        [HarmonyPatch("OnTriggerStay")]
        [HarmonyPostfix]
        private static void OnTriggerStay(Player __instance)
        {
            if (!InlinePlugin.UnequipOnStairs.Value)
            {
                object isAIObj = InlinePlugin.GetMemberValue(__instance, "isAI");
                bool isAI = isAIObj is bool b && b;

                if (!isAI)
                {
                    InlinePlugin.SetMemberValue(__instance, "inWalkZone", false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player))]
    internal class PlayerNewTrixProtectionPatch
    {
        [HarmonyPatch("StopCurrentAbility")]
        [HarmonyPrefix]
        private static void StopCurrentAbility_Prefix(Player __instance)
        {
            if (InlinePlugin.Instance != null && InlinePlugin.Instance.IsBoostVisualOverrideActive)
            {
                InlinePlugin.Log.LogInfo("[BRCInlineAnims] StopCurrentAbility Prefix: Halting Boost Override early for compatibility.");
                InlinePlugin.Instance.StopBoostVisualOverride(__instance, false, true);
            }
        }

        [HarmonyPatch("StopCurrentAbility")]
        [HarmonyPostfix]
        private static void StopCurrentAbility_Postfix(Player __instance)
        {
            MoveStyle style = (MoveStyle)InlinePlugin.GetMemberValue(__instance, "moveStyle");
            object charVisObj = InlinePlugin.GetMemberValue(__instance, "characterVisual");
            if (charVisObj == null) return;

            Animator anim = (Animator)InlinePlugin.GetMemberValue(charVisObj, "anim");
            if (anim != null)
            {
                if (style == MoveStyle.INLINE && InlinePlugin.CachedVanillaInlineController != null)
                {
                    if (anim.runtimeAnimatorController != InlinePlugin.CachedVanillaInlineController &&
                        anim.runtimeAnimatorController != InlinePlugin.MergedInlineController)
                    {
                        InlinePlugin.Log.LogInfo("[BRCInlineAnims] NewTrix Protection: Restoring Vanilla Inline Controller after ability stop.");
                        InlinePlugin.SwapAnimatorControllerSafely(anim, InlinePlugin.CachedVanillaInlineController);
                    }
                }
                else if (style == MoveStyle.ON_FOOT && InlinePlugin.CachedVanillaOnFootController != null)
                {
                    if (anim.runtimeAnimatorController != InlinePlugin.CachedVanillaOnFootController &&
                        anim.runtimeAnimatorController != BunchOfEmotesSupport.GetController())
                    {
                        InlinePlugin.Log.LogInfo("[BRCInlineAnims] NewTrix Protection: Restoring Vanilla OnFoot Controller after ability stop.");
                        InlinePlugin.SwapAnimatorControllerSafely(anim, InlinePlugin.CachedVanillaOnFootController);
                    }
                }
            }
        }

        [HarmonyPatch("PlayAnim", new Type[] { typeof(int), typeof(bool), typeof(bool), typeof(float) })]
        [HarmonyPrefix]
        private static bool PlayAnim_Prefix(Player __instance, int newAnim)
        {
            if (InlinePlugin.Instance != null && InlinePlugin.Instance.IsBoostVisualOverrideActive)
            {
                int boostRunHash = Animator.StringToHash("boostRun");
                int startBoostHash = Animator.StringToHash("startBoost");

                string targetBoostAnim = InlinePlugin.TargetBoostAnim.Value;
                int targetAnimHash = Animator.StringToHash(targetBoostAnim);
                int boeTargetHash = 0;
                BunchOfEmotesSupport.TryGetGameAnimationForCustomAnimation(targetBoostAnim, out boeTargetHash);

                if (newAnim == boostRunHash || newAnim == startBoostHash ||
                    newAnim == targetAnimHash || (boeTargetHash != 0 && newAnim == boeTargetHash))
                {
                    InlinePlugin.SetMemberValue(__instance, "curAnim", newAnim);
                    InlinePlugin.SetMemberValue(__instance, "targetAnim", newAnim);
                    return false;
                }
                else
                {
                    InlinePlugin.Instance.StopBoostVisualOverride(__instance, false, true, newAnim);
                }
            }

            int jumpHash = Animator.StringToHash("jump");
            int fallHash = Animator.StringToHash("fall");

            if (newAnim == jumpHash || newAnim == fallHash)
            {
                MoveStyle style = (MoveStyle)InlinePlugin.GetMemberValue(__instance, "moveStyle");
                object charVisObj = InlinePlugin.GetMemberValue(__instance, "characterVisual");
                if (charVisObj == null) return true;

                Animator anim = (Animator)InlinePlugin.GetMemberValue(charVisObj, "anim");
                if (anim != null)
                {
                    object currentAb = InlinePlugin.GetMemberValue(__instance, "ability");

                    if (style == MoveStyle.INLINE && InlinePlugin.CachedVanillaInlineController != null)
                    {
                        if (anim.runtimeAnimatorController != InlinePlugin.CachedVanillaInlineController &&
                            anim.runtimeAnimatorController != InlinePlugin.MergedInlineController)
                        {
                            if (!(currentAb is UnifiedEmoteAbility))
                            {
                                InlinePlugin.Log.LogInfo("[BRCInlineAnims] NewTrix Protection: Forcing Inline Controller for Airborne State.");
                                InlinePlugin.SwapAnimatorControllerSafely(anim, InlinePlugin.CachedVanillaInlineController);
                            }
                        }
                    }
                }
            }

            return true;
        }
    }
}