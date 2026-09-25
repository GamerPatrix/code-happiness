using System;
using System.Text;
using UnityEngine;
using ChatdollKit.Dialog;
using ChatdollKit.UI;

public static class CheckReply
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var companion = GameObject.Find("/AICompanion");
        if (companion == null) { return "FAIL: no /AICompanion"; }

        var dialog = companion.GetComponent<DialogProcessor>();
        sb.AppendLine("status=" + dialog.Status);

        var avatar = companion.GetComponent<ChatdollKit.AIAvatar>();
        sb.AppendLine("avatarMode=" + avatar.Mode);

        foreach (var w in companion.GetComponentsInChildren<SimpleMessageWindow>(true))
        {
            var txt = w.GetComponentInChildren<UnityEngine.UI.Text>(true);
            sb.AppendLine("window " + w.gameObject.name + " active=" + w.gameObject.activeInHierarchy
                + " text='" + (txt == null ? "<no Text>" : txt.text) + "'");
        }

        var speech = companion.GetComponent<ChatdollKit.Model.SpeechController>();
        if (speech != null) { sb.AppendLine("speechController found"); }

        return sb.ToString();
    }
}
