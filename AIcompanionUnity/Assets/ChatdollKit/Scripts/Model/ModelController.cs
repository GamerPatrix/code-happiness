using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace ChatdollKit.Model
{
    public class ModelController : MonoBehaviour
    {
        // Avator
        [Header("Avatar")]
        public GameObject AvatarModel;

        // Animation
        [Header("Animation")]
        [SerializeField]
        private float IdleAnimationDefaultDuration = 10.0f;
        [SerializeField]
        private string IdleAnimationKey = "BaseParam";
        [SerializeField]
        private int IdleAnimationValue;
        [SerializeField]
        private string layeredAnimationDefaultState = "Default";
        public float AnimationFadeLength = 0.5f;
        // Upper bound for how long AnimatedSay() waits for an animation to play out when there
        // is no audio occupying that time (e.g. a text-only companion whose speech synthesizer
        // returns no clip). Keeps a reply made of many tagged sentences from stalling. It is
        // generous enough to cover a clip the LLM asked to see repeated several times in a row.
        [SerializeField]
        private float MaxAnimationHoldSeconds = 30f;
        // Idles are picked at random, but back-to-back picks would look robotic and a clip that is
        // cut off exactly at its last frame pops. These stretch/shrink how long each idle is allowed
        // to run and how long the avatar may just stand there before the next one starts.
        [SerializeField]
        private float IdleDurationJitterMin = 0.85f;
        [SerializeField]
        private float IdleDurationJitterMax = 1.35f;
        [SerializeField]
        private float IdleGapMinSeconds = 0.2f;
        [SerializeField]
        private float IdleGapMaxSeconds = 1.5f;
        // Safety net for animations that point at each other with `next=`.
        private const int MaxChainedAnimations = 8;
        private const string DefaultIdlingMode = "normal";
        private Animator animator;
        private List<Animation> animationQueue = new List<Animation>();
        private float idleTailGap;
        private int chainedAnimations;
        private HashSet<string> warnedIdlingModes = new HashSet<string>();
        private float animationStartAt { get; set; }
        private Animation currentAnimation { get; set; }
        private Dictionary<string, List<Animation>> idleAnimations = new Dictionary<string, List<Animation>>();
        private Dictionary<string, List<int>> idleWeightedIndexes = new Dictionary<string, List<int>>();
        private Dictionary<string, FaceExpression> idleFaces = new Dictionary<string, FaceExpression>();
        public string IdlingMode { get; private set; } = "normal";
        public DateTime IdlingModeStartAt { get; private set; } = DateTime.UtcNow;
        private Func<Animation> GetAnimation;
        private Dictionary<string, Animation> registeredAnimations { get; set; } = new Dictionary<string, Animation>();

        // Speech
        // Resolved lazily: other components (e.g. AIAvatar) read this from their own Awake(), and
        // Unity does not guarantee Awake() order between components on the same GameObject.
        private SpeechController speechController;
        public SpeechController SpeechController
        {
            get
            {
                if (speechController == null)
                {
                    speechController = gameObject.GetComponent<SpeechController>();
                }
                return speechController;
            }
            set { speechController = value; }
        }
        public Func<string, Dictionary<string, object>, CancellationToken, UniTask<AudioClip>> SpeechSynthesizerFunc
        {
            get { return SpeechController.SpeechSynthesizerFunc; }
            set { SpeechController.SpeechSynthesizerFunc = value; }
        }

        // Face
        public FaceController FaceController { get; set; }

        // Helpers
        private IBlink blinker { get; set; }
        private ILipSyncHelper lipSyncHelper;

        // History recorder for debug and test
        public ActionHistoryRecorder History;

        private void Awake()
        {
            GetAnimation = GetIdleAnimation;

            // Sub Controllers
            SpeechController = gameObject.GetComponent<SpeechController>();
            FaceController = gameObject.GetComponent<FaceController>();

            // LipSyncHelper
            lipSyncHelper = gameObject.GetComponent<ILipSyncHelper>();

            // Blinker
            blinker = gameObject.GetComponent<IBlink>();

            if (AvatarModel == null)
            {
                enabled = false;
            }
            else
            {
                ActivateAvatar();
            }
        }

        private void Start()
        {
            if (idleAnimations.Count == 0)
            {
                // Set idle animation if not registered
                Debug.LogWarning("Idle animations are not registered. Temprarily use dafault state as idle animation.");
                AddIdleAnimation(new Animation(
                    IdleAnimationKey, IdleAnimationValue, IdleAnimationDefaultDuration
                ));
            }

            // NOTE: Do not start idling here to prevent overwrite the animation that user invokes at Start() in other module.
            // Don't worry, idling will be started at UpdateAnimation() if user doesn't invoke their custom animation.
        }

        private void Update()
        {
            UpdateAnimation();
        }

        private void LateUpdate()
        {
            // Move to avatar position (because this game object includes AudioSource)
            gameObject.transform.position = AvatarModel.transform.position;
        }

#region Idling
        // Start idling.
        // A pose with no explicit hold is "sticky": it stays until something replaces it, which is
        // exactly what the LLM asked for when it said [pose:Sit]. AIAvatar calls this at the end of
        // every reply, so those calls must not snap a held pose back to idle.
        public void StartIdling(bool resetStartTime = true)
        {
            if (IsHoldingPose())
            {
                return;
            }

            // AIAvatar calls this at the end of every reply, but a reply can still have work
            // pending: an animation queued after the one now playing, or one pushed by `next=`.
            // Handing the Animator over to the idle picker here would drop that work, which is why
            // "[anim:Clap next=Sit]" used to show only the clap. Let the queue drain first -
            // UpdateAnimation() calls this again once GetQueuedAnimation() runs out.
            if (GetAnimation == GetQueuedAnimation && animationQueue.Count > 0)
            {
                return;
            }

            // Only switch to idle if not already idling or if forced reset
            if (GetAnimation != GetIdleAnimation || resetStartTime)
            {
                GetAnimation = GetIdleAnimation;
                if (resetStartTime)
                {
                    animationStartAt = 0;
                    // Nothing is pending, so never let an old queue resurrect via StopIdling().
                    animationQueue.Clear();
                }
            }
        }

        // True while a pose that should stay forever is the animation currently playing
        private bool IsHoldingPose()
        {
            return currentAnimation != null && currentAnimation.IsPose && currentAnimation.HoldSeconds <= 0f;
        }

        // Stop idling
        public void StopIdling()
        {
            GetAnimation = GetQueuedAnimation;
        }

        // Register idling animation
        public void AddIdleAnimation(Animation animation, int weight = 1, string mode = "normal")
        {
            if (!idleAnimations.ContainsKey(mode))
            {
                idleAnimations.Add(mode, new List<Animation>());
                idleWeightedIndexes.Add(mode, new List<int>());
            }

            idleAnimations[mode].Add(animation);

            // Set weight
            var index = idleAnimations[mode].Count - 1;
            for (var i = 0; i < weight; i++)
            {
                idleWeightedIndexes[mode].Add(index);
            }
        }

        public void AddIdleAnimation(string name, float duration, int weight = 1, string mode = "normal")
        {
            AddIdleAnimation(GetRegisteredAnimation(name, duration), weight, mode);
        }

        // Register idling face expression for mode
        public void AddIdleFace(string mode, string name)
        {
            // Indexer, not Add(): re-registering the same mode must not throw.
            idleFaces[mode] = new FaceExpression(name, float.MaxValue);
        }

        // Change idling mode
        public async UniTask ChangeIdlingModeAsync(string mode = "normal", Func<UniTask> onBeforeChange = null)
        {
            IdlingMode = mode;

            if (onBeforeChange != null)
            {
                await onBeforeChange();
            }

            if (idleFaces.ContainsKey(mode))
            {
                FaceController.SetFace(new List<FaceExpression>() { idleFaces[mode] });
            }
            else if (mode == DefaultIdlingMode)
            {
                FaceController.SetFace(new List<FaceExpression>() { new FaceExpression("Neutral", 0.0f, string.Empty) });
            }
            IdlingModeStartAt = DateTime.UtcNow;

            // Switching idle mode is an explicit request, so it has to be able to replace a sticky pose.
            ClearCurrentAnimation();
            StartIdling(true);
        }

        // Drop whatever is playing so the next idle pick starts from scratch
        private void ClearCurrentAnimation()
        {
            currentAnimation = null;
            animationStartAt = 0;
            idleTailGap = 0f;
            chainedAnimations = 0;
        }

        // Public entry point for the LLM's [idle:mode] tag.
        // Unknown modes are ignored rather than throwing, so a hallucinated mode never breaks the reply.
        public async UniTask ChangeIdlingModeFromTagAsync(string mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }

            mode = mode.Trim();
            var resolved = ResolveIdlingMode(mode);
            if (resolved == null)
            {
                Debug.LogWarning($"Idling mode '{mode}' has no animations registered. Available: {string.Join(", ", idleAnimations.Keys)}");
                return;
            }

            await ChangeIdlingModeAsync(resolved);
        }

        // Returns a mode that actually has idles, or null. Falls back to the default mode, and then to
        // the first registered mode - a project whose idles live in `Idle/Standing/` and `Idle/Sitting/`
        // has no "normal" mode at all, and the avatar would otherwise stand completely still.
        private string ResolveIdlingMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                mode = DefaultIdlingMode;
            }

            foreach (var key in idleAnimations.Keys)
            {
                if (string.Equals(key, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            if (idleAnimations.ContainsKey(DefaultIdlingMode) && idleAnimations[DefaultIdlingMode].Count > 0)
            {
                if (warnedIdlingModes.Add(mode))
                {
                    Debug.LogWarning($"Idling mode '{mode}' is not registered. Falling back to '{DefaultIdlingMode}'.");
                }
                return DefaultIdlingMode;
            }

            foreach (var kvp in idleAnimations)
            {
                if (kvp.Value != null && kvp.Value.Count > 0)
                {
                    if (warnedIdlingModes.Add(mode))
                    {
                        Debug.LogWarning($"Idling mode '{mode}' is not registered. Falling back to '{kvp.Key}'.");
                    }
                    return kvp.Key;
                }
            }

            return null;
        }
#endregion

        // Speak with animation and face expression
        public async UniTask AnimatedSay(AnimatedVoiceRequest request, CancellationToken token)
        {
            // Speak, animate and express face sequentially
            foreach (var animatedVoice in request.AnimatedVoices)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // Idling mode, e.g. the avatar sits down and stays sitting for the rest of the chat
                if (!string.IsNullOrEmpty(animatedVoice.IdlingModeChange))
                {
                    await ChangeIdlingModeFromTagAsync(animatedVoice.IdlingModeChange);
                }

                // Face
                if (animatedVoice.Faces != null && animatedVoice.Faces.Count > 0)
                {
                    FaceController.SetFace(animatedVoice.Faces);
                }

                // Animation
                if (animatedVoice.Animations.Count > 0)
                {
                    Animate(animatedVoice.Animations);
                }

                // Speech
                if (animatedVoice.Voices.Count > 0)
                {
                    // Wait for the requested voices end
                    await SpeechController.Say(animatedVoice.Voices, token);
                }

                // Hold until the animations queued for this sentence have played out.
                // Speech normally already occupies this time, but when the synthesizer returns
                // nothing the loop would otherwise run every sentence in a single frame and only
                // the last animation would ever reach the Animator.
                if (animatedVoice.Animations.Count > 0)
                {
                    await WaitForAnimationsAsync(animatedVoice.Animations, token);
                }
            }

            if (request.StartIdlingOnEnd && !token.IsCancellationRequested)
            {
                // Only reset to idle if we're not already idling
                // This prevents interrupting ongoing idle animations
                if (GetAnimation != GetIdleAnimation)
                {
                    // Wait for current animation to complete before starting idle
                    if (currentAnimation != null)
                    {
                        var remainingTime = currentAnimation.EffectiveDuration - (Time.realtimeSinceStartup - animationStartAt);
                        if (remainingTime > 0)
                        {
                            await UniTask.Delay((int)(Mathf.Min(remainingTime, MaxAnimationHoldSeconds) * 1000), cancellationToken: token);
                        }
                    }
                    // Stop running animation not to keep the current state to the next turn (prompting)
                    // Do not start idling when cancel requested because the next animated voice request may include animations
                    StartIdling();
                }
                // If already idling, don't interrupt the current idle animation
            }
        }

        // Wait until the animations queued for one sentence have played out.
        // Animate() starts the first one and stamps animationStartAt, so the time left
        // can be measured right away.
        private async UniTask WaitForAnimationsAsync(List<Animation> animations, CancellationToken token)
        {
            var totalDuration = 0f;
            for (var i = 0; i < animations.Count; i++)
            {
                var animation = animations[i];
                if (animation == null)
                {
                    continue;
                }

                // A pose with no hold is meant to last forever, so waiting for it would just stall
                // the reply for the whole clamp. Contribute nothing and stop following the chain -
                // whatever is behind a sticky pose can never be reached anyway.
                if (animation.EffectiveDuration > MaxAnimationHoldSeconds)
                {
                    continue;
                }

                totalDuration += animation.EffectiveDuration;

                // `next=` links are pushed into the queue only after the animation before them
                // expires, so they are not in `animations`. AnimatedSay has to stay alive for them
                // as well, otherwise it returns early and the next sentence's Animate() clears the
                // queue before the chained animation ever reaches the Animator.
                var nextName = animation.NextAnimationName;
                for (var hops = 0; hops < MaxChainedAnimations && !string.IsNullOrEmpty(nextName); hops++)
                {
                    var next = GetRegisteredAnimationByName(nextName);
                    if (next == null || next.EffectiveDuration > MaxAnimationHoldSeconds)
                    {
                        break;
                    }

                    totalDuration += next.EffectiveDuration;
                    nextName = next.NextAnimationName;
                }
            }

            var remainingTime = totalDuration - (Time.realtimeSinceStartup - animationStartAt);
            if (remainingTime > 0)
            {
                await UniTask.Delay((int)(Mathf.Min(remainingTime, MaxAnimationHoldSeconds) * 1000), cancellationToken: token);
            }
        }

        public AnimatedVoiceRequest ToAnimatedVoiceRequest(string inputText, string language = null)
        {
            var avreq = new AnimatedVoiceRequest();
            var preGap = 0f;
            var ttsConfig = new TTSConfiguration();
            if (!string.IsNullOrEmpty(language))
            {
                ttsConfig.Params["language"] = language;
            }

            var pattern = @"(\[.*?\])|([^[]+)";
            foreach (Match match in Regex.Matches(inputText, pattern))
            {
                var parsedText = match.Value.Trim();

                if (parsedText.StartsWith("[face:"))
                {
                    var face = parsedText.Substring(6, parsedText.Length - 7);
                    avreq.AddFace(face, duration: FaceController.DefaultFaceExpressionDuration);
                    ttsConfig.Params["style"] = face;
                }
                else if (parsedText.StartsWith("[anim:") || parsedText.StartsWith("[pose:"))
                {
                    var isPoseTag = parsedText.StartsWith("[pose:");
                    var inner = parsedText.Substring(6, parsedText.Length - 7).Trim();
                    var animation = CreateAnimationFromTag(inner, isPoseTag);
                    if (animation != null)
                    {
                        avreq.AddAnimation(animation);
                    }
                }
                else if (parsedText.StartsWith("[idle:"))
                {
                    var mode = parsedText.Substring(6, parsedText.Length - 7).Trim();
                    if (!string.IsNullOrEmpty(mode))
                    {
                        avreq.AddIdlingModeChange(mode);
                    }
                }
                else if (parsedText.StartsWith("[pause:"))
                {
                    var pauseValue = parsedText.Substring(7, parsedText.Length - 8);
                    if (float.TryParse(pauseValue, out float gap))
                    {
                        preGap = gap;
                    }
                }
                else if (parsedText.StartsWith("["))
                {
                    continue;
                }
                else
                {
                    avreq.AddVoice(parsedText, preGap, parsedText.EndsWith("。") ? 0 : 0.3f, ttsConfig: ttsConfig);
                    // Reset preGap. Do not reset ttsConfig to continue the style of voice.
                    preGap = 0f;
                }
            }

            return avreq;
        }

        // Builds one animation from the body of a [anim:...] / [pose:...] tag.
        // Grammar: "Name [loops=N] [hold=SECONDS] [next=NAME]". Names contain spaces ("Female Sitting
        // Pose"), so the name is everything up to the first token that looks like a known option, and
        // the value of `next=` keeps consuming tokens until the next known option. A quoted name is
        // also accepted, which is what the system prompt shows the model.
        private Animation CreateAnimationFromTag(string tagBody, bool isPoseTag)
        {
            if (string.IsNullOrEmpty(tagBody))
            {
                return default;
            }

            var parts = SplitTagBody(tagBody);
            var name = new StringBuilder();
            var loops = 1;
            var hold = 0f;
            var next = (string)null;
            var hasHold = false;
            var i = 0;

            // Everything before the first recognised `key=` token is the animation name.
            while (i < parts.Count && !IsAnimationOptionToken(parts[i]))
            {
                if (name.Length > 0)
                {
                    name.Append(' ');
                }

                name.Append(parts[i]);
                i++;
            }

            for (; i < parts.Count; i++)
            {
                var eq = parts[i].IndexOf('=');
                var key = parts[i].Substring(0, eq).Trim().ToLowerInvariant();
                var value = parts[i].Substring(eq + 1).Trim().Trim('"', '\'');

                switch (key)
                {
                    case "loops":
                    case "loop":
                    case "repeat":
                    case "times":
                        if (!int.TryParse(value, out loops) || loops < 1)
                        {
                            Debug.LogWarning($"Invalid loops value '{value}' in tag [{(isPoseTag ? "pose" : "anim")}:{tagBody}].");
                            loops = 1;
                        }
                        break;
                    case "hold":
                    case "holdseconds":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hold))
                        {
                            Debug.LogWarning($"Invalid hold value '{value}' in tag [{(isPoseTag ? "pose" : "anim")}:{tagBody}].");
                            hold = 0f;
                        }
                        hasHold = true;
                        break;
                    case "next":
                    case "then":
                        // The following animation's name can contain spaces too, so keep taking tokens.
                        var sbNext = new StringBuilder(value);
                        while (i + 1 < parts.Count && !IsAnimationOptionToken(parts[i + 1]))
                        {
                            i++;
                            sbNext.Append(' ').Append(parts[i]);
                        }
                        next = sbNext.Length == 0 ? null : sbNext.ToString().Trim().Trim('"', '\'');
                        break;
                    default:
                        Debug.LogWarning($"Ignoring unsupported animation option '{key}' in tag [{(isPoseTag ? "pose" : "anim")}:{tagBody}].");
                        break;
                }
            }

            var animation = GetRegisteredAnimationByName(name.ToString().Trim().Trim('"', '\''));
            if (animation == null)
            {
                Debug.LogWarning($"Animation {name} is not registered.");
                return default;
            }

            if (isPoseTag)
            {
                animation.Kind = AnimationKind.Pose;
                // A pose the model did not put a timer on stays until the next thing replaces it.
                if (!hasHold)
                {
                    animation.HoldSeconds = 0f;
                }
            }

            animation.Loops = loops;
            if (hasHold)
            {
                animation.HoldSeconds = hold;
            }
            animation.NextAnimationName = next;
            return animation;
        }

        // True when a whitespace token looks like a `key=` option. Any alphabetic key counts, so a
        // misspelled option warns instead of silently becoming part of the animation name.
        private static bool IsAnimationOptionToken(string token)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            for (var i = 0; i < eq; i++)
            {
                if (!char.IsLetter(token[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // Splits on whitespace but keeps a double/single quoted run together, so
        // [anim:"Female Sitting Pose" loops=2] is two tokens rather than five.
        private static List<string> SplitTagBody(string tagBody)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            foreach (var c in tagBody)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    else
                    {
                        current.Append(c);
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Length = 0;
                    }

                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }

            return parts;
        }

        // Case-insensitive lookup that hands out a private copy, so changing loops/hold/next on one
        // request never mutates the registered template other requests share.
        private Animation GetRegisteredAnimationByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return default;
            }

            if (!registeredAnimations.TryGetValue(name, out var template))
            {
                foreach (var kvp in registeredAnimations)
                {
                    if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        template = kvp.Value;
                        break;
                    }
                }
            }

            return template == null ? default : template.Clone();
        }

#region Animation
        // Set animations to queue
        public void Animate(List<Animation> animations)
        {
            ResetLayers();
            animationQueue.Clear();
            animationQueue = new List<Animation>(animations);
            animationStartAt = 0;
            chainedAnimations = 0;
            // An explicit request always wins over a sticky pose left over from the previous reply.
            currentAnimation = null;
            idleTailGap = 0f;
            GetAnimation = GetQueuedAnimation;

            // Start the first queued animation right away instead of waiting for the next
            // Update(). Callers such as AnimatedSay() can run several Animate() calls within
            // a single frame, and without this only the last one would ever reach the Animator.
            UpdateAnimation();
        }

        private void ResetLayers()
        {
            for (var i = 1; i < animator.layerCount; i++)
            {
                animator.CrossFadeInFixedTime(layeredAnimationDefaultState, AnimationFadeLength, i);
            }
        }

        private void UpdateAnimation()
        {
            // This method will be called every frames in `Update()`
            var animationToRun = GetAnimation();
            if (animationToRun == null)
            {
                // Start idling instead when animationToRun is null
                // `resetStartTime = false`: When idling, GetIdleAnimation() returns null if idle animation is not timeout.
                // So, StartIdling() will be called every frame while idle animation is not timeout.
                // If set 0 `animationStartAt` everytime StartIdling() called, idle animation changes every frame.
                // This `false` prevents reset `animationStartAt` to keep running idle animation.
                StartIdling(false);
                return;
            }

            if (currentAnimation == null || animationToRun.Id != currentAnimation.Id)
            {
                // Start new animation
                StartAnimation(animationToRun);
            }
        }

        // Push an animation into the Animator and (re)start its clock.
        // restartFromBeginning is used when the same clip plays again because the model asked for
        // loops: the layers must keep their current blend and the state simply rewinds to frame 0.
        private void StartAnimation(Animation animation, bool restartFromBeginning = false)
        {
            if (restartFromBeginning)
            {
                RestartState(animation);
            }
            else
            {
                ResetLayers();
                animator.SetInteger(animation.ParameterKey, animation.ParameterValue);
                if (!string.IsNullOrEmpty(animation.LayeredAnimationName))
                {
                    var layerIndex = animator.GetLayerIndex(animation.LayeredAnimationLayerName);
                    animator.CrossFadeInFixedTime(animation.LayeredAnimationName, AnimationFadeLength, layerIndex);
                }
            }

            currentAnimation = animation;
            animationStartAt = Time.realtimeSinceStartup;
        }

        // Restarts the clip from the beginning. Without this, asking for the same state again leaves the
        // Animator where it was and the loop never actually replays.
        private void RestartState(Animation animation)
        {
            if (string.IsNullOrEmpty(animation.AnimatorStateName) || animator == null)
            {
                return;
            }

            var layerIndex = string.IsNullOrEmpty(animation.AnimatorLayerName)
                ? 0
                : animator.GetLayerIndex(animation.AnimatorLayerName);
            if (layerIndex < 0)
            {
                layerIndex = 0;
            }

            animator.Play(animation.AnimatorStateName, layerIndex, 0f);
        }

        private Animation GetQueuedAnimation()
        {
            if (animationQueue.Count == 0) return default;

            var current = animationQueue[0];
            if (animationStartAt > 0 && current != null && Time.realtimeSinceStartup - animationStartAt > current.LastLoopDuration)
            {
                // The current play-through is over. Play it again if the model asked for repeats.
                if (current.LoopsPlayed < Mathf.Max(1, current.Loops) - 1)
                {
                    current.LoopsPlayed++;
                    StartAnimation(current, true);
                    return current;
                }

                // Done: move on to whatever comes next.
                animationQueue.RemoveAt(0);
                animationStartAt = 0;

                var next = ResolveNextAnimation(current);
                if (next != null)
                {
                    chainedAnimations++;
                    animationQueue.Insert(0, next);
                }

                // If queue is empty after removing, ensure smooth transition
                if (animationQueue.Count == 0)
                {
                    // Let the current animation finish naturally before transitioning
                    return default;
                }
            }

            if (animationQueue.Count == 0) return default;

            return animationQueue[0];
        }

        // Follows the `next=` link the model put on an animation. Bounded so two clips that point at
        // each other can't spin the queue forever.
        private Animation ResolveNextAnimation(Animation from)
        {
            if (string.IsNullOrEmpty(from.NextAnimationName) || chainedAnimations >= MaxChainedAnimations)
            {
                return default;
            }

            var next = GetRegisteredAnimationByName(from.NextAnimationName);
            if (next == null)
            {
                Debug.LogWarning($"Next animation {from.NextAnimationName} for {from.Name} is not registered.");
            }

            return next;
        }

        private Animation GetIdleAnimation()
        {
            var weighted = idleWeightedIndexes.TryGetValue(IdlingMode, out var w) ? w : null;
            if (weighted == null || weighted.Count == 0)
            {
                var fallback = ResolveIdlingMode(IdlingMode);
                if (fallback == null || fallback == IdlingMode)
                {
                    return default;
                }

                IdlingMode = fallback;
                if (!idleWeightedIndexes.TryGetValue(IdlingMode, out weighted) || weighted.Count == 0)
                {
                    return default;
                }
            }

            if (currentAnimation == null || animationStartAt == 0 || Time.realtimeSinceStartup - animationStartAt > currentAnimation.EffectiveDuration + idleTailGap)
            {
                // Return random idle animation when:
                // - Animation is not running currently (currentAnimation == null)
                // - Right after StartIdling() called (animationStartAt == 0)
                // - Idle animation is timeout (Time.realtimeSinceStartup - animationStartAt > currentAnimation.Duration)
                //   plus a short random gap, so idles don't chain back-to-back without a beat.
                var i = UnityEngine.Random.Range(0, weighted.Count);
                var animation = idleAnimations[IdlingMode][weighted[i]].Clone();

                // Jitter the hold so the same clip never runs for exactly the same length twice.
                // Poses with an explicit hold keep the time the model asked for.
                idleTailGap = 0f;
                if (!animation.IsPose && animation.SingleLoopDuration > 1f)
                {
                    var jitter = UnityEngine.Random.Range(Mathf.Min(IdleDurationJitterMin, IdleDurationJitterMax), Mathf.Max(IdleDurationJitterMin, IdleDurationJitterMax));
                    animation.Duration = animation.SingleLoopDuration * jitter;
                    idleTailGap = UnityEngine.Random.Range(Mathf.Min(IdleGapMinSeconds, IdleGapMaxSeconds), Mathf.Max(IdleGapMinSeconds, IdleGapMaxSeconds));
                }

                return animation;
            }
            else
            {
                return default;
            }
        }

        public void RegisterAnimations(Dictionary<string, Animation> animations)
        {
            foreach (var animation in animations)
            {
                RegisterAnimation(animation.Key, animation.Value);
            }
        }

        public void RegisterAnimation(string name, Animation animation)
        {
            registeredAnimations[name] = animation;
        }

        public Animation GetRegisteredAnimation(string name, float duration = 0.0f, string layeredAnimationName = null, string layeredAnimationLayerName = null)
        {
            var animation = GetRegisteredAnimationByName(name);
            if (animation == null)
            {
                Debug.LogWarning($"Animation {name} is not registered.");
                return default;
            }

            if (duration != 0.0f)
            {
                animation.Duration = duration;
            }

            if (layeredAnimationName != null)
            {
                animation.LayeredAnimationName = layeredAnimationName;
            }

            if (layeredAnimationLayerName != null)
            {
                animation.LayeredAnimationLayerName = layeredAnimationLayerName;
            }

            return animation;
        }

        public bool IsAnimationRegistered(string name)
        {
            return GetRegisteredAnimationByName(name) != null;
        }

        public string ListRegisteredAnimations(string itemTemplate = null)
        {
            var template = (itemTemplate == null ? "- {0}" : itemTemplate) + "\n";
            var list = "";
            foreach (var item in registeredAnimations.Keys)
            {
                list += string.Format(template, item);
            }
            return list;
        }
#endregion


#region Avatar
        public void ActivateAvatar(GameObject avatarObject = null, bool configureViseme = false)
        {
            if (avatarObject != null)
            {
                AvatarModel = avatarObject;
            }
            animator = AvatarModel.GetComponent<Animator>();

            // Blink (Blink at first because FaceExpression depends blink)
            blinker.Setup(AvatarModel);
            FaceController.Setup(AvatarModel);

            if (configureViseme)
            {
                lipSyncHelper.ConfigureViseme(AvatarModel);
            }

            _ = blinker.StartBlinkAsync();
            currentAnimation = null;  // Set null to newly start idling animation

            AvatarModel.SetActive(true);
            enabled = true;
        }

        public void DeactivateAvatar(Action unloadAction = null)
        {
            if (AvatarModel == null) return;

            enabled = false;
            blinker.StopBlink();

            AvatarModel.SetActive(false);
            unloadAction?.Invoke();
        }
#endregion
    }
}
