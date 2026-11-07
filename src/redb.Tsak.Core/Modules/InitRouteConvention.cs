using System.Reflection;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// The <c>InitRoute.main</c> module convention: an exported type named <c>InitRoute</c> with one public static
/// <c>main</c> in one of two forms — <c>IRouteContext main(IRouteContext)</c> or
/// <c>Task&lt;IRouteContext&gt; main(IRouteContext)</c>. A <c>main</c> that fits neither form, or more than one
/// <c>main</c>, is an explicit error: skipping it silently made the module vanish without a trace. The
/// context type is matched by name (<c>IRouteContext</c> or <c>RouteContext</c>), as discovery always did —
/// module assemblies load into their own load contexts.
/// </summary>
internal static class InitRouteConvention
{
    public const string TypeName = "InitRoute";
    public const string MethodName = "main";
    public const string SyncForm = "public static IRouteContext main(IRouteContext context)";
    public const string AsyncForm = "public static Task<IRouteContext> main(IRouteContext context)";

    public enum Form
    {
        Unsupported,
        Sync,
        Async
    }

    public static Form Classify(MethodInfo main)
    {
        var parameters = main.GetParameters();
        if (parameters.Length != 1 || !IsContextType(parameters[0].ParameterType))
            return Form.Unsupported;

        var returnType = main.ReturnType;
        if (IsContextType(returnType))
            return Form.Sync;

        if (returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            && IsContextType(returnType.GetGenericArguments()[0]))
            return Form.Async;

        return Form.Unsupported;
    }

    /// <summary>
    /// The one <c>main</c> of an <c>InitRoute</c> type. Throws <see cref="InvalidOperationException"/> naming the
    /// type, the found signatures and the supported forms when a <c>main</c> fits neither form or there is more
    /// than one.
    /// </summary>
    public static MethodInfo Resolve(Type initRoute, IReadOnlyList<MethodInfo> mains)
    {
        var unsupported = mains.Where(m => Classify(m) == Form.Unsupported).ToList();
        if (unsupported.Count > 0)
            throw new InvalidOperationException(
                $"{initRoute.FullName} declares main with an unsupported signature: "
                + $"{string.Join("; ", unsupported.Select(Describe))}. Supported: '{SyncForm}' or '{AsyncForm}'.");

        if (mains.Count > 1)
            throw new InvalidOperationException(
                $"{initRoute.FullName} declares more than one main ({string.Join("; ", mains.Select(Describe))}); "
                + $"both forms at once or overloads are not supported. Keep one: '{SyncForm}' or '{AsyncForm}'.");

        return mains[0];
    }

    /// <summary>A readable signature, e.g. <c>Task&lt;IRouteContext&gt; main(IRouteContext)</c>.</summary>
    public static string Describe(MethodInfo main) =>
        $"{Label(main.ReturnType)} {main.Name}({string.Join(", ", main.GetParameters().Select(p => Label(p.ParameterType)))})";

    private static bool IsContextType(Type type) => type.Name is "IRouteContext" or "RouteContext";

    private static string Label(Type type)
    {
        if (type == typeof(void))
            return "void";
        if (!type.IsGenericType)
            return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Label))}>";
    }
}
