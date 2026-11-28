using System;
using System.Globalization;
using System.Text.Json;

namespace PaperTodo;

// All module declarations share this one type. The cold catalog scan initializes every
// partial declaration once, including modules that have not opened a window yet.
internal static partial class InjectionParameters { }

internal abstract class InjectionParameter
{
    protected InjectionParameter(string id, string title, string description, string unit,
        string application, string? defaultDescription,
        double? recommendedMinimum, double? recommendedMaximum)
    {
        Id = id;
        Title = title;
        Description = description;
        Unit = unit;
        Application = application;
        DefaultDescription = defaultDescription;
        RecommendedMinimum = recommendedMinimum;
        RecommendedMaximum = recommendedMaximum;
    }

    internal string Id { get; }
    internal string Title { get; }
    internal string Description { get; }
    internal string Unit { get; }
    internal string Application { get; }
    internal abstract string Type { get; }
    internal abstract JsonElement? DefaultValue { get; }
    internal string? DefaultDescription { get; }
    internal double? RecommendedMinimum { get; }
    internal double? RecommendedMaximum { get; }
    internal bool HasOverride { get; private protected set; }
    internal JsonElement? BoundOverride { get; private set; }
    internal JsonElement? AppliedOverride { get; private set; }
    internal JsonElement? AcceptedOverride { get; private set; }

    internal abstract object Parse(JsonElement value);
    internal abstract void Bind(object? value, bool hasOverride);
    internal abstract JsonElement? NumericSuggestion(double value);
    internal void BindOverride(object? value, JsonElement? rawValue)
    {
        Bind(value, rawValue.HasValue);
        BoundOverride = rawValue?.Clone();
    }
    internal void MarkApplied(JsonElement? value) => AppliedOverride = value?.Clone();
    internal void MarkAccepted(JsonElement? value) => AcceptedOverride = value?.Clone();
}

/// <summary>
/// A typed slot bound only at startup or Apply. Fixed values read one cached field;
/// Resolve keeps the caller's original live calculation when no override is present.
/// </summary>
internal sealed class InjectionParameter<T> : InjectionParameter
{
    private readonly T _defaultValue;
    private readonly bool _hasDefault;
    private JsonElement? _defaultJson;
    private T _value;
    private static readonly string ParameterType = GetParameterType();

    internal InjectionParameter(string id, T defaultValue, string title, string description,
        string unit = "", string application = "live",
        double? recommendedMinimum = null, double? recommendedMaximum = null)
        : this(id, defaultValue, title, description, unit, application,
            true, null, recommendedMinimum, recommendedMaximum) { }

    private InjectionParameter(string id, T defaultValue, string title, string description,
        string unit, string application, bool hasDefault, string? defaultDescription,
        double? recommendedMinimum, double? recommendedMaximum)
        : base(id, title, description, unit, application, defaultDescription,
            recommendedMinimum, recommendedMaximum)
    {
        _defaultValue = defaultValue;
        _hasDefault = hasDefault;
        _value = defaultValue;
    }

    internal static InjectionParameter<T> Dynamic(string id, string title, string description,
        string unit = "", string? defaultDescription = null, string application = "live",
        double? recommendedMinimum = null, double? recommendedMaximum = null) =>
        new(id, default!, title, description, unit, application, false, defaultDescription,
            recommendedMinimum, recommendedMaximum);

    internal T Value => _value;
    internal T Resolve(T original) => HasOverride ? _value : original;
    internal override string Type => ParameterType;
    // Default JSON is inspection metadata. Ordinary startup and consumer reads do not need it.
    internal override JsonElement? DefaultValue => _hasDefault
        ? _defaultJson ??= JsonSerializer.SerializeToElement(_defaultValue) : null;

    internal override object Parse(JsonElement value)
    {
        var parsed = value.Deserialize<T>();
        if (parsed is null || parsed is double number && !double.IsFinite(number) ||
            parsed is float single && !float.IsFinite(single))
            throw new JsonException($"'{Id}' requires a representable {ParameterType} value.");
        return parsed;
    }

    internal override void Bind(object? value, bool hasOverride)
    {
        _value = hasOverride ? (T)value! : _defaultValue;
        HasOverride = hasOverride;
    }

    internal override JsonElement? NumericSuggestion(double value)
    {
        if (ParameterType is not ("integer" or "number")) return null;
        try
        {
            var converted = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToElement(converted);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    private static string GetParameterType() => System.Type.GetTypeCode(typeof(T)) switch
    {
        TypeCode.Boolean => "boolean",
        TypeCode.String => "string",
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "integer",
        TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
        _ => throw new NotSupportedException($"Injection value type '{typeof(T).Name}' is not supported.")
    };
}
