using System;
using System.Text;
using UnityEngine;

public static class ProbeBase
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var t = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (t == null) { return "NULL"; }
        var b = t;
        while (b != null)
        {
            sb.AppendLine(b.FullName + "  [" + b.Assembly.GetName().Name + "]");
            b = b.BaseType;
        }
        return sb.ToString();
    }
}
