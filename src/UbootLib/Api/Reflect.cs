using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace UbootLib.Api;

/// <summary>
/// Safe access to game members through the interop proxies: every call is guarded and
/// caches the reflection metadata, so mods never crash the process on a renamed member.
/// </summary>
public static class Reflect
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<(Type, string), FieldInfo> Fields = new();
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> Props = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo> Methods = new();

    private static Type TypeOf(object target) => target is Type t ? t : target?.GetType();

    public static object GetField(object target, string name)
    {
        try
        {
            var type = TypeOf(target);
            if (type == null) return null;
            var fi = Fields.GetOrAdd((type, name), k => k.Item1.GetField(k.Item2, Any));
            if (fi == null) return null;
            return fi.GetValue(target is Type ? null : target);
        }
        catch (Exception e)
        {
            Log($"GetField({name})", e);
            return null;
        }
    }

    public static bool TryGetField<T>(object target, string name, out T value)
    {
        value = default;
        var raw = GetField(target, name);
        if (raw is T typed) { value = typed; return true; }
        return false;
    }

    public static bool SetField(object target, string name, object value)
    {
        try
        {
            var type = TypeOf(target);
            if (type == null) return false;
            var fi = Fields.GetOrAdd((type, name), k => k.Item1.GetField(k.Item2, Any));
            if (fi == null) return false;
            fi.SetValue(target is Type ? null : target, value);
            return true;
        }
        catch (Exception e)
        {
            Log($"SetField({name})", e);
            return false;
        }
    }

    public static object GetProperty(object target, string name)
    {
        try
        {
            var type = TypeOf(target);
            if (type == null) return null;
            var pi = Props.GetOrAdd((type, name), k => k.Item1.GetProperty(k.Item2, Any));
            return pi?.GetValue(target is Type ? null : target);
        }
        catch (Exception e)
        {
            Log($"GetProperty({name})", e);
            return null;
        }
    }

    public static object Invoke(object target, string method, params object[] args)
    {
        try
        {
            var type = TypeOf(target);
            if (type == null) return null;
            var mi = Methods.GetOrAdd((type, method), k =>
                k.Item1.GetMethod(k.Item2, Any, null, CallingConventions.Any,
                    args?.Select(a => a?.GetType() ?? typeof(object)).ToArray() ?? Type.EmptyTypes, null)
                ?? k.Item1.GetMethod(k.Item2, Any));
            return mi?.Invoke(target is Type ? null : target, args);
        }
        catch (Exception e)
        {
            Log($"Invoke({method})", e);
            return null;
        }
    }

    public static object GetStatic(Type type, string name) => GetField(type, name);
    public static object CallStatic(Type type, string method, params object[] args) => Invoke(type, method, args);

    /// <summary>Reads a nested chain such as "uboat.propulsion.rpm" without null checks at each step.</summary>
    public static object Chain(object root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current == null) return null;
            current = GetField(current, part) ?? GetProperty(current, part);
        }
        return current;
    }

    private static void Log(string op, Exception e) =>
        UbootLibPlugin.KitLog?.LogDebug($"[api] {op} failed: {e.GetType().Name}: {e.Message}");

}
