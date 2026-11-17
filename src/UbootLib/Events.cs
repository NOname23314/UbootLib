using System;
using System.Collections.Generic;

namespace UbootLib;

/// <summary>Game events exposed to mods (main thread only).</summary>
public sealed class Events
{
    public event Action<string, int> SceneLoaded;
    public event Action GameQuitting;

    internal void RaiseSceneLoaded(string sceneName, int buildIndex)
    {
        var handlers = SceneLoaded;
        if (handlers == null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((Action<string, int>)d)(sceneName, buildIndex); }
            catch (Exception e) { ModLoader.ReportHandlerFailure("SceneLoaded", e); }
        }
    }

    internal void RaiseQuitting()
    {
        var handlers = GameQuitting;
        if (handlers == null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception e) { ModLoader.ReportHandlerFailure("GameQuitting", e); }
        }
    }
}
