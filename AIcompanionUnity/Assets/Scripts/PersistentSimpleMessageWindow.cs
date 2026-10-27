using UnityEngine;
using ChatdollKit.Dialog;

namespace AICompanion
{
    // SimpleMessageWindow but the last reply stays on screen.
    // AIAvatar wires SpeechController.OnSayEnd to CharacterMessageWindow.Hide(), and with
    // DummySpeechSynthesizer there is no audio to play, so the reply would vanish the instant
    // it appeared. KeepLastMessage turns that hide into a no-op for the character's window.
    public class PersistentSimpleMessageWindow : SimpleMessageWindow
    {
        public bool KeepLastMessage = true;

        public override void Hide()
        {
            if (KeepLastMessage)
            {
                return;
            }

            base.Hide();
        }
    }
}
