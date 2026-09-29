using System.Globalization;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

internal static class WorldSettingsValueValidator
{
    public static void ValidateProfile(IReadOnlyDictionary<string, string> values)
    {
        foreach (var definition in WorldSettingsService.ProfileDefinitions)
        {
            if (!values.TryGetValue(definition.Key, out var raw))
                throw new InvalidOperationException($"profile 缺少 {definition.Key}。 ");
            Validate(definition, raw);
        }
    }

    public static void ValidateManagedValue(string key, string raw)
    {
        var definition = WorldSettingsService.ProfileDefinitions
            .Concat(WorldSettingsService.GlobalDefinitions)
            .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (definition is not null) Validate(definition, raw);
    }

    private static void Validate(WorldSettingDefinition definition, string raw)
    {
        switch (definition.Kind)
        {
            case WorldSettingKind.Boolean:
                if (!bool.TryParse(raw, out _))
                    throw new InvalidOperationException($"{definition.Key} 不是有效的 True/False。 ");
                break;
            case WorldSettingKind.Number:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new InvalidOperationException($"{definition.Key} 不是有效数值。 ");
                ValidateRange(definition, number);
                break;
            case WorldSettingKind.Integer:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) || !double.IsFinite(integer) || integer != Math.Truncate(integer))
                    throw new InvalidOperationException($"{definition.Key} 不是有效整数。 ");
                ValidateRange(definition, integer);
                break;
            case WorldSettingKind.Choice:
                if (definition.Choices is null || !definition.Choices.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{definition.Key} 的枚举值无效。 ");
                break;
        }
    }

    private static void ValidateRange(WorldSettingDefinition definition, double value)
    {
        if (value < definition.Minimum || value > definition.Maximum)
            throw new InvalidOperationException($"{definition.Key} 超出允许范围 {definition.Minimum.ToString(CultureInfo.InvariantCulture)}–{definition.Maximum.ToString(CultureInfo.InvariantCulture)}。 ");
    }
}
