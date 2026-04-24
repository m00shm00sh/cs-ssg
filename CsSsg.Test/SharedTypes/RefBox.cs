namespace CsSsg.Test.SharedTypes;

/// <summary>
/// Boxes a value so lambda capture captures an effective reference instead of value.
/// </summary>
/// <param name="initialValue">initial value</param>
/// <typeparam name="T"></typeparam>
internal class RefBox<T>(T initialValue)
{
    public T Value = initialValue;
}

internal static class RefBox
{
    internal static RefBox<T> Create<T>(T initialValue) => new(initialValue);
}