using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ChatdollKit.Dialog;
using ChatdollKit.LLM;
using ChatdollKit.Model;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Animation = ChatdollKit.Model.Animation;

namespace CodeHappiness.Avatar
{
    /// <summary>
    /// Owns every <see cref="AvatarSittingPoint"/> in the scene: tells the LLM which spots exist and
    /// which animations work at each one, moves the avatar to a spot when the model asks for it with
    /// a [sit:"Name"] tag, and (optionally) wanders between the spots while it is idling.
    ///
    /// Mirrors <see cref="AvatarAnimationLibrary"/>: the animation names come from that library, so a
    /// spot can never list a clip the avatar does not actually have.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [DisallowMultipleComponent]
    public class AvatarPointLibrary : MonoBehaviour
    {
        // The generated block is wrapped in these markers so re-applying it never duplicates text and
        // so it survives the animation library rewriting the system message underneath us.
        private const string StartMarker = "<!-- avatar-sitting-points -->";
        private const string EndMarker = "<!-- /avatar-sitting-points -->";

        private const string DefaultSystemPromptTemplate =
@"You also have places you can go to in the room.

Tags:
- [sit:""Name""] moves you to that spot and gets you into it. Write it right before the words the movement should accompany.
- [stand] gets you up out of the spot you are in and back to standing.
- You may add a pose or animation from that spot's own list right after moving, e.g.
    [sit:""Couch far end""][pose:""Female Laying Pose""]

Rules:
- Only use spot names and animation names from the list below, spelled exactly as written and kept inside the quotes.
- A spot only supports the animations listed under it. Never play an animation at a spot that does not list it - the body would end up in the wrong place relative to the furniture.
- Move only when it fits what you are saying or what the conversation calls for. Staying put is perfectly fine.
- Tags are not spoken, so do not read them out or explain them.

Spots you can use:
{0}";

        [Header("References (auto-detected when empty)")]
        [SerializeField] private ModelController modelController;
        [SerializeField] private AvatarAnimationLibrary animationLibrary;
        [SerializeField] private LLMServiceBase llmService;
        [SerializeField] private DialogProcessor dialogProcessor;

        [Header("Spots")]
        [Tooltip("Leave empty to auto-discover every AvatarSittingPoint in the scene.")]
        [SerializeField] private List<AvatarSittingPoint> points = new List<AvatarSittingPoint>();
        [SerializeField] private bool includeInPrompt = true;
        [SerializeField] private string systemPromptTemplate = DefaultSystemPromptTemplate;

        [Header("Idle wandering")]
        [Tooltip("Let the avatar move between its spots by itself while it has nothing to do.")]
        [SerializeField] private bool enableIdleWandering = true;
        [Tooltip("Seconds of doing nothing before it decides to move somewhere.")]
        [SerializeField] private float idleSecondsBeforeWandering = 30f;
        [Tooltip("Minimum seconds between two self-initiated moves.")]
        [SerializeField] private float minSecondsBetweenWanders = 45f;
        [Tooltip("Chance per wander attempt of just staying where it is.")]
        [Range(0f, 1f)]
        [SerializeField] private float stayPutChance = 0.4f;

        private readonly List<AvatarSittingPoint> discovered = new List<AvatarSittingPoint>();
        private CancellationTokenSource wanderTokenSource;
        private float lastWanderAt = float.NegativeInfinity;

        public ModelController ModelController => modelController != null ? modelController : (modelController = Resolve<ModelController>());
        public AvatarAnimationLibrary AnimationLibrary => animationLibrary != null ? animationLibrary : (animationLibrary = Resolve<AvatarAnimationLibrary>());
        public LLMServiceBase LLMService => llmService != null ? llmService : (llmService = Resolve<LLMServiceBase>());
        public DialogProcessor DialogProcessor => dialogProcessor != null ? dialogProcessor : (dialogProcessor = Resolve<DialogProcessor>());

        /// <summary>Every spot in the scene, explicit list first when one was assigned.</summary>
        public IReadOnlyList<AvatarSittingPoint> Points =>
            points != null && points.Count(p => p != null) > 0 ? points : discovered;

        /// <summary>Spot the avatar is currently using, or null when it is just standing around.</summary>
        public AvatarSittingPoint CurrentPoint { get; private set; }

        private void Awake()
        {
            RefreshPoints();

            var controller = ModelController;
            if (controller != null)
            {
                controller.PointChangeAsyncFunc = HandlePointChangeAsync;
            }

            if (includeInPrompt)
            {
                RefreshSystemMessage();
            }
        }

        private void OnEnable()
        {
            StartWanderLoop();
        }

        private void OnDisable()
        {
            StopWanderLoop();

            // Never leave the avatar pinned to a spot by a component that no longer exists.
            if (modelController != null)
            {
                modelController.ReleasePosition();
            }
        }

        /// <summary>Re-scans the scene for spots. Call after adding or removing empties at runtime.</summary>
        public void RefreshPoints()
        {
            discovered.Clear();
            discovered.AddRange(FindObjectsByType<AvatarSittingPoint>(FindObjectsSortMode.None));
        }

        // ---------------------------------------------------------------- LLM prompt

        /// <summary>
        /// Builds the spot list the model sees. Returns empty text when nothing is worth telling it,
        /// which keeps a scene with no spots from adding noise to the system message.
        /// </summary>
        public string BuildSystemPrompt()
        {
            var list = BuildSpotList();
            if (string.IsNullOrEmpty(list))
            {
                return string.Empty;
            }

            var template = systemPromptTemplate;
            if (string.IsNullOrEmpty(template) || !template.Contains("[sit:") || !template.Contains("{0}"))
            {
                template = DefaultSystemPromptTemplate;
            }

            return StartMarker + "\n" + string.Format(template, list) + "\n" + EndMarker;
        }

        public string BuildSpotList()
        {
            var library = AnimationLibrary;
            var builder = new StringBuilder();
            foreach (var point in Points)
            {
                if (point == null || !point.IncludeInPrompt || !point.gameObject.activeInHierarchy)
                {
                    continue;
                }

                builder.Append("- \"").Append(point.PointName).Append("\" (").Append(point.Kind.ToString().ToLowerInvariant()).Append("): ");
                builder.Append(string.IsNullOrWhiteSpace(point.Description) ? "no description" : point.Description.Trim());
                builder.AppendLine();

                if (!point.HasUsableAnimations)
                {
                    builder.AppendLine("        Animations here: none yet - you can only stand at this spot.");
                    continue;
                }

                builder.Append("        Animations here: ");
                var first = true;
                foreach (var entry in point.Animations)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.AnimationName))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        builder.Append("; ");
                    }

                    builder.Append('"').Append(entry.AnimationName).Append('"');
                    var note = !string.IsNullOrWhiteSpace(entry.Note)
                        ? entry.Note
                        : library != null ? library.FindEntry(entry.AnimationName)?.Description : null;
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        builder.Append(" (").Append(note.Trim()).Append(')');
                    }

                    first = false;
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Writes <see cref="BuildSystemPrompt"/> into the LLM service's system message. Idempotent:
        /// the previous block is located by its markers and replaced, so calling this repeatedly after
        /// editing spots never stacks copies of the list.
        /// </summary>
        public void RefreshSystemMessage()
        {
            var service = LLMService;
            if (service == null)
            {
                Debug.LogWarning($"{name}: no LLMServiceBase found, the spot list was not sent to the model.", this);
                return;
            }

            var existing = service.SystemMessageContent ?? string.Empty;
            var startIndex = existing.IndexOf(StartMarker, System.StringComparison.Ordinal);
            if (startIndex >= 0)
            {
                var endIndex = existing.IndexOf(EndMarker, startIndex, System.StringComparison.Ordinal);
                if (endIndex > startIndex)
                {
                    var after = endIndex + EndMarker.Length;
                    // Trim the blank line that separated the block from whatever followed it.
                    while (after < existing.Length && (existing[after] == '\n' || existing[after] == '\r'))
                    {
                        after++;
                    }

                    existing = (existing.Substring(0, startIndex) + existing.Substring(after)).TrimEnd();
                }
            }

            var prompt = BuildSystemPrompt();
            service.SystemMessageContent = string.IsNullOrEmpty(prompt)
                ? existing
                : string.IsNullOrEmpty(existing) ? prompt : existing + "\n\n" + prompt;
        }

        /// <summary>Removes the spot list from the system message.</summary>
        public void ResetSystemMessage()
        {
            var service = LLMService;
            if (service == null)
            {
                return;
            }

            var existing = service.SystemMessageContent ?? string.Empty;
            var startIndex = existing.IndexOf(StartMarker, System.StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return;
            }

            var endIndex = existing.IndexOf(EndMarker, startIndex, System.StringComparison.Ordinal);
            var after = endIndex > startIndex ? endIndex + EndMarker.Length : existing.Length;
            service.SystemMessageContent = (existing.Substring(0, startIndex) + existing.Substring(after)).Trim();
        }

        // ---------------------------------------------------------------- lookup

        /// <summary>Case-insensitive lookup by name, so a slightly different spelling still works.</summary>
        public AvatarSittingPoint FindPoint(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName))
            {
                return null;
            }

            var trimmed = pointName.Trim().Trim('"');
            foreach (var point in Points)
            {
                if (point != null && string.Equals(point.PointName, trimmed, System.StringComparison.OrdinalIgnoreCase))
                {
                    return point;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- movement

        /// <summary>
        /// Handles the raw body of a [sit:"Name"] / [stand] tag. Called by ModelController from
        /// AnimatedSay before the frame's faces and animations run, so the avatar is already in place
        /// when the clip starts.
        /// </summary>
        public async UniTask HandlePointChangeAsync(string tagBody)
        {
            if (string.IsNullOrWhiteSpace(tagBody))
            {
                await StandUpAsync();
                return;
            }

            await MoveToAsync(tagBody, null);
        }

        /// <summary>
        /// Moves the avatar to a spot and plays the clip that gets it into position.
        /// <paramref name="animationName"/> overrides the spot's own default animation.
        /// </summary>
        public async UniTask<bool> MoveToAsync(string pointName, string animationName = null)
        {
            var point = FindPoint(pointName);
            if (point == null)
            {
                Debug.LogWarning($"{name}: no spot called \"{pointName}\". Available: {string.Join(", ", Points.Where(p => p != null).Select(p => p.PointName))}");
                return false;
            }

            return await MoveToAsync(point, animationName);
        }

        public async UniTask<bool> MoveToAsync(AvatarSittingPoint point, string animationName)
        {
            if (point == null)
            {
                return false;
            }

            var controller = ModelController;
            if (controller == null || controller.AvatarModel == null)
            {
                Debug.LogWarning($"{name}: no ModelController/AvatarModel, cannot move to \"{point.PointName}\".", this);
                return false;
            }

            // The idle mode has to change first: ChangeIdlingModeAsync() clears whatever is playing,
            // so doing it afterwards would wipe the sit-down clip that was just started.
            if (!string.IsNullOrWhiteSpace(point.IdleMode))
            {
                await controller.ChangeIdlingModeAsync(point.IdleMode);
            }

            // Root motion is force-disabled, but humanoid clips still carry RootT/RootQ curves that
            // the Animator writes onto the model transform, so a plain teleport gets dragged off the
            // furniture a frame later. The hold re-asserts the spot in LateUpdate() for as long as
            // the avatar is on this point.
            controller.TeleportToAndHold(point.Position, point.Rotation);
            CurrentPoint = point;

            var arrivalAnimationName = string.IsNullOrWhiteSpace(animationName) ? point.DefaultAnimationName : animationName;
            var animation = BuildAnimation(arrivalAnimationName);
            if (animation == null)
            {
                return true;
            }

            WarnIfNotAllowedAt(point, animation.Name);
            controller.Animate(new List<Animation> { animation });
            return true;
        }

        /// <summary>Gets the avatar out of its spot and back to plain standing idling.</summary>
        public async UniTask StandUpAsync()
        {
            var controller = ModelController;
            if (controller == null)
            {
                return;
            }

            CurrentPoint = null;
            controller.ReleasePosition();
            await controller.ChangeIdlingModeAsync("Standing");
        }

        /// <summary>
        /// Builds the runtime animation for a name. Prefers the library entry (it carries the Animator
        /// state, layer, kind and hold settings) and falls back to whatever ModelController has registered.
        /// </summary>
        private Animation BuildAnimation(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var library = AnimationLibrary;
            var entry = library != null ? library.FindEntry(name) : null;
            if (entry != null)
            {
                return entry.ToAnimation();
            }

            var controller = ModelController;
            if (controller != null && controller.IsAnimationRegistered(name))
            {
                return controller.GetRegisteredAnimation(name);
            }

            Debug.LogWarning($"{name}: animation \"{name}\" is not registered, so the spot plays nothing.");
            return null;
        }

        // Only warn once per (spot, animation) pair - the model may well be right about a clip the
        // author simply forgot to tick, and a warning is enough to catch it while authoring.
        private readonly HashSet<string> warnedCombinations = new HashSet<string>();

        private void WarnIfNotAllowedAt(AvatarSittingPoint point, string animationName)
        {
            if (string.IsNullOrEmpty(animationName) || point.ContainsAnimation(animationName))
            {
                return;
            }

            var key = point.PointName + "|" + animationName;
            if (!warnedCombinations.Add(key))
            {
                return;
            }

            Debug.LogWarning($"\"{point.PointName}\" does not list \"{animationName}\" as an animation that works there. " +
                             "Open Tools > Code Happiness > Place Sitting Point and tick it if it really does fit.");
        }

        // ---------------------------------------------------------------- idle wandering

        private void StartWanderLoop()
        {
            StopWanderLoop();
            if (!enableIdleWandering)
            {
                return;
            }

            wanderTokenSource = new CancellationTokenSource();
            WanderLoop(wanderTokenSource.Token).Forget();
        }

        private void StopWanderLoop()
        {
            if (wanderTokenSource == null)
            {
                return;
            }

            wanderTokenSource.Cancel();
            wanderTokenSource.Dispose();
            wanderTokenSource = null;
        }

        private async UniTaskVoid WanderLoop(CancellationToken token)
        {
            // Give the scene a moment to register animations and settle before moving anyone around.
            await UniTask.Delay(5000, cancellationToken: token);

            while (!token.IsCancellationRequested)
            {
                await UniTask.Delay(5000, cancellationToken: token);

                if (!ShouldWander())
                {
                    continue;
                }

                await WanderOnceAsync();
            }
        }

        private bool ShouldWander()
        {
            if (!enableIdleWandering || Points.Count(p => p != null && p.HasUsableAnimations) < 2)
            {
                return false;
            }

            var dialog = DialogProcessor;
            if (dialog != null && dialog.Status != DialogProcessor.DialogStatus.Idling)
            {
                return false;
            }

            var controller = ModelController;
            if (controller == null || controller.AvatarModel == null)
            {
                return false;
            }

            // A pose the model deliberately asked for must survive until the model replaces it.
            if (controller.IsHoldingPose())
            {
                return false;
            }

            if (Time.realtimeSinceStartup - lastWanderAt < minSecondsBetweenWanders)
            {
                return false;
            }

            var idleFor = (float)(System.DateTime.UtcNow - controller.IdlingModeStartAt).TotalSeconds;
            return idleFor >= idleSecondsBeforeWandering;
        }

        private async UniTask WanderOnceAsync()
        {
            lastWanderAt = Time.realtimeSinceStartup;

            if (Random.value < stayPutChance)
            {
                return;
            }

            var candidates = Points.Where(p => p != null && p.HasUsableAnimations && p != CurrentPoint).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            await MoveToAsync(candidates[Random.Range(0, candidates.Count)], null);
        }

        // ---------------------------------------------------------------- helpers

        private T Resolve<T>() where T : Component
        {
            return GetComponent<T>()
                ?? GetComponentInParent<T>()
                ?? GetComponentInChildren<T>()
                ?? FindFirstObjectByType<T>();
        }
    }
}
