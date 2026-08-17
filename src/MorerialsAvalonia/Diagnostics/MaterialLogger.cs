using System.Diagnostics;

namespace MorerialsAvalonia.Diagnostics;

internal static class MaterialLogger
{
    internal static void Write(string message, Exception exception)
        => Trace.TraceError("{0}: {1}", message, exception);
}
