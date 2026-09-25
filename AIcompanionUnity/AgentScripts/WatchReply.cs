using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ChatdollKit;
using ChatdollKit.Dialog;
using ChatdollKit.UI;

public static class WatchReply
{
    // Lives on the Canvas (always active) so it keeps sampling while the
    // message window itself is toggled visible/hidden by the kit.
    private class Watcher : MonoBehaviour
    {
        public Text target;
        public string longest = "";
        public string lastNonEmpty = "";

        private void Update()
        {
            if (target == null) { return; }
            var t = target.text;
            if (string.IsNullOrEmpty(t)) { return; }
            lastNonEmpty = t;
            if (t.Length > longest.Length) { longest = t; }
        }
    }

    public static string Main()
    {
        var sb = new StringBuilder();
        if (!Application.isPlaying) { return "FAIL: not playing"; }
        Application.runInBackground = true;

        var companion = GameObject.Find("/AICompanion");
        var dialog = companion.GetComponent<DialogProcessor>();
        var avatar = companion.GetComponent<AIAvatar>();

        var canvas = companion.GetComponentInChildren<Canvas>(true);
        if (canvas == null) { return "FAIL: no Canvas"; }

        var windows = companion.GetComponentsInChildren<SimpleMessageWindow>(true);
        SimpleMessageWindow character = null;
        foreach (var wnd in windows)
        {
            if (wnd.gameObject.name.ToLower().Contains("character")) { character = wnd; }
        }
        if (character == null) { return sb + "FAIL: no CharacterMessageWindow among " + windows.Length; }

        var text = character.GetComponentInChildren<Text>(true);
        if (text == null) { return sb + "FAIL: no Text under " + character.gameObject.name; }

        var w = canvas.GetComponent<Watcher>();
        if (w == null) { w = canvas.gameObject.AddComponent<Watcher>(); }
        w.target = text;
        w.longest = "";
        w.lastNonEmpty = "";

        var mi = companion.GetComponentInChildren<MessageInput>(true);
        if (mi == null) { return sb + "FAIL: no MessageInput"; }
        var field = mi.GetComponentInChildren<InputField>(true);
        if (field == null) { return sb + "FAIL: no InputField"; }

        field.text = "What is the capital of Japan? Answer in one short sentence.";
        mi.OnSubmitMessageInput();
        sb.AppendLine("submitted frame=" + Time.frameCount + " status=" + dialog.Status);

        w.StartCoroutine(Poll(w, dialog, avatar, w));
        sb.AppendLine("polling coroutine started on " + w.gameObject.name);
        return sb.ToString();
    }

    private static System.Collections.IEnumerator Poll(MonoBehaviour host, DialogProcessor dialog, AIAvatar avatar, Watcher w)
    {
        var frameBudget = 12000;
        while (frameBudget > 0)
        {
            frameBudget--;
            if (dialog.Status == DialogProcessor.DialogStatus.Idling
                || dialog.Status == DialogProcessor.DialogStatus.Error)
            {
                break;
            }
            yield return null;
        }
        yield return new WaitForSeconds(1.0f);

        var sb = new StringBuilder();
        sb.AppendLine("finalStatus=" + dialog.Status);
        sb.AppendLine("avatarMode=" + avatar.Mode);
        sb.AppendLine("framesUsed=" + (12000 - frameBudget));
        sb.AppendLine("lastNonEmpty='" + w.lastNonEmpty + "'");
        sb.AppendLine("LONGEST='" + w.longest + "'");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(Application.dataPath, "..", "AgentScripts", "watch-out.txt"),
            sb.ToString());
    }
}
