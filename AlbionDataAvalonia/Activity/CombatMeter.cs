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
public sealed record CombatMeterRow(string Id, string Name, double Amount, double Rate, double Share, int Hits);
public sealed record CombatMeterSnapshot(DateTime Start, DateTime End, IReadOnlyList<CombatMeterRow> Rows,
    double Total, long Observations, bool Truncated);

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
            long count = 0;
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
                if (!actor.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!totals.TryGetValue(actor.Id, out var a)) totals.Add(actor.Id, a = new(actor.Name));
                a.Amount += Math.Abs(e.Delta); a.Hits++; count++;
                // A player's activity spans include seven seconds after each selected observation.
                var until = e.At.AddSeconds(7) < end ? e.At.AddSeconds(7) : end;
                var begins = e.At > a.ActiveUntil ? e.At : a.ActiveUntil;
                a.ActiveSeconds += Math.Max(0, (until - begins).TotalSeconds);
                if (until > a.ActiveUntil) a.ActiveUntil = until;
            }
            var total = totals.Values.Sum(x => x.Amount);
            var elapsed = Math.Max(1, (end - start).TotalSeconds);
            var rows = totals.Select(x => new CombatMeterRow(x.Key, x.Value.Name, x.Value.Amount,
                x.Value.Amount / (activeRate ? Math.Max(1, x.Value.ActiveSeconds) : elapsed),
                total > 0 ? x.Value.Amount / total * 100 : 0, x.Value.Hits)).OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(start, end, rows, total, count, truncated);
        }
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
        public DateTime ActiveUntil;
    }
}
