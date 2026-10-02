using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// Calls every member of the <c>System.IO.Abstractions</c> interfaces an object implements,
/// each with default arguments, and reports what each call threw.
/// </summary>
/// <remarks>
/// Each call is emitted rather than made through <see cref="MethodBase.Invoke(object, object[])"/>,
/// which cannot pass a span: many members of <c>IPath</c>, <c>IFile</c> and
/// <c>IRandomAccess</c> take one. A returned task or sequence is run to its end, and anything
/// returned that holds a resource is disposed, so a member that fails only once it is used is
/// reported, and the sweep leaves nothing open behind it.
/// </remarks>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2070:UnrecognizedReflectionPattern",
    Justification = "The subject is whatever the adapters implement, which cannot be named statically. The suite is not trimmed.")]
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2075:UnrecognizedReflectionPattern",
    Justification = "The subject is whatever the adapters implement, which cannot be named statically. The suite is not trimmed.")]
[UnconditionalSuppressMessage(
    "AOT",
    "IL3050:RequiresDynamicCode",
    Justification = "The calls are emitted at run time. The suite is not compiled ahead of time.")]
internal static class MemberSweep
{
    private const string Namespace = "System.IO.Abstractions";

    /// <summary>
    /// Calls every member of each target, in a fixed order, and returns what each threw, or
    /// null where it returned, keyed by the target's label, the interface and the signature.
    /// </summary>
    public static SortedDictionary<string, Exception?> Run(IEnumerable<(string Label, object Target)> targets)
    {
        List<(string Key, object Target, MethodInfo Method)> members = [];
        foreach ((string label, object target) in targets)
        {
            foreach (Type contract in target.GetType().GetInterfaces().Where(type => type.Namespace == Namespace))
            {
                foreach (MethodInfo method in contract.GetMethods())
                {
                    Assert.False(method.ContainsGenericParameters, $"{contract.Name}.{method.Name} is generic, which the sweep cannot call.");
                    members.Add(($"{label} {contract.Name}.{Signature(method)}", target, method));
                }
            }
        }

        SortedDictionary<string, Exception?> outcomes = new(StringComparer.Ordinal);
        foreach ((string key, object target, MethodInfo method) in members.OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            Assert.True(outcomes.TryAdd(key, Call(Caller(method), target)), $"Two members are both named {key}.");
        }

        return outcomes;
    }

    private static Exception? Call(Func<object, object?> caller, object target)
    {
        try
        {
            Settle(caller(target));
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>Runs a returned task or sequence to its end, then disposes what was returned.</summary>
    private static void Settle(object? result)
    {
        try
        {
            switch (result)
            {
                case Task task:
                    task.GetAwaiter().GetResult();
                    break;
                case IAsyncEnumerable<string> lines:
                    DrainAsync(lines).AsTask().GetAwaiter().GetResult();
                    break;
                case string:
                    break;
                case IEnumerable sequence:
                    foreach (object? item in sequence)
                    {
                        (item as IDisposable)?.Dispose();
                    }

                    break;
                case not null when result.GetType().GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes) is { } asTask:
                    ((Task)asTask.Invoke(result, null)!).GetAwaiter().GetResult();
                    break;
            }
        }
        finally
        {
            (result as IDisposable)?.Dispose();
        }
    }

    private static async ValueTask DrainAsync(IAsyncEnumerable<string> lines)
    {
        await foreach (string _ in lines)
        {
        }
    }

    /// <summary>
    /// A call to <paramref name="method"/> on a target passed as an object, with the default
    /// of each parameter's type, returning the result boxed, or null for one that cannot be.
    /// </summary>
    private static Func<object, object?> Caller(MethodInfo method)
    {
        DynamicMethod call = new(method.Name, typeof(object), [typeof(object)], typeof(MemberSweep).Module, skipVisibility: true);
        ILGenerator il = call.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, method.DeclaringType!);
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            Type type = parameter.ParameterType;
            if (type.IsByRef)
            {
                il.Emit(OpCodes.Ldloca, il.DeclareLocal(type.GetElementType()!));
            }
            else if (type.IsValueType)
            {
                il.Emit(OpCodes.Ldloc, il.DeclareLocal(type));
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
        }

        il.Emit(OpCodes.Callvirt, method);
        Type returned = method.ReturnType;
        if (returned == typeof(void))
        {
            il.Emit(OpCodes.Ldnull);
        }
        else if (returned.IsByRefLike)
        {
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
        }
        else if (returned.IsValueType)
        {
            il.Emit(OpCodes.Box, returned);
        }

        il.Emit(OpCodes.Ret);
        return call.CreateDelegate<Func<object, object?>>();
    }

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)))})";

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return "ref " + TypeName(type.GetElementType()!);
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}
