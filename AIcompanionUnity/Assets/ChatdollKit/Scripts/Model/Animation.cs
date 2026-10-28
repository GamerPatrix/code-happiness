using System;

namespace ChatdollKit.Model
{
    /// <summary>
    /// A clip either plays once and ends (<see cref="AnimationKind.Animation"/>), or it is a static
    /// pose that stays until something replaces it (<see cref="AnimationKind.Pose"/>).
    /// </summary>
    public enum AnimationKind
    {
        Animation,
        Pose
    }

    public class Animation
    {
        public string Id { get; private set; }

        /// <summary>Registered name. Only used for logging and for the list shown to the LLM.</summary>
        public string Name { get; set; }

        public string ParameterKey { get; set; }
        public int ParameterValue { get; set; }
        public float Duration { get; set; }
        public string LayeredAnimationName { get; set; }
        public string LayeredAnimationLayerName { get; set; }

        /// <summary>Name of the AnimatorController state that plays this clip, used to restart it for loops.</summary>
        public string AnimatorStateName { get; set; }

        /// <summary>Animator layer <see cref="AnimatorStateName"/> lives on ("Base Layer" for base layer clips).</summary>
        public string AnimatorLayerName { get; set; }

        /// <summary>Pose = holds in place. Animation = plays out and ends.</summary>
        public AnimationKind Kind { get; set; } = AnimationKind.Animation;

        /// <summary>How many times in a row to play the clip. Ignored for poses.</summary>
        public int Loops { get; set; } = 1;

        /// <summary>
        /// Extra time on top of the natural playback.
        /// For a pose this is the whole hold, and 0 (or less) means "hold until something else replaces it".
        /// </summary>
        public float HoldSeconds { get; set; }

        /// <summary>Name of the animation/pose to move to when this one finishes. Resolved by ModelController.</summary>
        public string NextAnimationName { get; set; }

        /// <summary>How many of the requested loops played so far. Managed by ModelController.</summary>
        public int LoopsPlayed { get; set; }

        public Animation(string parameterKey, int parameterValue, float duration, string layeredAnimationName = null, string layeredAnimationLayerName = null)
        {
            Id = Guid.NewGuid().ToString();
            ParameterKey = parameterKey;
            ParameterValue = parameterValue;
            Duration = duration;
            LayeredAnimationName = layeredAnimationName;
            LayeredAnimationLayerName = layeredAnimationLayerName;
        }

        public bool IsPose => Kind == AnimationKind.Pose;

        public bool IsLayered => !string.IsNullOrEmpty(LayeredAnimationName) && !string.IsNullOrEmpty(LayeredAnimationLayerName);

        /// <summary>Time for one play-through of the clip.</summary>
        public float SingleLoopDuration => Duration > 0f ? Duration : 1f;

        /// <summary>Time for the last play-through, including the trailing hold.</summary>
        public float LastLoopDuration
        {
            get
            {
                if (IsPose)
                {
                    // A pose without an explicit hold never expires on its own - that is what makes it a pose.
                    return HoldSeconds > 0f ? HoldSeconds : float.MaxValue;
                }

                return SingleLoopDuration + Math.Max(0f, HoldSeconds);
            }
        }

        /// <summary>
        /// How long this animation occupies the avatar before the queue moves on,
        /// counting every requested loop.
        /// </summary>
        public float EffectiveDuration
        {
            get
            {
                if (IsPose)
                {
                    return LastLoopDuration;
                }

                var loops = Loops > 1 ? Loops : 1;
                return SingleLoopDuration * (loops - 1) + LastLoopDuration;
            }
        }

        /// <summary>Copies every playback field so registered templates can be handed out safely.</summary>
        public Animation Clone()
        {
            return new Animation(ParameterKey, ParameterValue, Duration, LayeredAnimationName, LayeredAnimationLayerName)
            {
                Name = Name,
                AnimatorStateName = AnimatorStateName,
                AnimatorLayerName = AnimatorLayerName,
                Kind = Kind,
                Loops = Loops,
                HoldSeconds = HoldSeconds,
                NextAnimationName = NextAnimationName
            };
        }
    }
}
