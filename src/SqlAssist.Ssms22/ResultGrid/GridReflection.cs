using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// SSMS 結果格線的反射存取，方法一律先編成委派再呼叫。
/// </summary>
/// <remarks>
/// 全程反射、不新增組件參照，理由與當初的可行性探測相同：
/// <c>Microsoft.SqlServer.GridControl</c> 與 <c>SQLEditors</c> 是 SSMS 的內部
/// 組件，把建置綁在上面等於每次 SSMS 改版都可能編不過。反射失敗是這一輪不做，
/// 建置失敗是整個擴充裝不起來。
///
/// 但反射的呼叫成本在這裡是真的會痛：實測的結果有 178 欄，選滿 1000 列就是
/// 17.8 萬次 <c>MethodInfo.Invoke</c>——每一次都要配置引數陣列、裝箱、
/// 走一遍存取檢查。所以每一個方法只解析一次，編成強型別委派之後重複使用，
/// 並依「型別＋方法名」快取，同一個 session 裡按第二次不必再編一遍。
///
/// 委派而不是 <c>MethodInfo</c> 快取：後者省掉的只有查找，
/// 每次呼叫仍然要包引數陣列，而那正是這裡最貴的一段。
/// </remarks>
internal static class GridReflection
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly object Gate = new();

    private static readonly Dictionary<(Type Type, string Name), Delegate?> Cache = new();

    /// <summary>讀一個屬性；沒有這個屬性時回傳 <c>null</c>。</summary>
    public static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, Any)?.GetValue(instance);

    /// <summary>讀一個屬性並轉成 <typeparamref name="T"/>；型別對不上時回傳 <c>null</c>。</summary>
    public static T? Property<T>(object instance, string name)
        where T : struct
    {
        return Property(instance, name) is T value ? value : null;
    }

    /// <summary>綁一個 <c>(long 列, int 欄)</c> 取值的方法。</summary>
    public static Func<long, int, object?>? BindCell(object instance, string name)
    {
        var open = Resolve<Func<object, long, int, object?>>(
            instance.GetType(),
            name,
            (type, method) => BuildCell<object>(type, method, boxResult: true));

        return open is null ? null : (row, column) => open(instance, row, column);
    }

    /// <summary>綁一個 <c>(long 列, int 欄)</c> 回傳 <c>bool</c> 的方法。</summary>
    /// <remarks>
    /// 與 <see cref="BindCell"/> 分開只為了不要每一格都裝箱一個 <c>bool</c>：
    /// 這條路徑跟取值那條一樣，每一格都會走一次。
    /// </remarks>
    public static Func<long, int, bool>? BindCellFlag(object instance, string name)
    {
        var open = Resolve<Func<object, long, int, bool>>(
            instance.GetType(),
            name,
            (type, method) => BuildCell<bool>(type, method, boxResult: false));

        return open is null ? null : (row, column) => open(instance, row, column);
    }

    /// <summary>綁一個吃 <c>int</c> 的方法。</summary>
    public static Func<int, object?>? BindByIndex(object instance, string name)
    {
        var open = Resolve<Func<object, int, object?>>(
            instance.GetType(),
            name,
            BuildByIndex);

        return open is null ? null : index => open(instance, index);
    }

    private static TDelegate? Resolve<TDelegate>(
        Type type,
        string name,
        Func<Type, MethodInfo, Delegate?> build)
        where TDelegate : class
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((type, name), out var cached))
            {
                return cached as TDelegate;
            }

            Delegate? built = null;

            // 找不到方法、簽章對不上、編譯不出來，三種都記成「這個型別沒有這一個」，
            // 下一次不必再試一遍。SSMS 改版拿掉某個方法時，這條路徑每按一次命令
            // 都會走到，而重複解析失敗的成本比解析成功還高。
            try
            {
                var method = type.GetMethod(name, Any);
                built = method is null ? null : build(type, method);
            }
            catch (Exception)
            {
                // 多載讓 GetMethod 擲出 AmbiguousMatchException 也算「沒有這一個」。
                built = null;
            }

            Cache[(type, name)] = built;
            return built as TDelegate;
        }
    }

    private static Delegate? BuildCell<TResult>(Type type, MethodInfo method, bool boxResult)
    {
        var parameters = method.GetParameters();

        if (parameters.Length != 2
            || parameters[0].ParameterType != typeof(long)
            || parameters[1].ParameterType != typeof(int))
        {
            return null;
        }

        var instance = Expression.Parameter(typeof(object), "instance");
        var row = Expression.Parameter(typeof(long), "row");
        var column = Expression.Parameter(typeof(int), "column");

        Expression body = Expression.Call(Expression.Convert(instance, type), method, row, column);

        if (boxResult)
        {
            body = Expression.Convert(body, typeof(object));
        }
        else if (method.ReturnType != typeof(TResult))
        {
            return null;
        }

        return Expression.Lambda<Func<object, long, int, TResult>>(body, instance, row, column).Compile();
    }

    private static Delegate? BuildByIndex(Type type, MethodInfo method)
    {
        var parameters = method.GetParameters();

        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(int))
        {
            return null;
        }

        var instance = Expression.Parameter(typeof(object), "instance");
        var index = Expression.Parameter(typeof(int), "index");

        var body = Expression.Convert(
            Expression.Call(Expression.Convert(instance, type), method, index),
            typeof(object));

        return Expression.Lambda<Func<object, int, object?>>(body, instance, index).Compile();
    }
}
