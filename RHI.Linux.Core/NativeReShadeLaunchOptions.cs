using System.Text.RegularExpressions;

namespace RHI.Linux.Core;

// Adds or removes only RESHADE_ENABLE=1 before %command%. Proton flags are never added here;
// unrelated settings are preserved and ambiguous shell syntax is refused.
public static class NativeReShadeLaunchOptions
{
    public const string Variable = "RESHADE_ENABLE";
    public const string DisableVariable = "DISABLE_RESHADE";
    public const string Assignment = Variable + "=1";

    public static bool IsEnabled(string existing)
    {
        try { return Validate(existing).Token != null; }
        catch (FormatException) { return false; }
    }

    public static string Enable(string existing)
    {
        var (options, token) = Validate(existing);
        return token != null ? existing : Assignment + " " + options;
    }

    public static string Disable(string existing)
    {
        var (options, token) = Validate(existing);
        if (token == null) return existing;
        var start = options.IndexOf(token, StringComparison.Ordinal);
        var end = start + token.Length;
        if (end < options.Length && options[end] == ' ') end++;
        return options[..start] + options[end..];
    }

    private static (string Options, string? Token) Validate(string existing)
    {
        if (existing.IndexOfAny(['\r', '\n']) >= 0) throw new FormatException("Launch options must be on one line.");
        var tokens = HdrLaunchOptions.Tokens(existing);
        if (tokens.Count == 0) existing = "%command%";
        else if (!existing.Contains("%command%", StringComparison.Ordinal) && existing.TrimStart().StartsWith('-')) existing = "%command% " + existing;
        tokens = HdrLaunchOptions.Tokens(existing);
        if (tokens.Count(t => t == "%command%") != 1 || existing.Split("%command%").Length != 2)
            throw new FormatException("Use exactly one unquoted %command% placeholder before configuring Native Vulkan ReShade.");
        if (tokens.Any(t => t.Trim('\'', '"') is "env" or "-i" or "--ignore-environment" || t.EndsWith("/env", StringComparison.Ordinal)))
            throw new FormatException("An environment wrapper may remove RESHADE_ENABLE. Configure Native Vulkan ReShade manually in your launcher.");
        if (tokens.Any(t => t.Contains(DisableVariable, StringComparison.Ordinal)))
            throw new FormatException("DISABLE_RESHADE is set and would override RESHADE_ENABLE. Review it in your launch options; RHI has not changed them.");
        var prefix = tokens.TakeWhile(t => Regex.IsMatch(t, "^[A-Za-z_][A-Za-z0-9_]*=")).ToList();
        var matches = tokens.Where(t => t.Contains(Variable, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return (existing, null);
        if (matches.Count != 1 || !prefix.Contains(matches[0]) || matches[0] is not (Assignment or Variable + "='1'" or Variable + "=\"1\""))
            throw new FormatException("RESHADE_ENABLE already appears with a conflicting value or in an ambiguous position. Review it in your launch options; RHI has not changed it.");
        return (existing, matches[0]);
    }
}
