using System;
using System.Text;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

public static class ProbeApi4
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var t = typeof(PlayerSettings);

        foreach (var n in new[] { "activeInputHandler", "insecureHttpOption", "insecureHttpScopes" })
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (p == null) { sb.AppendLine(n + ": NO STATIC PROPERTY"); continue; }
            sb.AppendLine(n + " : " + p.PropertyType.FullName + " canWrite=" + p.CanWrite
                + " value=" + (p.CanRead ? System.Convert.ToString(p.GetValue(null), System.Globalization.CultureInfo.InvariantCulture) : "?"));
        }

        foreach (var n in new[] { "ActiveInputHandler", "InsecureHttpOption" })
        {
            var et = Type.GetType("UnityEngine." + n + ", UnityEngine.CoreModule");
            if (et == null) { et = Type.GetType("UnityEditor." + n + ", UnityEditor.CoreModule"); }
            if (et == null) { sb.AppendLine(n + ": TYPE NOT FOUND"); continue; }
            sb.AppendLine(n + " = " + string.Join(", ", Enum.GetValues(et).Cast<object>()
                .Select(v => v + "=" + Convert.ToInt32(v))));
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-api4-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }
}
