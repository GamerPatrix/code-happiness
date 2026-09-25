using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ChatdollKit.UI;
using ChatdollKit.Dialog;

public static class SubmitTest
{
    public static string Main()
    {
        var sb = new StringBuilder();
        sb.AppendLine("isPlaying=" + Application.isPlaying);

        var host = GameObject.Find("/AICompanion/Canvas/MessageInput");
        if (host == null) { return "FAIL: /AICompanion/Canvas/MessageInput not found"; }

        var mi = host.GetComponent<MessageInput>();
        if (mi == null) { return "FAIL: host has no MessageInput"; }
        sb.AppendLine("MessageInput=" + host.name + " activeInHierarchy=" + host.activeInHierarchy);

        var so = new UnityEditor.SerializedObject(mi);
        var field = so.FindProperty("messageInput").objectReferenceValue as InputField;
        if (field == null) { return sb + "FAIL: messageInput ref null"; }

        field.text = "Hello! Please introduce yourself in one short sentence.";
        sb.AppendLine("typed into field, len=" + field.text.Length);

        mi.OnSubmitMessageInput();
        sb.AppendLine("OnSubmitMessageInput called. field now='" + field.text + "'");

        var dialog = GameObject.Find("/AICompanion").GetComponent<DialogProcessor>();
        if (dialog != null) { sb.AppendLine("DialogProcessor status=" + dialog.Status); }

        return sb.ToString();
    }
}
