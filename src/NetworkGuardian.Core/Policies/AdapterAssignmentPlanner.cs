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

        // Bounded global search: retain alternatives rather than letting one greedy pair
        // consume the only SSID another adapter can use. Normal 2-4 adapter setups are exhaustive.
        var plans = new List<List<AdapterCandidateAssignment>> { results };
        foreach (var set in sets.OrderBy(s => s.InterfaceGuid))
        {
            var expanded = new List<List<AdapterCandidateAssignment>>();
            foreach (var plan in plans)
            {
                expanded.Add(plan);
                foreach (var candidate in set.Candidates.OrderByDescending(c => c.Score).Take(8))
                {
                    if (!allowSameSsidOnMultipleAdapters && plan.Any(p =>
                        string.Equals(p.Candidate.Ssid, candidate.Ssid, StringComparison.OrdinalIgnoreCase))) continue;
                    expanded.Add(new(plan) { new(set.InterfaceGuid, candidate) });
                }
            }
            plans = expanded.OrderByDescending(Utility).Take(512).ToList();
        }
        return plans.OrderByDescending(Utility).First();
    }

    private static double Utility(List<AdapterCandidateAssignment> plan) =>
        plan.Count == 0 ? 0 : plan.Count * 1000 + plan.Sum(p => p.Candidate.Score) + plan.Max(p => p.Candidate.Score);
}
