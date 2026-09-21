namespace NetworkGuardian.Core.Policies;

/// <summary>Selects one stable adapter that may use Windows automatic connection for a profile.</summary>
public static class ExclusiveAutoConnectPolicy
{
    public static Guid? SelectOwner(
        IEnumerable<Guid> knownPhysicalAdapters,
        IEnumerable<Guid> currentlyAvailableAdapters)
    {
        ArgumentNullException.ThrowIfNull(knownPhysicalAdapters);
        ArgumentNullException.ThrowIfNull(currentlyAvailableAdapters);

        // Disabled adapters are absent from WlanEnumInterfaces. Prefer the complete PnP inventory so
        // ownership cannot move to another adapter during startup and move back when hardware appears.
        var physical = knownPhysicalAdapters
            .Where(guid => guid != Guid.Empty)
            .Distinct()
            .OrderBy(guid => guid)
            .ToList();

        return physical.Count > 0
            ? physical[0]
            : currentlyAvailableAdapters
                .Where(guid => guid != Guid.Empty)
                .Distinct()
                .OrderBy(guid => guid)
                .Cast<Guid?>()
                .FirstOrDefault();
    }
}
