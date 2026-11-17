using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UbootLib.Api;

/// <summary>
/// Typed façade over the live game state. Every property is defensive: if the game
/// changes shape after a patch, the getter returns null/false instead of throwing.
/// </summary>
public static class Hub
{
    public static string GameVersion
    {
        get { try { return Application.version; } catch { return "?"; } }
    }

    public static string SceneName
    {
        get { try { return SceneManager.GetActiveScene().name; } catch { return "?"; } }
    }

    /// <summary>Network manager instance, or null when not in a lobby.</summary>
    public static W_NetworkManager Net
    {
        get { try { return W_NetworkManager.instance; } catch { return null; } }
    }

    public static bool InLobby => Net != null;

    public static bool IsServer
    {
        get { try { return W_NetworkManager.IsServer; } catch { return false; } }
    }

    public static IReadOnlyList<W_Player> Players
    {
        get
        {
            try
            {
                var mgr = Net;
                if (mgr?.clients == null) return Array.Empty<W_Player>();
                return mgr.clients.ToArray();
            }
            catch { return Array.Empty<W_Player>(); }
        }
    }

    public static ulong LocalSteamId
    {
        get
        {
            try { return Steamworks.SteamUser.GetSteamID().m_SteamID; }
            catch { return 0; }
        }
    }

    public static W_Player LocalPlayer
    {
        get
        {
            try
            {
                var id = LocalSteamId;
                return Players.FirstOrDefault(p => p != null && p.id.m_SteamID == id);
            }
            catch { return null; }
        }
    }

    public static IReadOnlyList<W_Uboat> Uboats
    {
        get
        {
            try { return UnityEngine.Object.FindObjectsOfType<W_Uboat>() ?? Array.Empty<W_Uboat>(); }
            catch { return Array.Empty<W_Uboat>(); }
        }
    }

    public static IReadOnlyList<WolfpackPawn> Pawns
    {
        get
        {
            try { return UnityEngine.Object.FindObjectsOfType<WolfpackPawn>() ?? Array.Empty<WolfpackPawn>(); }
            catch { return Array.Empty<WolfpackPawn>(); }
        }
    }

    /// <summary>One-line state digest, handy for logs and HUD.</summary>
    public static string Summary()
    {
        try
        {
            var role = InLobby ? (IsServer ? "host" : "client") : "menu";
            return $"scene={SceneName} role={role} players={Players.Count} uboats={Uboats.Count} pawns={Pawns.Count}";
        }
        catch (Exception e)
        {
            return $"summary failed: {e.Message}";
        }
    }

    /// <summary>Resolves a game type and returns its interop proxy, e.g. Hub.Type("W_Uboat").</summary>
    public static Type Type(string nameOrFullName) => Catalog.Resolve(nameOrFullName);

    /// <summary>Searches the catalog, e.g. Hub.Search("Torpedo").</summary>
    public static IEnumerable<GameTypeInfo> Search(string fragment, int limit = 40) => Catalog.Search(fragment, limit);
}
