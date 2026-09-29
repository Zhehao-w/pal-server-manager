using System.Globalization;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HaoHaoTianTian.PalHR.Windows;

public static class WorldSettingsEditorDialog
{
    public static async Task<WorldSettingsEditorResult?> ShowAsync(
        XamlRoot xamlRoot,
        WorldSettingsEditorSession session,
        WorldSettingsService service,
        bool serverRunning,
        bool allowRestart,
        bool includeGlobalSettings = true,
        bool existingWorldImport = false)
    {
        var values = new Dictionary<string, string>(session.ProfileValues, StringComparer.OrdinalIgnoreCase);
        var globals = new Dictionary<string, string>(session.GlobalValues, StringComparer.OrdinalIgnoreCase);
        var controls = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        var status = new TextBlock
        {
            Text = existingWorldImport
                ? "只准备导入世界的管理设置档；不会修改存档或服务器全局设置。"
                : session.IsDraft
                ? "这些设置会在新世界第一次生成前应用。"
                : serverRunning ? "修改将在服务器下次启动后生效。" : "保存后，将在下次启动此存档时生效。",
            Foreground = Brush("PalMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var body = new StackPanel
        {
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var header = new StackPanel { Spacing = 5 };
        header.Children.Add(new TextBlock
        {
            Text = existingWorldImport ? "导入现有存档 · 自定义世界设置" : session.IsDraft ? "新存档 · 自定义世界设置" : $"存档 {session.SlotId} · {session.Tag}",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = includeGlobalSettings
                ? "每个存档的设置分别保存在 JSON 配置档中。服务器名称等位于最下方的全局区域，不会进入存档配置。"
                : "仅编辑此存档的世界设置。ServerName、密码、端口等全局设置不会导入或改动。",
            Foreground = Brush("PalMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        body.Children.Add(header);

        foreach (var category in WorldSettingsService.ProfileDefinitions.Select(item => item.Category).Distinct())
        {
            var definitions = WorldSettingsService.ProfileDefinitions.Where(item => item.Category == category).ToArray();
            body.Children.Add(BuildCategory(category, definitions, values, controls));
        }
        if (includeGlobalSettings)
            body.Children.Add(BuildCategory("服务器（全局共享）", WorldSettingsService.GlobalDefinitions, globals, controls,
                "这些值由所有存档共享；管理器不会把它们写入任何存档 profile。AdminPassword、REST、端口和 RCON 不在此处管理。"));

        var utility = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        utility.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        utility.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        utility.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var defaultsButton = new Button { Content = "恢复游戏默认值" };
        var importButton = new Button { Content = "导入当前 INI" };
        Grid.SetColumn(importButton, 1);
        Grid.SetColumn(status, 2);
        utility.Children.Add(defaultsButton);
        utility.Children.Add(importButton);
        utility.Children.Add(status);
        body.Children.Add(utility);

        defaultsButton.Click += async (_, _) =>
        {
            try
            {
                values = await service.GetDefaultValuesAsync();
                PopulateControls(WorldSettingsService.ProfileDefinitions, values, controls);
                status.Text = "已载入本机 DefaultPalWorldSettings.ini；点击保存后才会写入 profile。";
            }
            catch (Exception exception) { status.Text = $"读取默认值失败：{exception.Message}"; }
        };
        importButton.Click += async (_, _) =>
        {
            try
            {
                values = await service.ImportCurrentIniAsync();
                PopulateControls(WorldSettingsService.ProfileDefinitions, values, controls);
                status.Text = "已导入当前 PalWorldSettings.ini；点击保存后才会写入 profile。";
            }
            catch (Exception exception) { status.Text = $"导入失败：{exception.Message}"; }
        };

        var scroll = new ScrollViewer
        {
            Content = body,
            MaxWidth = 840,
            MaxHeight = 640,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "世界设置",
            Content = scroll,
            PrimaryButtonText = session.IsDraft ? "采用此设置" : "保存设置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            MinWidth = 700
        };
        if (allowRestart && serverRunning)
        {
            dialog.SecondaryButtonText = "保存并重启";
        }

        WorldSettingsEditorResult? result = null;
        void CaptureResult(ContentDialogButtonClickEventArgs args, bool restart)
        {
            try
            {
                var profileResult = ReadControls(WorldSettingsService.ProfileDefinitions, controls);
                var globalResult = includeGlobalSettings ? ReadControls(WorldSettingsService.GlobalDefinitions, controls) : globals;
                result = new WorldSettingsEditorResult(profileResult, globalResult, restart);
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                status.Text = exception.Message;
            }
        }
        dialog.PrimaryButtonClick += (_, args) => CaptureResult(args, false);
        dialog.SecondaryButtonClick += (_, args) => CaptureResult(args, true);
        var outcome = await dialog.ShowAsync();
        return outcome is ContentDialogResult.Primary or ContentDialogResult.Secondary ? result : null;
    }

    private static StackPanel BuildCategory(
        string title,
        IReadOnlyList<WorldSettingDefinition> definitions,
        IReadOnlyDictionary<string, string> values,
        IDictionary<string, Control> controls,
        string? note = null)
    {
        var section = new StackPanel { Spacing = 7 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("PalMutedTextBrush")
        });

        var rows = new StackPanel { Spacing = 0 };
        if (!string.IsNullOrWhiteSpace(note))
        {
            rows.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = Brush("PalMutedTextBrush"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 12, 16, 10)
            });
            rows.Children.Add(new Border { Height = 1, Background = Brush("PalBorderBrush"), Opacity = 0.45 });
        }

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var row = new Grid { MinHeight = 64, ColumnSpacing = 20, Padding = new Thickness(16, 8, 16, 8) };
            ToolTipService.SetToolTip(row, $"INI: {definition.Key}\n{definition.Description}");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock { Text = definition.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = definition.Description, Foreground = Brush("PalMutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var control = CreateControl(definition, values.TryGetValue(definition.Key, out var value) ? value : "");
            control.MinHeight = 32;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(text);
            row.Children.Add(control);
            rows.Children.Add(row);
            if (index < definitions.Count - 1)
                rows.Children.Add(new Border { Height = 1, Background = Brush("PalBorderBrush"), Margin = new Thickness(16, 0, 16, 0), Opacity = 0.45 });
            controls[definition.Key] = control;
        }

        section.Children.Add(new Border
        {
            Background = Brush("PalCardAltBrush"),
            BorderBrush = Brush("PalBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = rows
        });
        return section;
    }

    private static Control CreateControl(WorldSettingDefinition definition, string raw)
    {
        switch (definition.Kind)
        {
            case WorldSettingKind.Boolean:
                return new ToggleSwitch
                {
                    IsOn = bool.TryParse(raw, out var enabled) && enabled,
                    OnContent = "开启",
                    OffContent = "关闭",
                    HorizontalAlignment = HorizontalAlignment.Left
                };
            case WorldSettingKind.Number:
            case WorldSettingKind.Integer:
                return new NumberBox
                {
                    Value = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN,
                    Minimum = definition.Minimum,
                    Maximum = definition.Maximum,
                    SmallChange = definition.Step,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                    ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
                };
            case WorldSettingKind.Choice:
                var combo = new ComboBox { ItemsSource = definition.Choices, HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectedItem = definition.Choices?.FirstOrDefault(item => string.Equals(item, raw, StringComparison.OrdinalIgnoreCase)) ?? definition.Choices?.FirstOrDefault();
                return combo;
            case WorldSettingKind.PlatformList:
                return new TextBox { Text = raw.Trim().TrimStart('(').TrimEnd(')'), PlaceholderText = "Steam,Xbox,PS5,Mac" };
            default:
                return definition.IsSecret
                    ? new PasswordBox { Password = PalWorldIniDocument.Unquote(raw), PlaceholderText = "留空表示无密码" }
                    : new TextBox { Text = PalWorldIniDocument.Unquote(raw) };
        }
    }

    private static void PopulateControls(
        IReadOnlyList<WorldSettingDefinition> definitions,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, Control> controls)
    {
        foreach (var definition in definitions)
        {
            if (!values.TryGetValue(definition.Key, out var raw) || !controls.TryGetValue(definition.Key, out var control)) continue;
            switch (control)
            {
                case ToggleSwitch toggle: toggle.IsOn = bool.TryParse(raw, out var enabled) && enabled; break;
                case NumberBox number: number.Value = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN; break;
                case ComboBox combo: combo.SelectedItem = definition.Choices?.FirstOrDefault(item => string.Equals(item, raw, StringComparison.OrdinalIgnoreCase)); break;
                case PasswordBox password: password.Password = PalWorldIniDocument.Unquote(raw); break;
                case TextBox text when definition.Kind == WorldSettingKind.PlatformList: text.Text = raw.Trim().TrimStart('(').TrimEnd(')'); break;
                case TextBox text: text.Text = PalWorldIniDocument.Unquote(raw); break;
            }
        }
    }

    private static Dictionary<string, string> ReadControls(
        IReadOnlyList<WorldSettingDefinition> definitions,
        IReadOnlyDictionary<string, Control> controls)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!controls.TryGetValue(definition.Key, out var control)) throw new InvalidOperationException($"找不到 {definition.Label} 的输入控件。 ");
            values[definition.Key] = control switch
            {
                ToggleSwitch toggle => toggle.IsOn ? "True" : "False",
                NumberBox number when double.IsNaN(number.Value) => throw new InvalidOperationException($"“{definition.Label}”需要有效数值。"),
                NumberBox number when definition.Kind == WorldSettingKind.Integer => Math.Round(number.Value).ToString("0", CultureInfo.InvariantCulture),
                NumberBox number => number.Value.ToString("0.######", CultureInfo.InvariantCulture),
                ComboBox combo when combo.SelectedItem is string choice => choice,
                PasswordBox password => PalWorldIniDocument.Quote(password.Password),
                TextBox text when definition.Kind == WorldSettingKind.PlatformList => FormatPlatforms(text.Text, definition.Label),
                TextBox text => PalWorldIniDocument.Quote(text.Text),
                _ => throw new InvalidOperationException($"“{definition.Label}”没有有效选择。")
            };
        }
        return values;
    }

    private static string FormatPlatforms(string value, string label)
    {
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) throw new InvalidOperationException($"“{label}”至少需要一个平台。 ");
        return $"({string.Join(',', entries)})";
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
