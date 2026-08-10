using Engine;
using Game;
using Game.NetWork;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Keeps the world alive around the NPC when it travels beyond the
/// player-loaded bubble, so autonomous expeditions actually work. The engine
/// does everything around players only; this mirrors that machinery around
/// the NPC while it is far away:
///  - registers an extra terrain update location so chunks load around it;
///  - wakes hibernated entities and seeds creature spawns in surrounding
///    spawn chunks, exactly like the engine's per-camera SpawnChunks pass
///    (same limits and weighted spawn tables, so no creature inflation);
///  - shields nearby creatures from the every-2s "far from all players"
///    despawn sweep by clearing AutoDespawn while they are close, restoring
///    it when they (or the NPC) leave.
/// Server side only; dormant while the NPC stays near a player.
/// </summary>
public sealed class GeniusExpeditionKeeper
{
    private const int UpdateLocationIndex = 910_007;
    private const float ActivateDistance = 40f;
    private const float DeactivateDistance = 32f;
    /// <summary>
    /// How far terrain exists around a lone NPC — and therefore the hard limit
    /// on how wide find_blocks can see out here. The engine keeps 64 blocks of
    /// content around each player camera and FREES every chunk past the
    /// furthest update location, so this number IS the companion's horizon.
    /// Raised from 24 once the request below stopped asking for render
    /// geometry: content-only chunks reach TerrainChunkState.InvalidVertices1
    /// (cells, light, block behaviors, spawns — everything simulation needs)
    /// and skip vertex buffer generation entirely, which is the expensive part.
    /// </summary>
    private const float LoadRadius = 48f;
    private const float ProtectRadius = 48f;
    private const float ReleaseRadius = 56f;
    private const int ChunkRange = 2;

    private readonly List<ComponentSpawn> _protected = [];
    private readonly HashSet<Point2> _seeded = [];
    private readonly Game.Random _random = new();
    private bool _active;
    private double _nextTickTime;
    private double _nextWildSpawnTime;

    public void Tick(ComponentGeniusBrain brain)
    {
        if (CommonLib.WorkType == WorkType.Client)
        {
            return;
        }

        var now = brain.m_subsystemTime.GameTime;
        if (now < _nextTickTime)
        {
            return;
        }

        _nextTickTime = now + 1.0;
        var players = brain.Project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
        var spawn = brain.Project.FindSubsystem<SubsystemSpawn>(throwOnError: false);
        var creatureSpawn = brain.Project.FindSubsystem<SubsystemCreatureSpawn>(throwOnError: false);
        if (players is null || spawn is null || creatureSpawn is null)
        {
            return;
        }

        var myPosition = brain.Creature.ComponentBody.Position;
        var nearestPlayer = float.MaxValue;
        foreach (var player in players.ComponentPlayers)
        {
            nearestPlayer = Math.Min(
                nearestPlayer, Vector3.Distance(player.ComponentBody.Position, myPosition));
        }

        if (_active && nearestPlayer < DeactivateDistance)
        {
            Engine.Log.Information("[Genius] Expedition keeper OFF (back near a player).");
            Deactivate(brain);
            return;
        }

        if (!_active)
        {
            if (nearestPlayer <= ActivateDistance)
            {
                return;
            }

            _active = true;
            Engine.Log.Information(
                $"[Genius] Expedition keeper ON at {myPosition} ({nearestPlayer:0}m from nearest player).");
        }

        // visibilityDistance 0, contentDistance LoadRadius — the engine's own
        // headless pattern (PlayerData asks for 0/64 while a player is still
        // loading). Nobody renders from the NPC's eyes, so asking for geometry
        // was pure waste; content-only chunks carry the whole simulation.
        brain.SubsystemTerrain.TerrainUpdater.SetUpdateLocation(
            UpdateLocationIndex, new Vector2(myPosition.X, myPosition.Z), 0f, LoadRadius);
        KeepChunksAlive(brain, spawn, creatureSpawn, myPosition, now);
        ProtectFromDespawn(spawn, myPosition);
    }

    /// <summary>Call when the NPC entity leaves the world.</summary>
    public void Shutdown(ComponentGeniusBrain brain) => Deactivate(brain);

    private void Deactivate(ComponentGeniusBrain brain)
    {
        if (_active)
        {
            brain.SubsystemTerrain.TerrainUpdater.RemoveUpdateLocation(UpdateLocationIndex);
        }

        foreach (var tracked in _protected)
        {
            if (tracked.Entity.Project is not null && !tracked.IsDespawning)
            {
                tracked.AutoDespawn = true;
            }
        }

        _protected.Clear();
        _active = false;
    }

    /// <summary>The engine's per-camera SpawnChunks pass, centered on the NPC.</summary>
    private void KeepChunksAlive(
        ComponentGeniusBrain brain,
        SubsystemSpawn spawn,
        SubsystemCreatureSpawn creatureSpawn,
        Vector3 myPosition,
        double now)
    {
        var gameInfo = brain.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: false);
        if (gameInfo?.WorldSettings.EnvironmentBehaviorMode != EnvironmentBehaviorMode.Living)
        {
            return;
        }

        var terrain = brain.SubsystemTerrain.Terrain;
        var npcChunk = Terrain.ToChunk(new Vector2(myPosition.X, myPosition.Z));
        var readyChunks = new List<SpawnChunk>();
        for (var dx = -ChunkRange; dx <= ChunkRange; dx++)
        {
            for (var dz = -ChunkRange; dz <= ChunkRange; dz++)
            {
                var chunkPoint = new Point2(npcChunk.X + dx, npcChunk.Y + dz);
                var terrainChunk = terrain.GetChunkAtCell(chunkPoint.X * 16 + 8, chunkPoint.Y * 16 + 8);
                if (terrainChunk is null || terrainChunk.State <= TerrainChunkState.InvalidPropagatedLight)
                {
                    continue;
                }

                var spawnChunk = spawn.GetOrCreateSpawnChunk(chunkPoint);
                spawnChunk.LastVisitedTime = now;
                if (spawnChunk.SpawnsData.Count > 0)
                {
                    // Wake entities that hibernated here when players left.
                    foreach (var data in spawnChunk.SpawnsData)
                    {
                        spawn.SpawnEntity(data);
                    }

                    spawnChunk.SpawnsData.Clear();
                }

                // The engine can recycle SpawnChunk records (IsSpawned resets),
                // which made us re-seed the same chunks 36s apart in playtest 2
                // — creature-inflation risk. Our own per-life set is the guard.
                if (!spawnChunk.IsSpawned && _seeded.Add(chunkPoint))
                {
                    // First visit ever: initial wildlife seeding, vanilla cadence.
                    creatureSpawn.SpawnChunkCreatures(
                        spawnChunk, creatureSpawn.GetSpawnFactorByPlayerCount(), constantSpawn: false);
                    creatureSpawn.SpawnChunkCreatures(spawnChunk, 2, constantSpawn: true);
                    spawnChunk.IsSpawned = true;
                    Engine.Log.Information(
                        $"[Genius] Expedition keeper seeded first-visit spawn chunk {chunkPoint}.");
                }

                readyChunks.Add(spawnChunk);
            }
        }

        // Wilderness respawn trickle (the vanilla random-spawn pass is
        // camera-driven, so run our own at the same interval and limits).
        if (now >= _nextWildSpawnTime && readyChunks.Count > 0)
        {
            _nextWildSpawnTime = now + creatureSpawn.GetEffectiveSpawnIntervalTime();
            var chunk = readyChunks[_random.Int(0, readyChunks.Count - 1)];
            creatureSpawn.SpawnChunkCreatures(chunk, 1, constantSpawn: false);
        }
    }

    private void ProtectFromDespawn(SubsystemSpawn spawn, Vector3 myPosition)
    {
        for (var i = _protected.Count - 1; i >= 0; i--)
        {
            var tracked = _protected[i];
            if (tracked.Entity.Project is null || tracked.IsDespawning)
            {
                _protected.RemoveAt(i);
                continue;
            }

            if (Vector3.Distance(tracked.ComponentFrame.Position, myPosition) > ReleaseRadius)
            {
                tracked.AutoDespawn = true;
                _protected.RemoveAt(i);
            }
        }

        foreach (var candidate in spawn.Spawns)
        {
            if (!candidate.AutoDespawn || candidate.IsDespawning)
            {
                continue;
            }

            if (Vector3.Distance(candidate.ComponentFrame.Position, myPosition) <= ProtectRadius)
            {
                candidate.AutoDespawn = false;
                _protected.Add(candidate);
            }
        }
    }
}
