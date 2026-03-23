namespace CsSsg.Src.Exceptions;


public class UnexpectedEnumValueException(string message) : InvalidOperationException(message)
{
    public static UnexpectedEnumValueException Create<TEnum>(TEnum? value) where TEnum : struct, Enum
        => new($"type: {typeof(TEnum).Name}, value: {value.ToString() ?? "(null)"}");

    public static UnexpectedEnumValueException Create<TEnum>(TEnum value) where TEnum : struct, Enum
        => new($"type: {typeof(TEnum).Name}, value: {value}");

    public static void VerifyOrThrow<TEnum>(TEnum? value) where TEnum : struct, Enum
    {
        if (value.HasValue && Enum.IsDefined(value.Value)) return;
        throw Create(value);
    }

    public static void VerifyOrThrow<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (Enum.IsDefined(value)) return;
        throw Create(value);
    }
}
