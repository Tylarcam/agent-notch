using AgentNotch.Models;

namespace AgentNotch.Dock;

internal sealed class DockRailItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public AgentKind? Kind { get; init; }
    public bool IsOdysseus { get; init; }
    public DockRailStatus Status { get; set; } = DockRailStatus.Idle;
    public AgentSession? Session { get; set; }
}
