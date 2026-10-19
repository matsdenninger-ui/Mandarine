using System.Windows;
using System.Windows.Media;
using Mandarine.Native;
using Microsoft.Win32;

namespace Mandarine.UI;

/// <summary>Follows the Windows light/dark app setting and re-colors the shared brushes live.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Initialize(Application app)
    {
        Apply(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                app.Dispatcher.InvokeAsync(() => Apply(app));
        };
    }

    static void Apply(Application app)
    {
        IsDark = WindowEffects.IsDarkMode();
        var r = app.Resources;
        if (IsDark)
        {
            Set(r, "Brush.Surface", "#2B2B2E");
            Set(r, "Brush.SurfaceAcrylic", "#33202020");
            Set(r, "Brush.Stroke", "#22FFFFFF");
            Set(r, "Brush.Text", "#F3F3F3");
            Set(r, "Brush.TextSecondary", "#A8A8AD");
            Set(r, "Brush.Hover", "#17FFFFFF");
            Set(r, "Brush.Pressed", "#26FFFFFF");
            Set(r, "Brush.Accent", "#FF9A3D");
            Set(r, "Brush.AccentSoft", "#33FF9A3D");
        }
        else
        {
            Set(r, "Brush.Surface", "#F7F7F8");
            Set(r, "Brush.SurfaceAcrylic", "#66FFFFFF");
            Set(r, "Brush.Stroke", "#14000000");
            Set(r, "Brush.Text", "#1B1B1F");
            Set(r, "Brush.TextSecondary", "#5F6368");
            Set(r, "Brush.Hover", "#0F000000");
            Set(r, "Brush.Pressed", "#1A000000");
            Set(r, "Brush.Accent", "#F27A00");
            Set(r, "Brush.AccentSoft", "#26FF8A1F");
        }
    }

    static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
