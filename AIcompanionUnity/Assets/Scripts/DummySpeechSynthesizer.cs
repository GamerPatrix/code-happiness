using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using ChatdollKit.SpeechSynthesizer;

namespace AICompanion
{
    // Text-only companion: no TTS service is configured, so this returns a null clip.
    // SpeechController.Say skips playback when the clip is null, which keeps the
    // dialog loop, message window and facial performance working without a voice.
    // Without an enabled ISpeechSynthesizer, SpeechController.SpeechSynthesizerFunc
    // stays null and Say() throws NullReferenceException on every response.
    public class DummySpeechSynthesizer : MonoBehaviour, ISpeechSynthesizer
    {
        public bool IsEnabled { get; set; } = true;

        public UniTask<AudioClip> GetAudioClipAsync(string text, Dictionary<string, object> parameters, CancellationToken token = default)
        {
            return UniTask.FromResult<AudioClip>(null);
        }
    }
}
