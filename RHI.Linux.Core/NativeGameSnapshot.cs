namespace RHI.Linux.Core;

public sealed record NativeLaunchConfiguration(string Path, string? Options, string? Error);

// A view of one game's native setup after pending runtime transactions recover. Sequence comparisons use values,
// so rereading identical files does not cause the UI to rebuild.
public sealed record NativeGameSnapshot(
    string? Executable,
    string? UnsupportedReason,
    NativeRuntimeStatus Runtime,
    NativeLoadEvidence? Evidence,
    IReadOnlyList<NativeLaunchConfiguration> LaunchConfigurations,
    string? RuntimeError,
    string? TargetError,
    string? EvidenceError)
{
    public bool HasSameValues(NativeGameSnapshot? other) => other != null &&
        Executable == other.Executable && UnsupportedReason == other.UnsupportedReason &&
        Runtime.State == other.Runtime.State && Runtime.Version == other.Runtime.Version &&
        Runtime.ForeignLayers.SequenceEqual(other.Runtime.ForeignLayers) &&
        Evidence == other.Evidence && LaunchConfigurations.SequenceEqual(other.LaunchConfigurations) &&
        RuntimeError == other.RuntimeError && TargetError == other.TargetError && EvidenceError == other.EvidenceError;

    public static NativeGameSnapshot Read(NativeReShade runtime, Game game, GamePreferences preferences)
    {
        string? executable = null, unsupported = null;
        string? runtimeError = null, targetError = null, evidenceError = null;
        NativeRuntimeStatus status;
        NativeLoadEvidence? evidence = null;
        try { status = runtime.Status(); }
        catch (Exception ex) when (IsReadError(ex))
        {
            status = new(NativeRuntimeState.Damaged, null, []);
            runtimeError = ex.Message;
        }
        try
        {
            executable = NativeReShade.Executable(game, preferences);
            unsupported = NativeReShade.Unsupported(game, preferences);
        }
        catch (Exception ex) when (IsReadError(ex)) { targetError = ex.Message; }
        try { if (executable != null) evidence = runtime.Evidence(executable); }
        catch (Exception ex) when (IsReadError(ex)) { evidenceError = ex.Message; }

        var configurations = new List<NativeLaunchConfiguration>();
        try
        {
            foreach (var path in Proton.LocalConfigs(game).Order(StringComparer.Ordinal))
            {
                try { configurations.Add(new(path, Proton.ReadOptions(path, game.AppId!), null)); }
                catch (Exception ex) when (IsReadError(ex)) { configurations.Add(new(path, null, ex.Message)); }
            }
        }
        catch (Exception ex) when (IsReadError(ex)) { configurations.Add(new("", null, ex.Message)); }

        return new(executable, unsupported, status, evidence, configurations, runtimeError, targetError, evidenceError);
    }

    private static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException
        or FormatException or System.Text.Json.JsonException;
}
