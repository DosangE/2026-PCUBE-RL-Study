using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

public enum Team
{
    Blue = 0,
    Red = 1
}

// Reward layout for this agent. Weights/budgets live on SoccerEnvController so one inspector field
// ablates a term across the whole field. Rationale + scale table: week4/RewardDesign.md
//
//   BASE     goal group reward +/-1                       (SoccerEnvController.GoalTouched)
//            goal individual mirror +/-1 (striker only)    (SoccerEnvController.GoalTouched)
//            existential  striker -1/N, goalie +1/N        (here, OnActionReceived)
//   SHAPING  S1 ball progress toward the attacked goal     (SoccerEnvController, group reward)
//            S2 striker ball touch                         (here, OnCollisionEnter)
//            G1 goalie clearance = how far the punt went   (SoccerEnvController -> RewardClearance)
//            G2 goalie blocking-position error             (here, OnActionReceived)
public class AgentSoccer : Agent
{
    public enum Position
    {
        Striker,
        Goalie,
        Generic
    }

    // Set per-player in the prefab/scene (blue vs red, and role).
    public Team team;
    public Position position;

    float m_KickPower;
    const float k_Power = 2000f;
    float m_LateralSpeed;
    float m_ForwardSpeed;
    float m_ExistentialReward;

    // Per-episode shaping bookkeeping. Every shaping term is budgeted so the most it can pay out in
    // one episode stays well under the +/-1 goal signal; reset in OnEpisodeBegin.
    float m_NextTouchRewardTime; // cooldown gate: vibrating against the ball must not farm S2/G1
    float m_TouchBudget;         // remaining S2 (striker) or G1 (goalie) budget this episode
    float m_PositionBudget;      // remaining G2 budget this episode

    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;
    SoccerEnvController m_EnvController;

    // Reset anchor + spawn rotation, read by SoccerEnvController.ResetScene().
    public Vector3 initialPos;
    public float rotSign;

    void Awake()
    {
        // Capture the spawn anchor here, NOT in Initialize(): ML-Agents calls Initialize()
        // lazily (several physics steps into play), by which point kickoff has already shoved
        // the agent off its spawn — poisoning the reset target and the goalie shaping anchor.
        initialPos = transform.position;
    }

    public override void Initialize()
    {
        rotSign = team == Team.Blue ? 1f : -1f;

        if (position == Position.Goalie)
        {
            m_LateralSpeed = 1.0f;
            m_ForwardSpeed = 1.0f;
        }
        else if (position == Position.Striker)
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.3f;
        }
        else
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.0f;
        }

        m_SoccerSettings = FindFirstObjectByType<SoccerSettings>();
        m_EnvController = GetComponentInParent<SoccerEnvController>();
        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        // Goalie is rewarded for time survived, striker penalized, so scoring fast is favored.
        var maxSteps = m_EnvController != null && m_EnvController.MaxEnvironmentSteps > 0
            ? m_EnvController.MaxEnvironmentSteps
            : 1;
        m_ExistentialReward = 1f / maxSteps;
        ResetShapingBudgets();
    }

    void ResetShapingBudgets()
    {
        m_NextTouchRewardTime = 0f;
        if (m_EnvController == null)
        {
            m_TouchBudget = 0f;
            m_PositionBudget = 0f;
            return;
        }
        m_TouchBudget = position == Position.Goalie
            ? m_EnvController.goalieClearanceBudget
            : m_EnvController.strikerTouchBudget;
        m_PositionBudget = m_EnvController.goaliePositionBudget;
    }

    public override void OnEpisodeBegin()
    {
        agentRb.linearVelocity = Vector3.zero;
        agentRb.angularVelocity = Vector3.zero;
        ResetShapingBudgets();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(agentRb.linearVelocity);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        AddReward(position == Position.Goalie ? m_ExistentialReward : -m_ExistentialReward);

        if (position == Position.Goalie)
            GoaliePositionShaping();

        MoveAgent(actions.DiscreteActions);
    }

    // G2 — goalie blocking position. The ideal spot is on the line from the defended goal to the
    // ball, a short standoff out from the goal line: holding it means the goalie both keeps its
    // depth AND slides across to cover the ball's angle, so one term does the work of two.
    //
    // This is the one place a POSITION reward beats a delta. For a striker "be near the ball"
    // breeds a player that hugs the ball and never shoots; for a goalie, holding position IS the
    // job. The safeguard is the budget: the most this can charge over a whole episode is
    // goaliePositionBudget (0.15), so it can never outweigh conceding (-1).
    void GoaliePositionShaping()
    {
        if (m_EnvController == null || m_PositionBudget <= 0f || m_EnvController.goaliePositionBudget <= 0f)
            return;

        var goal = m_EnvController.OwnGoalCenter(team);
        var ballPos = m_EnvController.ball.transform.position;
        var toBall = ballPos - goal;
        toBall.y = 0f;
        if (toBall.sqrMagnitude < 0.0001f)
            return;

        var ideal = goal + toBall.normalized * m_EnvController.goalieStandoff;
        var here = transform.position;
        here.y = ideal.y;
        var err = Mathf.Clamp01(Vector3.Distance(here, ideal) / m_EnvController.goaliePositionRefDist);

        // Spread the budget over the episode: worst-case error on every step spends exactly the budget.
        var perStep = m_EnvController.goaliePositionBudget / m_EnvController.MaxEnvironmentSteps;
        var penalty = Mathf.Min(perStep * err, m_PositionBudget);
        m_PositionBudget -= penalty;
        AddReward(-penalty);
    }

    // G1 payout, called by SoccerEnvController once the clearance measurement window closes.
    // gain01 is how far the ball travelled away from the goal we defend, normalized to 0..1.
    public void RewardClearance(float gain01)
    {
        if (m_EnvController == null || m_TouchBudget <= 0f)
            return;
        var reward = Mathf.Min(m_EnvController.goalieClearanceWeight * Mathf.Clamp01(gain01), m_TouchBudget);
        m_TouchBudget -= reward;
        AddReward(reward);
    }

    public void MoveAgent(ActionSegment<int> act)
    {
        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        m_KickPower = 0f;

        switch (act[0]) // 0 stop, 1 forward, 2 back
        {
            case 1: dirToGo = transform.forward * m_ForwardSpeed; m_KickPower = 1f; break;
            case 2: dirToGo = transform.forward * -m_ForwardSpeed; break;
        }

        switch (act[1]) // 0 stop, 1 strafe right, 2 strafe left
        {
            case 1: dirToGo = transform.right * m_LateralSpeed; break;
            case 2: dirToGo = transform.right * -m_LateralSpeed; break;
        }

        switch (act[2]) // 0 stop, 1 rotate right, 2 rotate left
        {
            case 1: rotateDir = transform.up * 1f; break;
            case 2: rotateDir = transform.up * -1f; break;
        }

        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed, ForceMode.VelocityChange);
    }

    // Keyboard control used when no trained model/trainer is attached (Behavior Type = Default/Heuristic).
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var kb = Keyboard.current;
        var d = actionsOut.DiscreteActions;
        d[0] = kb == null ? 0 : kb.wKey.isPressed ? 1 : kb.sKey.isPressed ? 2 : 0;
        d[1] = kb == null ? 0 : kb.eKey.isPressed ? 1 : kb.qKey.isPressed ? 2 : 0;
        d[2] = kb == null ? 0 : kb.dKey.isPressed ? 1 : kb.aKey.isPressed ? 2 : 0;
    }

    void OnCollisionEnter(Collision c)
    {
        var force = k_Power * m_KickPower;
        if (position == Position.Goalie)
        {
            force = k_Power;
        }
        if (!c.gameObject.CompareTag("ball"))
            return;

        var dir = c.contacts[0].point - transform.position;
        dir = dir.normalized;
        c.gameObject.GetComponent<Rigidbody>().AddForce(dir * force);

        if (m_EnvController == null)
            return;

        // A single physical contact reports many collision events as the ball rolls along the body,
        // so both touch-driven terms are gated by a cooldown; without it an agent parked on the ball
        // collects the bonus every physics step.
        var gated = Time.time < m_NextTouchRewardTime;
        m_NextTouchRewardTime = Time.time + m_EnvController.touchRewardCooldown;

        if (position == Position.Striker)
        {
            // S2 — striker ball touch. Pure bootstrap: until the agent can actually move the ball
            // goalward, S1 hands it no gradient at all, so "touch the ball" is the first thing worth
            // learning. Deliberately tiny and budgeted (0.1/episode = 20 touches at 0.005) so a
            // striker that only pokes the ball still earns far less than a single goal.
            if (gated || m_TouchBudget <= 0f)
                return;
            var reward = Mathf.Min(m_EnvController.strikerTouchWeight, m_TouchBudget);
            m_TouchBudget -= reward;
            AddReward(reward);
        }
        else if (position == Position.Goalie)
        {
            // G1 — clearance. Blocking is worthless if the ball drops at the goalie's feet for the
            // rebound, so we pay nothing for the touch itself: the env controller watches where the
            // ball ends up ~1s later and pays by DISTANCE GAINED from the defended goal.
            if (gated)
                return;
            m_EnvController.BeginClearanceWindow(this);
        }
    }
}
