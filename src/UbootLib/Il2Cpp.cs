using System;

namespace UbootLib;

/// <summary>Small IL2CPP helpers used by the kit and by mods.</summary>
public static class Il2Cpp
{
    /// <summary>
    /// Resolves a game type by name from the interop assembly without triggering a
    /// full assembly scan (cheap enough to call during load).
    /// </summary>
    public static Type FindType(string fullName)
    {
        try
        {
            return Type.GetType(fullName + ", Assembly-CSharp", throwOnError: false)
                   ?? Type.GetType(fullName, throwOnError: false);
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[kit] FindType('{fullName}') failed: {e.Message}");
            return null;
        }
    }
}
