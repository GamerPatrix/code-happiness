using System.Reflection;
using System.Threading;
using ChatdollKit.Model;
using CodeHappiness.Avatar;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// TEMPORARY diagnostic. Removed after the idle wander loop is verified.
/// Runs entirely on the main thread so UniTask continuations behave exactly as they do in a build.
/// </summary>
public class AgentWanderProbe : MonoBehaviour
{
    private const string ResultPath = @"C:\Users\patri\UnityProjects\code-happiness\AIcompanionUnity\AgentScripts\probe-result.txt";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        new GameObject("AgentWanderProbe").AddComponent<AgentWanderProbe>();
    }

    private AvatarPointLibrary lib;
    private ModelController controller;
    private System.Type libType;
    private float startedAt;
    private float lastDump;
    private string previous;
    private int moves;
    private bool delayChecked;

    private void Awake()
    {
        lib = FindFirstObjectByType<AvatarPointLibrary>();
        controller = FindFirstObjectByType<ModelController>();
        libType = typeof(AvatarPointLibrary);
        startedAt = Time.realtimeSinceStartup;
        previous = lib != null && lib.CurrentPoint != null ? lib.CurrentPoint.PointName : "<null>";
        System.IO.File.WriteAllText(ResultPath, $"probe booted at realtime={startedAt:F1} start={previous}\n");
        ProbeDelay().Forget();
    }

    private async UniTaskVoid ProbeDelay()
    {
        await UniTask.Delay(3000);
        delayChecked = true;
        Write($"UniTask.Delay(3000) resumed at {Time.realtimeSinceStartup:F1}s (expected ~3s)");
    }

    private void Update()
    {
        if (lib == null || controller == null || Time.realtimeSinceStartup - startedAt < 5f)
        {
            return;
        }

        var current = lib.CurrentPoint != null ? lib.CurrentPoint.PointName : "<null>";
        if (current != previous)
        {
            moves++;
            var p = controller.transform.position;
            Write($"MOVE #{moves} {previous} -> {current} pos=({p.x:F2},{p.y:F2},{p.z:F2}) held={controller.IsPositionHeld} anim=\"{controller.CurrentAnimationName}\"");
            previous = current;
        }

        if (Time.realtimeSinceStartup - lastDump >= 15f)
        {
            lastDump = Time.realtimeSinceStartup;
            var lastWanderAt = (float)libType.GetField("lastWanderAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lib);
            var token = libType.GetField("wanderTokenSource", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lib) as CancellationTokenSource;
            var idleFor = (float)(System.DateTime.UtcNow - controller.IdlingModeStartAt).TotalSeconds;
            var should = libType.GetMethod("ShouldWander", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lib, null);
            Write($"tick should={should} current={current} lastWanderAt={lastWanderAt:F1} token={(token == null ? "null" : (token.IsCancellationRequested ? "CANCELLED" : "alive"))} idleFor={idleFor:F1}s anim=\"{controller.CurrentAnimationName}\" pose={controller.IsHoldingPose()}");
        }
    }

    private void Write(string message)
    {
        System.IO.File.AppendAllText(ResultPath, $"[{Time.realtimeSinceStartup - startedAt:F0}s] {message}\n");
    }
}
