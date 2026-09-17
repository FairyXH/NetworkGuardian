using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>A planned (adapter, candidate) pairing.</summary>
public sealed record AdapterCandidateAssignment(Guid InterfaceGuid, WifiCandidate Candidate);

/// <summary>One adapter's ranked candidate list, as produced by <see cref="CandidateSelector"/>.</summary>
public sealed record AdapterCandidateSet(Guid InterfaceGuid, IReadOnlyList<WifiCandidate> Candidates);

/// <summary>
/// Assigns candidates when more than one adapter needs a new connection in the same round.
/// The planner never touches adapters that still have a working connection: the caller only passes
/// the adapters that are actually disconnected.
/// </summary>
public static class AdapterAssignmentPlanner
{
    public static IReadOnlyList<AdapterCandidateAssignment> Plan(
        IReadOnlyList<AdapterCandidateSet> sets,
        bool allowSameSsidOnMultipleAdapters)
    {
        var results = new List<AdapterCandidateAssignment>();
        if (sets.Count == 0)
        {
            return results;
        }

        // Greedy global assignment: the strongest (adapter, candidate) pair wins first.
        var pool = sets
            .SelectMany(set => set.Candidates.Select(c => (Set: set.InterfaceGuid, Candidate: c)))
            .OrderByDescending(pair => pair.Candidate.Score)
            .ThenByDescending(pair => pair.Candidate.SignalQuality)
            .ThenBy(pair => pair.Set)
            .ThenBy(pair => pair.Candidate.Ssid, StringComparer.Ordinal)
            .ToList();

        var assignedAdapters = new HashSet<Guid>();
        var usedSsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (setGuid, candidate) in pool)
        {
            if (assignedAdapters.Contains(setGuid))
            {
                continue;
            }

            if (!allowSameSsidOnMultipleAdapters && usedSsids.Contains(candidate.Ssid))
            {
                continue;
            }

            assignedAdapters.Add(setGuid);
            usedSsids.Add(candidate.Ssid);
            results.Add(new AdapterCandidateAssignment(setGuid, candidate));
        }

        return results;
    }
}
