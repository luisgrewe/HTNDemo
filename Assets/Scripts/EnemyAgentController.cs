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
    public float enemyAvoidDistance = 25;
    public bool isHiding = true;

    public GameObject[] itemsRemaining;
    public GameObject[] hidingSpots;
    public NavMeshAgent agent;
    public EnemyAgentPlanner planner;
    private bool matchEnded;
    private bool hidingFromEnemy;
    private GameObject hidingDestination;
    
	// Use this for initialization
	void Start () {
        agent = GetComponent<NavMeshAgent>();
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
    }

    private void Update()
    {
        if (matchEnded)
        {
            return;
        }

        UpdateWorldState();

        if (planner.CurrentWorldState.enemyNear)
        {
            if (!hidingFromEnemy)
            {
                hidingFromEnemy = true;
                hidingDestination = GetBestHidingSpot();
                planner.CurrentWorldState.setIsHidden(false);
                gameMgr.OverlayText("The enemy agent is hiding in an alcove!", 3);
            }
        }

        if (hidingFromEnemy)
        {
            HideFromPatrollingEnemy();
            return;
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
        if (matchEnded || !other.CompareTag("Player"))
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
        planner.CurrentWorldState.setNumTeleports(numTeleportTraps);
        float teleDistanceSquared = teleDistance * teleDistance;
        float enemyAvoidDistanceSquared = enemyAvoidDistance * enemyAvoidDistance;

        float playerDist = -1;  // Default -1 in case Player is removed from game.

        Vector3 diff = FindClosestEnemy().transform.position - transform.position;
        float enemyDist = diff.sqrMagnitude;
        if (FindAgent() != null)    // in case Player gets removed from game
        {
            Vector3 diff2 = FindAgent().transform.position - transform.position;
            playerDist = diff2.sqrMagnitude;
        }

        // Get Enemy and Player distances 
        if (playerDist < teleDistanceSquared && playerDist != -1)
        {
            planner.CurrentWorldState.setPlayerNear(true);
        }
        else if (playerDist >= teleDistanceSquared || playerDist == -1)
        {
            planner.CurrentWorldState.setPlayerNear(false);
        }
        if (enemyDist < enemyAvoidDistanceSquared)
        {
            planner.CurrentWorldState.setEnemyNear(true);
        }
        else if (enemyDist >= enemyAvoidDistanceSquared)
        {
            planner.CurrentWorldState.setEnemyNear(false);
        }

        // Get position to see if in hiding spot. Do not include y because then the agent would have to be intersecting the floor
        GameObject bestHidingSpot = GetBestHidingSpot();
        if (bestHidingSpot != null)
        {
            Vector2 agentPosition = new Vector2(transform.position.x, transform.position.z);
            Vector2 hidingPosition = new Vector2(bestHidingSpot.transform.position.x, bestHidingSpot.transform.position.z);
            planner.CurrentWorldState.isHidden = Vector2.Distance(agentPosition, hidingPosition) < 1.5f;
        }
        else
        {
            planner.CurrentWorldState.isHidden = false;
        }
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
        hidingFromEnemy = true;
        hidingDestination = GetBestHidingSpot();
        planner.CurrentWorldState.setEnemyNear(true);
        planner.CurrentWorldState.setIsHidden(false);
        HideFromPatrollingEnemy();
        gameMgr.OverlayText("The enemy agent is hiding in an alcove!", 3);
    }

    private void HideFromPatrollingEnemy()
    {
        if (hidingDestination == null)
        {
            hidingDestination = GetBestHidingSpot();
        }

        if (hidingDestination == null)
        {
            hidingFromEnemy = false;
            return;
        }

        agent.destination = hidingDestination.transform.position;

        float distanceToHidingSpot = (transform.position - hidingDestination.transform.position).sqrMagnitude;
        GameObject enemy = FindClosestEnemy();
        float enemyDistance = enemy == null
            ? Mathf.Infinity
            : (transform.position - enemy.transform.position).sqrMagnitude;

        bool reachedHidingSpot = distanceToHidingSpot < 1.5f * 1.5f;
        bool enemyIsOutOfRange = enemyDistance >= enemyAvoidDistance * enemyAvoidDistance;

        if (reachedHidingSpot)
        {
            planner.CurrentWorldState.setIsHidden(true);
        }

        if (reachedHidingSpot && enemyIsOutOfRange)
        {
            hidingFromEnemy = false;
            hidingDestination = null;
            planner.CurrentWorldState.setEnemyNear(false);
        }
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

    public GameObject GetBestHidingSpot()
    {
        GameObject enemy = FindClosestEnemy();
        if (enemy == null)
        {
            return null;
        }

        GameObject safestSpot = null;
        float greatestEnemyDistance = -1f;
        for (int i = 0; i < hidingSpots.Length; i++)
        {
            GameObject spot = hidingSpots[i];
            if (spot == null)
            {
                continue;
            }

            float enemyDistance = (spot.transform.position - enemy.transform.position).sqrMagnitude;
            if (enemyDistance > greatestEnemyDistance)
            {
                greatestEnemyDistance = enemyDistance;
                safestSpot = spot;
            }
        }

        return safestSpot;
    }

    public void GoToClosestItem()
    {
        agent.destination = GetClosestItem().transform.position;
    }

    public void GoToBestHidingSpot()
    {
        agent.destination = GetBestHidingSpot().transform.position;
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
