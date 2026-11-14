// File: src/eh.DockManager.Server/Portal/PortalSelf.cs
using eh.DockManager.Server.Models;

namespace eh.DockManager.Server.Portal;

/// <summary>
/// Identifies DockManager's own catalogue entry — the portal app that points at DockManager's own
/// container. Deleting it would remove the tile that opens the management interface, so the landing
/// flag, the REST delete endpoint and the MCP <c>delete_portal_app</c> tool all consult this single
/// rule.
/// </summary>
public static class PortalSelf
{
    /// <summary>
    /// True when <paramref name="app"/> is DockManager itself. Matched by <c>EHDM_SELF_CONTAINER</c>
    /// (container name or subdomain), by the container id against the running host name, or by the
    /// container name.
    /// </summary>
    public static bool IsSelf(PortalApp app)
    {
        var selfHost = Environment.MachineName;
        var selfOverride = Environment.GetEnvironmentVariable("EHDM_SELF_CONTAINER");
        return
            (selfOverride is { Length: > 0 } &&
             (string.Equals(app.ContainerName, selfOverride, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(app.Subdomain, selfOverride, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(app.ContainerId) &&
             app.ContainerId.StartsWith(selfHost, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(app.ContainerName, selfHost, StringComparison.OrdinalIgnoreCase);
    }
}
