using System;
using System.Text;
using UnityEngine;
using ChatdollKit.Dialog;
using ChatdollKit.UI;
using ChatdollKit.LLM;
using ChatdollKit.LLM.ChatGPT;

public static class CheckReply2
{
    public static string Main()
    {
        var sb = new StringBuilder();
        sb.AppendLine("time=" + DateTime.Now.ToString("HH:mm:ss") + " isPlaying=" + Application.isPlaying);

        var companion = GameObject.Find("/AICompanion");
        if (companion == null) { return "FAIL: no /AICompanion"; }

        var dialog = companion.GetComponent<DialogProcessor>();
        sb.AppendLine("status=" + dialog.Status);

        var avatar = companion.GetComponent<ChatdollKit.AIAvatar>();
        sb.AppendLine("avatarMode=" + avatar.Mode);
        sb.AppendLine("avatar.SpeechListener=" + (avatar.SpeechListener == null ? "NULL" : avatar.SpeechListener.GetType().Name));
        sb.AppendLine("avatar.MicrophoneManager=" + (avatar.MicrophoneManager == null ? "NULL" : avatar.MicrophoneManager.GetType().Name));

        var llm = companion.GetComponent<ChatGPTService>();
        if (llm != null)
        {
            sb.AppendLine("ChatGPTService enabled=" + llm.enabled + " isEnabled=" + llm.IsEnabled);
            foreach (var f in typeof(ChatGPTService).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name.ToLower().Contains("last") || f.Name.ToLower().Contains("response") || f.Name.ToLower().Contains("error"))
                {
                    object v = null;
                    try { v = f.GetValue(llm); } catch { }
                    sb.AppendLine("  " + f.Name + " = " + (v == null ? "null" : v.ToString()));
                }
            }
        }

        foreach (var w in companion.GetComponentsInChildren<SimpleMessageWindow>(true))
        {
            var txt = w.GetComponentInChildren<UnityEngine.UI.Text>(true);
            sb.AppendLine("window " + w.gameObject.name + " active=" + w.gameObject.activeInHierarchy
                + " text='" + (txt == null ? "<no Text>" : txt.text) + "'");
        }
        return sb.ToString();
    }
}
