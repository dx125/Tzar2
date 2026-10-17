using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 8 (placeholder until pathfinding, §9.2): moves every <see cref="MotionState.Moving"/> entity in a
/// straight line toward its target at its speed, stops it at impassable tiles, and updates facing and occupancy.
/// </summary>
/// <remarks>
/// The position update runs as fork/join jobs over fixed chunks of <see cref="ChunkSize"/> movers (the chunking
/// depends only on the mover count). Each job writes only its own movers' components and its own output buffers.
/// The occupancy update and event emission then run on the sim thread, merging job outputs in job order, so the
/// result is independent of the worker count (ADR 0017).
/// </remarks>
public sealed class MovementSystem : ISimSystem
{
    /// <summary>Movers per job.</summary>
    public const int ChunkSize = 512;

    private readonly MoveJob _job = new();

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Movement;

    /// <inheritdoc />
    public string Name => "movement";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        ComponentStore<Motion> motions = world.Motions;
        int jobs = (motions.Count + ChunkSize - 1) / ChunkSize;
        _job.Prepare(world, jobs);
        context.Workers.Run(_job, jobs);

        // Fixed-order merge.
        MapState map = world.Map;
        ComponentStore<OccupancyCell> cells = world.OccupancyCells;
        for (int j = 0; j < jobs; j++)
        {
            JobOutput output = _job.Outputs[j];
            for (int k = 0; k < output.MovedCount; k++)
            {
                EntityId entity = motions.Entities[output.Moved[k]];
                int cellPosition = cells.IndexOf(entity);
                if (cellPosition < 0)
                {
                    continue;
                }

                ref OccupancyCell cell = ref cells.At(cellPosition);
                ref Position position = ref world.Positions.Get(entity);
                int newCell = map.CellIndexAt(position.X, position.Y);
                if (newCell != cell.Cell)
                {
                    if (cell.Cell >= 0)
                    {
                        map.RemoveOccupant(cell.Cell);
                    }

                    map.AddOccupant(newCell);
                    cell.Cell = newCell;
                }
            }

            world.Events.EmitRange(output.Events.AsSpan(0, output.EventCount));
        }
    }

    private sealed class JobOutput
    {
        public int[] Moved = new int[ChunkSize];
        public int MovedCount;
        public SimEvent[] Events = new SimEvent[16];
        public int EventCount;

        public void Emit(in SimEvent e)
        {
            if (EventCount == Events.Length)
            {
                Array.Resize(ref Events, Events.Length * 2);
            }

            Events[EventCount++] = e;
        }
    }

    private sealed class MoveJob : IParallelJob
    {
        private World _world = null!;
        private Locomotor[] _locomotors = [];

        public JobOutput[] Outputs { get; private set; } = [];

        public void Prepare(World world, int jobs)
        {
            _world = world;
            if (_locomotors.Length != world.Ruleset.EntityTypes.Count)
            {
                _locomotors = new Locomotor[world.Ruleset.EntityTypes.Count];
                for (int i = 0; i < _locomotors.Length; i++)
                {
                    _locomotors[i] = world.Ruleset.EntityTypes[i].Locomotor;
                }
            }

            if (Outputs.Length < jobs)
            {
                JobOutput[] grown = new JobOutput[jobs];
                Outputs.CopyTo(grown, 0);
                for (int i = Outputs.Length; i < jobs; i++)
                {
                    grown[i] = new JobOutput();
                }

                Outputs = grown;
            }

            for (int i = 0; i < jobs; i++)
            {
                Outputs[i].MovedCount = 0;
                Outputs[i].EventCount = 0;
            }
        }

        public void Execute(int jobIndex)
        {
            World world = _world;
            MapState map = world.Map;
            ComponentStore<Motion> motions = world.Motions;
            ComponentStore<Position> positions = world.Positions;
            ComponentStore<Identity> identities = world.Identities;
            JobOutput output = Outputs[jobIndex];
            ReadOnlySpan<EntityId> entities = motions.Entities;
            Span<Motion> data = motions.Components;
            int end = Math.Min(entities.Length, (jobIndex + 1) * ChunkSize);
            for (int i = jobIndex * ChunkSize; i < end; i++)
            {
                ref Motion motion = ref data[i];
                if (motion.State != MotionState.Moving)
                {
                    continue;
                }

                EntityId entity = entities[i];
                ref Position position = ref positions.Get(entity);
                ref Identity identity = ref identities.Get(entity);
                long dx = (long)motion.TargetX - position.X;
                long dy = (long)motion.TargetY - position.Y;
                long distanceSquared = (dx * dx) + (dy * dy);
                long speed = motion.SpeedPerTick;
                int nx;
                int ny;
                bool arrives = distanceSquared <= speed * speed;
                if (arrives)
                {
                    nx = motion.TargetX;
                    ny = motion.TargetY;
                }
                else
                {
                    long distance = IntMath.ISqrt(distanceSquared);
                    nx = (int)(position.X + (dx * speed / distance));
                    ny = (int)(position.Y + (dy * speed / distance));
                    if (nx == position.X && ny == position.Y)
                    {
                        // Guarantee progress for very slow movers: one unit along the dominant axis.
                        if (Math.Abs(dx) >= Math.Abs(dy))
                        {
                            nx += Math.Sign(dx);
                        }
                        else
                        {
                            ny += Math.Sign(dy);
                        }
                    }
                }

                if (dx != 0 || dy != 0)
                {
                    identity.Facing = (byte)Trig.Atan2(dy, dx).ToDirection8();
                }

                if (!map.IsPassableAt(_locomotors[identity.TypeId], nx, ny))
                {
                    motion.State = MotionState.Blocked;
                    output.Emit(new SimEvent(SimEventKind.Blocked, identity.Owner, entity, position.X, position.Y, 0));
                    continue;
                }

                position.X = nx;
                position.Y = ny;
                output.Moved[output.MovedCount++] = i;
                if (arrives)
                {
                    motion.State = MotionState.Idle;
                    output.Emit(new SimEvent(SimEventKind.Arrived, identity.Owner, entity, nx, ny, 0));
                }
            }
        }
    }
}
