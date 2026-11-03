using System;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace FastSTRM;

/// <summary>
/// Renders item names for log messages and the UI.
/// </summary>
/// <remarks>
/// A name comes from the file name on disk and is therefore untrusted. A name containing line
/// breaks lets a crafted .strm file forge whole log lines, and a very long name (or one repeated
/// once per library item during a probe) inflates the log file. Control characters are replaced
/// with spaces and the length is capped before the value reaches a log sink.
/// </remarks>
internal static class LogSafe
{
    /// <summary>Default cap for a name written to the log.</summary>
    public const int MaxLoggedLength = 120;

    /// <summary>Cap for a name rendered in the plugin's own UI.</summary>
    public const int MaxDisplayedLength = 200;

    /// <summary>
    /// Returns a log/UI safe rendering of the item's name (falling back to its path).
    /// </summary>
    public static string Name(BaseItem item, int maxLength = MaxLoggedLength)
    {
        var name = item.Name ?? item.Path ?? string.Empty;
        if (name.Length == 0)
        {
            return "(unnamed)";
        }

        if (name.Any(char.IsControl))
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                {
                    chars[i] = ' ';
                }
            }

            name = new string(chars);
        }

        return name.Length <= maxLength ? name : name[..maxLength] + "…";
    }
}
