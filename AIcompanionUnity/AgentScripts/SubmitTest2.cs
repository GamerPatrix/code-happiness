using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ChatdollKit.Dialog;
using ChatdollKit.UI;

public static class SubmitTest2
{
    public static string Main()
    {
        var sb = new StringBuilder();
        if (!Application.isPlaying) { return "FAIL: not playing"; }

        // An unfocused Editor throttles/pauses the player loop, which freezes every
        // UniTask.Delay in the dialog pipeline and leaves DialogProcessor stuck.
        Application.runInBackground = true;
        sb.AppendLine("runInBackground=" + Application.runInBackground
            + " timeScale=" + Time.timeScale
            + " frameCount=" + Time.frameCount
            + " time=" + Time.time);

        var companion = GameObject.Find("/AICompanion");
        var dialog = companion.GetComponent<DialogProcessor>();
        var mi = companion.GetComponentInChildren<MessageInput>(true);
        if (mi == null) { return sb + "FAIL: no MessageInput"; }

        var field = mi.GetComponentInChildren<InputField>(true);
        if (field == null) { return sb + "FAIL: no InputField"; }

        field.text = "Hello! What can you tell me about yourself?";
        mi.OnSubmitMessageInput();
        sb.AppendLine("submitted. field='" + field.text + "' status=" + dialog.Status);
        return sb.ToString();
    }
}
