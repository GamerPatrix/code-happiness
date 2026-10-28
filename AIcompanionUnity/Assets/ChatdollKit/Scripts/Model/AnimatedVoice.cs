using System.Collections.Generic;

namespace ChatdollKit.Model
{
    // Animation with voice and face expression
    public class AnimatedVoice
    {
        public List<Voice> Voices { get; set; }
        public List<Animation> Animations { get; set; }
        public List<FaceExpression> Faces { get; set; }
        // Idling mode to switch to before this frame plays, set by a [idle:mode] tag. Null when the
        // frame does not ask for a change.
        public string IdlingModeChange { get; set; }
        // Sitting point to move to before this frame plays, set by a [sit:"Name"] or [stand] tag.
        // Null when the frame does not ask for a move; empty string means "get up and stand".
        public string PointChange { get; set; }

        public AnimatedVoice(List<Voice> voices = null, List<Animation> animations = null, List<FaceExpression> faces = null)
        {
            Voices = voices ?? new List<Voice>();
            Animations = animations ?? new List<Animation>();
            Faces = faces ?? new List<FaceExpression>();
        }

        public void AddVoice(string text, float preGap = 0.0f, float postGap = 0.0f, TTSConfiguration ttsConfig = null, bool useCache = true, string description = null)
        {
            Voices.Add(new Voice(text, preGap, postGap, ttsConfig, useCache, description));
        }

        public void AddAnimation(string paramKey, int paramValue, float duration = 0.0f, string layeredAnimation = null, string layeredAnimationLayer = null)
        {
            Animations.Add(new Animation(paramKey, paramValue, duration, layeredAnimation, layeredAnimationLayer));
        }

        // Adds a fully described animation (kind, loops, hold, next) built elsewhere, e.g. by
        // ModelController from a [anim:...] / [pose:...] tag.
        public void AddAnimation(Animation animation)
        {
            if (animation != null)
            {
                Animations.Add(animation);
            }
        }

        public void AddFace(string name, float duration = 0.0f, string description = null)
        {
            Faces.Add(new FaceExpression(name, duration, description));
        }
    }
}
