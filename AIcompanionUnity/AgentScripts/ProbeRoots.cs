using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

public static class ProbeRoots
{
    public static string Main()
    {
        var sb = new StringBuilder();
        for (var s = 0; s < SceneManager.sceneCount; s++)
        {
            var sc = SceneManager.GetSceneAt(s);
            if (!sc.IsValid() || !sc.isLoaded) { continue; }
            sb.AppendLine("=== scene " + sc.name + " (handle=" + sc.handle + ") ===");
            var roots = new List<GameObject>();
            sc.GetRootGameObjects(roots);
            foreach (var r in roots)
            {
                sb.AppendLine("ROOT '" + r.name + "' instanceID=" + r.GetInstanceID()
                    + " activeSelf=" + r.activeSelf
                    + " childCount=" + r.transform.childCount
                    + " compCount=" + r.GetComponents<Component>().Length);
                foreach (var c in r.GetComponents<Component>())
                {
                    sb.AppendLine("    <" + c.GetType().FullName + ">");
                }
                DescribeChildren(sb, r.transform, 1, 0);
            }
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-roots-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }

    private static void DescribeChildren(StringBuilder sb, Transform t, int depth, int origDepth)
    {
        if (origDepth > 2) { return; }
        foreach (var c in t.GetComponents<Component>())
        {
            sb.AppendLine(new string(' ', depth * 4) + "<" + c.GetType().Name + ">");
        }
        foreach (Transform ch in t)
        {
            sb.AppendLine(new string(' ', depth * 4) + "+ " + ch.name
                + " (activeSelf=" + ch.gameObject.activeSelf + ")");
            DescribeChildren(sb, ch, depth + 1, origDepth + 1);
        }
    }
}
