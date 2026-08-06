using System.Reflection;

namespace EdiToSscs.App;

/// <summary>
/// The application icon, loaded once and shared by every window.
///
/// <see cref="EdiToSscs.App"/>'s csproj also points <c>ApplicationIcon</c> at the same file, which is
/// what Windows shows for the EXE on the desktop and in File Explorer. This copy is for the windows
/// themselves — title bar, taskbar button and Alt-Tab — which WinForms would otherwise draw with its
/// own generic icon.
///
/// The .ico carries a separate drawing at each size from 16 to 256, so the small ones are their own
/// simplified artwork rather than a shrunken 256px image.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon?> Loaded = new(Load);

    /// <summary>The icon, or null if it could not be read — never a reason to fail to start.</summary>
    public static Icon? Value => Loaded.Value;

    /// <summary>Applies the icon to a window, doing nothing if it could not be loaded.</summary>
    public static void ApplyTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (Value is not null) form.Icon = Value;
    }

    private static Icon? Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }
}
