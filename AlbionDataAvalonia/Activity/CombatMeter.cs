using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public enum CombatMeterScope { Personal, Party, Faction, AllObserved }
public enum CombatMeterMetric { Damage, Healing, DamageReceived, HealingReceived }
public enum CombatMeterTargets { All, Players, Mobs }

public sealed record CombatMeterActor(string Id, string Name, ActivityActorKind Kind,
    ActorRelationship Relationship, bool SameFaction);
public sealed record CombatMeterRow(string Id, string Name, double Amount, double Rate, double Share, int Hits, bool IsPersonal = false);
public sealed record CombatMeterContribution(double Amount, double Rate, double Share, int Hits)
{
    public static CombatMeterContribution Empty { get; } = new(0, 0, 0, 0);
}
public sealed record CombatMeterSnapshot(DateTime Start, DateTime End, IReadOnlyList<CombatMeterRow> Rows,
    double Total, long Observations, bool Truncated)
{
    public CombatMeterContribution Personal { get; init; } = CombatMeterContribution.Empty;
    public CombatMeterContribution Party { get; init; } = CombatMeterContribution.Empty;
    public CombatMeterContribution Overall { get; init; } = CombatMeterContribution.Empty;
}

/// <summary>Bounded, process-local health observations, independent of the Activity database.
/// Membership is frozen when the event arrives. No later roster change rewrites history.</summary>
public sealed class CombatMeter
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private const int MaximumObservations = 200_000;
    private readonly object sync = new();
    private readonly List<Observation> events = new();
    private int first;
    private DateTime resetAt = DateTime.UtcNow;
    private DateTime retainedFrom = DateTime.MinValue;
    private DateTime latest = DateTime.MinValue;
    private bool discarded;
    private string owner = "";
    private DateTime? pausedAt;

    public bool IsPaused { get { lock (sync) return pausedAt is not null; } }
    public DateTime ResetAt { get { lock (sync) return resetAt; } }

    internal void SetOwner(string identity, DateTime at)
    {
        lock (sync)
        {
            if (owner == identity) return;
            owner = identity;
            ResetCore(at);
        }
    }

    public void Reset(DateTime at) { lock (sync) ResetCore(at); }
    private void ResetCore(DateTime at)
    {
        events.Clear(); first = 0; resetAt = at; retainedFrom = DateTime.MinValue; latest = at; discarded = false;
        if (pausedAt is not null) pausedAt = at;
    }

    public void SetPaused(bool paused, DateTime now)
    {
        lock (sync) pausedAt = paused ? pausedAt ?? now : null;
    }

    internal void Observe(DateTime at, double delta, CombatMeterActor source, CombatMeterActor target)
    {
        if (!double.IsFinite(delta) || delta == 0) return;
        lock (sync)
        {
            if (pausedAt is not null || at < resetAt || at < retainedFrom) return;
            if (source.Kind != ActivityActorKind.Player && target.Kind != ActivityActorKind.Player) return;
            latest = at > latest ? at : latest;
            // Delivery order is normally capture order. Sort only the occasional delayed observation.
            var value = new Observation(at, delta, source, target);
            if (events.Count == first || events[^1].At <= at) events.Add(value);
            else
            {
                var index = events.BinarySearch(first, events.Count - first, value, ObservationOrder.Instance);
                events.Insert(index < 0 ? ~index : index, value);
            }
            Trim(latest);
        }
    }

    private void Trim(DateTime now)
    {
        var cutoff = now - Retention;
        while (first < events.Count && (events[first].At < cutoff || events.Count - first > MaximumObservations))
        {
            retainedFrom = events[first++].At;
            discarded = true;
        }
        if (first > 8192 && first * 2 >= events.Count) { events.RemoveRange(0, first); first = 0; }
    }

    public CombatMeterSnapshot Snapshot(DateTime now, TimeSpan? window, DateTime? from, DateTime? to,
        CombatMeterScope scope, CombatMeterMetric metric, CombatMeterTargets targets, bool includeSelfHealing,
        bool activeRate, string search)
    {
        lock (sync)
        {
            var end = to is { } upper && upper < now ? upper : now;
            if (pausedAt is { } paused && end > paused) end = paused;
            Trim(end);
            var requestedStart = from ?? (window is { } span ? end - span : resetAt);
            var start = requestedStart > resetAt ? requestedStart : resetAt;
            var truncated = discarded && retainedFrom >= start;
            if (truncated) start = retainedFrom;
            if (start > end) start = end;
            var totals = new Dictionary<string, Aggregate>();
            var personal = new Aggregate("Me");
            var party = new Aggregate("My group");
            var overall = new Aggregate("Scope");
            for (var i = first; i < events.Count; i++)
            {
                var e = events[i];
                if (e.At < start) continue;
                if (e.At > end) break;
                var healing = metric is CombatMeterMetric.Healing or CombatMeterMetric.HealingReceived;
                if (healing != (e.Delta > 0) || healing && !includeSelfHealing && e.Source.Id == e.Target.Id) continue;
                var received = metric is CombatMeterMetric.DamageReceived or CombatMeterMetric.HealingReceived;
                var actor = received ? e.Target : e.Source;
                var other = received ? e.Source : e.Target;
                if (actor.Kind != ActivityActorKind.Player || !InScope(actor, scope)) continue;
                if (targets == CombatMeterTargets.Players && other.Kind != ActivityActorKind.Player
                    || targets == CombatMeterTargets.Mobs && other.Kind != ActivityActorKind.Mob) continue;
                if (!totals.TryGetValue(actor.Id, out var a)) totals.Add(actor.Id, a = new(actor.Name));
                Accumulate(a, e, end);
                a.IsPersonal |= actor.Relationship == ActorRelationship.Self;
                Accumulate(overall, e, end);
                if (actor.Relationship == ActorRelationship.Self) Accumulate(personal, e, end);
                if (actor.Relationship is ActorRelationship.Self or ActorRelationship.Party) Accumulate(party, e, end);
            }
            var total = overall.Amount;
            var elapsed = Math.Max(1, (end - start).TotalSeconds);
            CombatMeterContribution Contribution(Aggregate value) => new(value.Amount,
                value.Amount / (activeRate ? Math.Max(1, value.ActiveSeconds) : elapsed),
                total > 0 ? value.Amount / total * 100 : 0, value.Hits);
            var rows = totals.Where(x => x.Value.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => new CombatMeterRow(x.Key, x.Value.Name, x.Value.Amount,
                    Contribution(x.Value).Rate, Contribution(x.Value).Share, x.Value.Hits, x.Value.IsPersonal)).OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(start, end, rows, total, overall.Hits, truncated)
            { Personal = Contribution(personal), Party = Contribution(party), Overall = Contribution(overall) };
        }
    }

    private static void Accumulate(Aggregate value, Observation observation, DateTime end)
    {
        value.Amount += Math.Abs(observation.Delta);
        value.Hits++;
        // Union of selected event spans, for a player or the entire overlapping summary.
        var until = observation.At.AddSeconds(7) < end ? observation.At.AddSeconds(7) : end;
        var begins = observation.At > value.ActiveUntil ? observation.At : value.ActiveUntil;
        value.ActiveSeconds += Math.Max(0, (until - begins).TotalSeconds);
        if (until > value.ActiveUntil) value.ActiveUntil = until;
    }

    private static bool InScope(CombatMeterActor actor, CombatMeterScope scope) => scope switch
    {
        CombatMeterScope.Personal => actor.Relationship == ActorRelationship.Self,
        CombatMeterScope.Party => actor.Relationship is ActorRelationship.Self or ActorRelationship.Party,
        CombatMeterScope.Faction => actor.SameFaction,
        _ => true
    };
    private sealed record Observation(DateTime At, double Delta, CombatMeterActor Source, CombatMeterActor Target);
    private sealed class ObservationOrder : IComparer<Observation>
    {
        public static readonly ObservationOrder Instance = new();
        public int Compare(Observation? x, Observation? y) => DateTime.Compare(x!.At, y!.At);
    }
    private sealed class Aggregate(string name)
    {
        public string Name { get; } = name;
        public double Amount, ActiveSeconds;
        public int Hits;
        public bool IsPersonal;
        public DateTime ActiveUntil;
    }
}
