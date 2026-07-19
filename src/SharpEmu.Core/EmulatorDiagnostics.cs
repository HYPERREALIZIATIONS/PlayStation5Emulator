using System;

namespace SharpEmu.Core;

/// <summary>
/// Process-wide handle to the active logger, so that HLE modules and the CPU
/// backend can emit diagnostics without threading the Logger through every call.
/// </summary>
public static class EmulatorDiagnostics
{
    public static Logger Log { get; set; }
}
