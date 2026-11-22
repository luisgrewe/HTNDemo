using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class EnemyAgentController : MonoBehaviour {

    public GameManager gameMgr;
    private int numItems = 0;
    public bool isAlive = true;
    public int numTeleportTraps = 2;
    public float teleDistance = 20;
    public bool isHiding = true;

    // How far ahead (in seconds) the agent predicts the yellow vision boxes when planning a move.
    public float chaseLookaheadTime = 2.5f;
    public float hideLookaheadTime = 2.5f;
    // Extra clearance in front of / behind a vision box along its travel direction.
    public float visionForwardMargin = 1.5f;
    // Extra clearance beside a vision box. Must stay small enough that the alcoves count as safe.
    public float visionSideMargin = 0.5f;
    // Added to the forward margin while already avoiding, so the agent doesn't flicker between chase and hide.
    public float avoidHysteresis = 1f;

    public GameObject[] itemsRemaining;
    public GameObject[] hidingSpots;
    public NavMeshAgent agent;
    public EnemyAgentPlanner planner;
    private bool matchEnded;
    private bool avoidingEnemy;
    private GameObject hidingDestination;
    private NavMeshPath scratchPath;
    private readonly List<Transform> visionBoxes = new List<Transform>();
    private readonly List<Vector3> visionVelocities = new List<Vector3>();
    private readonly List<float> visionOffUntil = new List<float>();
    private Collider[] obstacleColliders;
    
	// Use this for initialization
	void Start () {
        agent = GetComponent<NavMeshAgent>();
        scratchPath = new NavMeshPath();
        itemsRemaining = gameMgr.items;
        hidingSpots = new GameObject[12];
        for(int i = 0; i < gameMgr.alcoves.Length; i++) // add all the alcoves to the hidingSpots
        {
            hidingSpots[i] = gameMgr.alcoves[i];
        }
        hidingSpots[10] = gameMgr.spawner.leftSpot;
        hidingSpots[11] = gameMgr.spawner.rightSpot;
        planner = GetComponent<EnemyAgentPlanner>();
        numTeleportTraps = 2;

        ObstacleController[] obstacles = FindObjectsByType<ObstacleController>(FindObjectsSortMode.None);
        obstacleColliders = new Collider[obstacles.Length];
        for (int i = 0; i < obstacles.Length; i++)
        {
            obstacleColliders[i] = obstacles[i].GetComponent<Collider>();
        }
    }

    private void Update()
    {
        if (matchEnded)
        {
            return;
        }

        UpdateWorldState();

        avoidingEnemy = planner.CurrentWorldState.enemyNear;
        if (!avoidingEnemy)
        {
            hidingDestination = null;
        }

        planner.MakePlan();
    }

    private void OnCollisionEnter(Collision collision)
    {
        CatchPlayer(collision.gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        CatchPlayer(other.gameObject);
    }

    private void CatchPlayer(GameObject other)
    {
        if (matchEnded)
        {
            return;
        }

        if (other.CompareTag("Enemy"))
        {
            Caught();
            return;
        }

        if (!other.CompareTag("Player"))
        {
            return;
        }

        matchEnded = true;
        agent.isStopped = true;
        PlayerController2 player = other.GetComponent<PlayerController2>();
        if (player != null)
        {
            player.isAlive = false;
        }
        gameMgr.GameOver("The Enemy Agent caught the Player.");
    }

    void UpdateWorldState()
    {
        WorldState ws = planner.CurrentWorldState;
        ws.setNumTeleports(numTeleportTraps);
        RefreshVisionBoxes();

        GameObject player = FindAgent();
        ws.setPlayerAlive(player != null);
        ws.setPlayerNear(player != null &&
            (player.transform.position - transform.position).sqrMagnitude < teleDistance * teleDistance);

        float forwardMargin = visionForwardMargin + (avoidingEnemy ? avoidHysteresis : 0f);
        bool safeToStay = IsSafeToStay(transform.position, 0f, hideLookaheadTime, forwardMargin);

        // "Enemy near" means the yellow vision box would catch us if we kept chasing right now.
        bool enemyNear;
        if (player != null)
        {
            enemyNear = ExposureAlongPath(transform.position, player.transform.position, chaseLookaheadTime, forwardMargin, false) > 0f;
        }
        else
        {
            enemyNear = !safeToStay;
        }

        // Once heading for a hiding spot, keep going until we're in it instead of stopping mid-corridor.
        bool reachedHidingSpot = hidingDestination == null ||
            Vector2.Distance(new Vector2(transform.position.x, transform.position.z),
                new Vector2(hidingDestination.transform.position.x, hidingDestination.transform.position.z)) < 1.5f;

        ws.setEnemyNear(enemyNear);
        ws.setIsHidden(safeToStay && reachedHidingSpot);
    }

    void RefreshVisionBoxes()
    {
        visionBoxes.Clear();
        visionVelocities.Clear();
        visionOffUntil.Clear();
        foreach (GameObject enemyObj in GameObject.FindGameObjectsWithTag("Enemy"))
        {
            EnemyController enemy = enemyObj.GetComponent<EnemyController>();
            if (enemy == null || enemy.transform.childCount == 0)
            {
                continue;
            }

            Transform box = enemy.transform.GetChild(0);
            Vector3 velocity = enemy.transform.right * enemy.forwardSpeed;
            visionBoxes.Add(box);
            visionVelocities.Add(velocity);
            visionOffUntil.Add(box.gameObject.activeSelf ? 0f : VisionOffTimeRemaining(enemy, velocity));
        }
    }

    // While an enemy passes through an obstacle its vision is switched off. Returns how many seconds are left
    // until its body leaves the obstacle and the vision comes back.
    float VisionOffTimeRemaining(EnemyController enemy, Vector3 velocity)
    {
        Collider body = enemy.GetComponent<Collider>();
        float speed = Mathf.Abs(velocity.x);
        if (body == null || obstacleColliders == null || speed < 0.01f)
        {
            return 0f;
        }

        Bounds bodyBounds = body.bounds;
        foreach (Collider obstacle in obstacleColliders)
        {
            if (obstacle == null || !obstacle.bounds.Intersects(bodyBounds))
            {
                continue;
            }

            float distanceLeft = velocity.x > 0f
                ? obstacle.bounds.max.x - bodyBounds.min.x
                : bodyBounds.max.x - obstacle.bounds.min.x;
            // Leave a little slack so the agent is out of the way before the vision snaps back on.
            return Mathf.Max(0f, distanceLeft / speed - 0.15f);
        }
        return 0f;
    }

    // Is the point inside any yellow vision box, as predicted t seconds from now?
    bool IsInVision(Vector3 point, float t, float forwardMargin)
    {
        float radius = agent != null ? agent.radius : 0.5f;
        for (int i = 0; i < visionBoxes.Count; i++)
        {
            if (t < visionOffUntil[i])
            {
                continue;
            }

            Transform box = visionBoxes[i];
            Vector3 center = box.position + visionVelocities[i] * t;
            Vector3 size = box.lossyScale;
            float halfX = Mathf.Abs(size.x) * 0.5f + radius + forwardMargin;
            float halfZ = Mathf.Abs(size.z) * 0.5f + radius + visionSideMargin;
            if (Mathf.Abs(point.x - center.x) <= halfX && Mathf.Abs(point.z - center.z) <= halfZ)
            {
                return true;
            }
        }
        return false;
    }

    bool IsSafeToStay(Vector3 point, float fromTime, float toTime, float forwardMargin)
    {
        for (float t = fromTime; t <= toTime; t += 0.1f)
        {
            if (IsInVision(point, t, forwardMargin))
            {
                return false;
            }
        }
        return true;
    }

    // Walks the NavMesh path from start to target at the agent's speed and returns roughly how many seconds a
    // vision box would overlap the agent (0 = never seen). Only the first lookaheadTime seconds are checked.
    // If waitAtEnd is set, also counts time spent standing at the target until lookaheadTime.
    float ExposureAlongPath(Vector3 start, Vector3 target, float lookaheadTime, float forwardMargin, bool waitAtEnd)
    {
        Vector3[] corners;
        if (NavMesh.CalculatePath(start, target, NavMesh.AllAreas, scratchPath) && scratchPath.corners.Length > 0)
        {
            corners = scratchPath.corners;
        }
        else
        {
            corners = new Vector3[] { start, target };
        }

        float speed = Mathf.Max(agent != null ? agent.speed : 6f, 0.1f);
        const float step = 0.5f;
        float stepTime = step / speed;
        float travelled = 0f;
        float exposure = IsInVision(start, 0f, forwardMargin) ? stepTime : 0f;
        Vector3 previous = start;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 segment = corners[i] - previous;
            float length = segment.magnitude;
            for (float d = step; d < length + step; d += step)
            {
                float along = Mathf.Min(d, length);
                float t = (travelled + along) / speed;
                if (t > lookaheadTime)
                {
                    return exposure;
                }
                if (IsInVision(previous + segment * (along / Mathf.Max(length, 0.0001f)), t, forwardMargin))
                {
                    exposure += stepTime;
                }
            }
            travelled += length;
            previous = corners[i];
        }

        if (waitAtEnd)
        {
            for (float t = travelled / speed; t <= lookaheadTime; t += 0.1f)
            {
                if (IsInVision(previous, t, forwardMargin))
                {
                    exposure += 0.1f;
                }
            }
        }
        return exposure;
    }
    
    public void IncrementNumItems()
    {
        numItems++;
        gameMgr.notifyItemPickedUp();
    }

    public int GetNumItems()
    {
        return numItems;
    }

    public void Caught()
    {
        planner.CurrentWorldState.setEnemyNear(true);
        planner.CurrentWorldState.setIsHidden(false);
        GoToBestHidingSpot();
    }

    public void Respawn()
    {
        agent.Warp(gameMgr.spawner.GetComponent<SpawnController>().agentSpawnPoints[Random.Range(0, 10)].transform.position
            + new Vector3(0, 0.5f, 0));
    }

    public void RespawnFarFromPlayer()
    {
        GameObject player = FindAgent();
        GameObject[] spawnPoints = gameMgr.spawner.GetComponent<SpawnController>().agentSpawnPoints;
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            return;
        }

        GameObject farthestPoint = spawnPoints[0];
        float greatestPlayerDistance = -1f;
        for (int i = 0; i < spawnPoints.Length; i++)
        {
            if (spawnPoints[i] == null)
            {
                continue;
            }

            float playerDistance = player == null
                ? 0f
                : (spawnPoints[i].transform.position - player.transform.position).sqrMagnitude;
            if (playerDistance > greatestPlayerDistance)
            {
                greatestPlayerDistance = playerDistance;
                farthestPoint = spawnPoints[i];
            }
        }

        agent.Warp(farthestPoint.transform.position + new Vector3(0, 0.5f, 0));
        agent.ResetPath();
    }

    public void ActivateTeleportTrap()
    {
        numTeleportTraps--;

        GameObject closestEnemy = FindClosestEnemy();
        GameObject closestAgent = FindAgent();

        Vector3 diff = Vector3.zero;

        if (closestEnemy)
        {
            diff = closestEnemy.transform.position - transform.position;
        }
        float enemyDist = diff.sqrMagnitude;

        if (closestAgent)
        {
            diff = closestAgent.transform.position - transform.position;
        }
        float agentDist = diff.sqrMagnitude;

        if (closestEnemy && enemyDist < agentDist)
        {
            gameMgr.OverlayText("Your opponent teleported an enemy!", 3);
            closestEnemy.GetComponent<EnemyController>().respawning = true;
            closestEnemy.GetComponent<EnemyController>().Respawn();
        }
        else if(closestAgent)
        {
            gameMgr.OverlayText("Your opponent teleported you!", 3);
            closestAgent.GetComponent<PlayerController2>().Respawn();
        }
    }

    public GameObject FindClosestEnemy()
    {
        GameObject[] gos;
        gos = GameObject.FindGameObjectsWithTag("Enemy");
        GameObject closest = null;
        float distance = Mathf.Infinity;
        Vector3 position = transform.position;
        foreach (GameObject go in gos)
        {
            Vector3 diff = go.transform.position - position;
            float curDistance = diff.sqrMagnitude;
            if (curDistance < distance)
            {
                closest = go;
                distance = curDistance;
            }
        }
        return closest;
    }

    public GameObject FindAgent()
    {
        return GameObject.FindGameObjectWithTag("Player");
    }

    public GameObject GetClosestItem()
    {
        itemsRemaining = gameMgr.items;
        GameObject closest = null;
        float distance = Mathf.Infinity;
        Vector3 position = transform.position;
        for(int i = 0; i < itemsRemaining.Length; i++)
        {
            if (itemsRemaining[i])
            {
                Vector3 diff = itemsRemaining[i].transform.position - position;
                float curDistance = diff.sqrMagnitude;
                if (curDistance < distance)
                {
                    closest = itemsRemaining[i];
                    distance = curDistance;
                }
            }
        }
        return closest;
    }

    // Picks the closest hiding spot the agent can reach and wait in without a vision box sweeping over it.
    // If none is fully safe, picks the one where the agent spends the least time in view.
    public GameObject GetBestHidingSpot()
    {
        if (scratchPath == null)
        {
            scratchPath = new NavMeshPath();
        }
        RefreshVisionBoxes();

        float forwardMargin = visionForwardMargin + (avoidingEnemy ? avoidHysteresis : 0f);
        float lookahead = hideLookaheadTime * 2f;

        if (hidingDestination != null &&
            ExposureAlongPath(transform.position, hidingDestination.transform.position, lookahead, forwardMargin, true) == 0f)
        {
            return hidingDestination;
        }

        GameObject bestSpot = null;
        float bestExposure = Mathf.Infinity;
        float bestDistance = Mathf.Infinity;

        for (int i = 0; i < hidingSpots.Length; i++)
        {
            GameObject spot = hidingSpots[i];
            if (spot == null)
            {
                continue;
            }

            float exposure = ExposureAlongPath(transform.position, spot.transform.position, lookahead, forwardMargin, true);
            float distance = PathLength(transform.position, spot.transform.position);
            if (exposure < bestExposure - 0.01f || (exposure < bestExposure + 0.01f && distance < bestDistance))
            {
                bestExposure = exposure;
                bestDistance = distance;
                bestSpot = spot;
            }
        }

        return bestSpot;
    }

    float PathLength(Vector3 start, Vector3 target)
    {
        if (!NavMesh.CalculatePath(start, target, NavMesh.AllAreas, scratchPath) || scratchPath.corners.Length < 2)
        {
            return Vector3.Distance(start, target);
        }

        float length = 0f;
        for (int i = 1; i < scratchPath.corners.Length; i++)
        {
            length += Vector3.Distance(scratchPath.corners[i - 1], scratchPath.corners[i]);
        }
        return length;
    }

    public void GoToClosestItem()
    {
        agent.destination = GetClosestItem().transform.position;
    }

    public void GoToBestHidingSpot()
    {
        GameObject spot = GetBestHidingSpot();
        if (spot == null)
        {
            return;
        }

        if (spot != hidingDestination)
        {
            hidingDestination = spot;
            gameMgr.OverlayText("The enemy agent is hiding from the patrol!", 2);
        }
        agent.destination = spot.transform.position;
    }

    public void ChasePlayer()
    {
        GameObject player = FindAgent();
        if (player != null)
        {
            agent.destination = player.transform.position;
        }
    }

    public void Idle()
    {
        agent.destination = transform.position;
    }
}
