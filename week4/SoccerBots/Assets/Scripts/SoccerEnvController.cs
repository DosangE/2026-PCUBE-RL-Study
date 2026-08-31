using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// Spawn/reset the players and ball, handle a goal (score + group reward + reset), and own every
// shaping weight for this field. Rationale + scale table: week4/RewardDesign.md
public class SoccerEnvController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public AgentSoccer Agent;
        [HideInInspector]
        public Vector3 StartingPos;
        [HideInInspector]
        public Quaternion StartingRot;
        [HideInInspector]
        public Rigidbody Rb;
    }

    [Tooltip("Steps before the round auto-resets (0 = never). 3000 steps @50Hz = 60s of match time. " +
             "Also the denominator of the existential reward (striker -1/N, goalie +1/N), so a full " +
             "timeout costs a striker exactly one goal's worth.")]
    public int MaxEnvironmentSteps = 3000;

    [Tooltip("Scatter the ball and non-goalie players across the whole pitch each reset " +
             "(instead of near their kickoff spots) to improve striker generalization. " +
             "Goalies always keep their goal line.")]
    public bool strongRandomization = true;

    // ---------------------------------------------------------------------------------------
    // Shaping weights. Every one is budgeted per episode so the sum of all shaping stays well
    // under the +/-1 goal signal — otherwise the agents farm shaping instead of scoring.
    // Set any weight/budget to 0 to ablate that term (that is how they were tuned one at a time).
    // ---------------------------------------------------------------------------------------

    [Header("S1 - ball progress (team, delta)")]
    [Tooltip("Reward per world unit the ball moves toward the goal a team attacks; the conceding " +
             "team gets the exact negative, so it is zero-sum and cannot be farmed by shuttling the " +
             "ball back and forth. 0.005 x the ~34u pitch = 0.17 for driving the ball end to end, " +
             "about 1/6 of a goal. This is the striker's main gradient: it rewards ADVANCING the " +
             "ball, whereas a 'be near the ball' bonus just breeds ball-hugging.")]
    public float ballProgressWeight = 0.005f;

    [Tooltip("Ignore per-step ball movement larger than this (world units). Guards against paying " +
             "out on teleports — resets, goal respawns — rather than on actual play.")]
    public float ballProgressMaxPerStep = 2f;

    [Header("S2 - striker ball touch (individual, event)")]
    [Tooltip("Reward for a striker touching the ball. Bootstrap only: before the striker can move " +
             "the ball at all, S1 gives zero gradient. Small enough that 20 touches (the budget) " +
             "are worth 1/10 of a goal.")]
    public float strikerTouchWeight = 0.005f;

    [Tooltip("Max total S2 payout per striker per episode.")]
    public float strikerTouchBudget = 0.1f;

    [Header("G1 - goalie clearance (individual, event, delta)")]
    [Tooltip("Reward for a goalie punting the ball clear, paid on how far the ball actually got " +
             "from the defended goal within the measurement window (not for the touch itself — a " +
             "block that drops at the goalie's feet is a rebound, not a save).")]
    public float goalieClearanceWeight = 0.02f;

    [Tooltip("Max total G1 payout per goalie per episode (5 clean clearances).")]
    public float goalieClearanceBudget = 0.1f;

    [Tooltip("Physics steps to watch the ball after a goalie touch before paying G1. 50 @50Hz = 1s.")]
    public int goalieClearanceWindow = 50;

    [Tooltip("Distance gained (world units) that counts as a full-value clearance.")]
    public float goalieClearanceRefDist = 15f;

    [Header("G2 - goalie blocking position (individual, per-step)")]
    [Tooltip("Max total G2 penalty per goalie per episode. Charged in proportion to how far the " +
             "goalie is from the ideal blocking spot, so a goalie that holds its line pays ~0 and " +
             "one that wanders the pitch pays at most this. Capped so it can never outweigh -1 for " +
             "conceding.")]
    public float goaliePositionBudget = 0.15f;

    [Tooltip("How far out from the goal centre the ideal blocking spot sits, along the goal->ball line. The goal " +
             "centre measured from the net colliders is ~21.4 from the field centre and the goalie is " +
             "authored at 17, so 4.5 puts the ideal spot on the goalie's own home line.")]
    public float goalieStandoff = 4.5f;

    [Tooltip("Distance from the ideal spot that counts as maximum position error.")]
    public float goaliePositionRefDist = 10f;

    [Header("Shared")]
    [Tooltip("Seconds before the same agent can trigger another touch-based reward (S2/G1). One " +
             "physical contact fires many collision events as the ball rolls along the body; " +
             "without this an agent parked on the ball farms the bonus every physics step.")]
    public float touchRewardCooldown = 0.5f;

    [Tooltip("VsGoalie lesson only: per-step penalty scale for a ball that sits still. The " +
             "penalty grows exponentially the longer the ball stays put, pushing strikers to " +
             "go fetch a ball behind them instead of loafing. 0 disables it.")]
    public float ballStallPenaltyScale = 0.00005f;

    [Tooltip("Max total ball-stall penalty per episode.")]
    public float ballStallBudget = 0.1f;

    public GameObject ball;
    [HideInInspector]
    public Rigidbody ballRb;
    Vector3 m_BallStartingPos;

    // List of players on this field.
    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    public int blueScore;
    public int redScore;

    int m_ResetTimer;
    SimpleMultiAgentGroup m_BlueAgentGroup;
    SimpleMultiAgentGroup m_RedAgentGroup;

    // World-space centres of each team's own goal, measured once from the tagged goal colliders so
    // this works for every field copy without hard-coding pitch dimensions.
    Vector3 m_BlueGoalCenter; // the goal Blue defends and Red attacks
    Vector3 m_RedGoalCenter;  // the goal Red defends and Blue attacks

    // S1 state: last frame's ball distance to the red goal (the goal Blue attacks).
    float m_PrevBallDistToRedGoal;

    // G1 state: one clearance measurement in flight at a time (the latest goalie punt wins).
    AgentSoccer m_ClearanceGoalie;
    int m_ClearanceStepsLeft;
    float m_ClearanceStartDist;

    // VsGoalie lesson (2 attacking strikers vs the other team's lone goalie): the attacker/defender
    // roles are fixed for the round, so we can shape defense specifically. Set in ResetScene.
    bool m_VsGoalieLesson;
    Team m_DefenderTeam;      // the team whose goalie is defending
    int m_BallStillSteps;      // consecutive FixedUpdate steps the ball has been ~motionless
    float m_StallPenaltyPaid;  // total stall penalty charged this round (kept bounded, see below)

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        m_BallStartingPos = ball.transform.position;

        m_BlueGoalCenter = FindGoalCenter("blueGoal");
        m_RedGoalCenter = FindGoalCenter("redGoal");

        m_BlueAgentGroup = new SimpleMultiAgentGroup();
        m_RedAgentGroup = new SimpleMultiAgentGroup();

        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();
        }
        // Group membership is (re)built per lesson in ResetScene, so the field can shrink/grow.
        ResetScene();
    }

    // Union of this field's goal colliders for a tag, flattened to the pitch plane.
    Vector3 FindGoalCenter(string goalTag)
    {
        var found = false;
        var bounds = new Bounds();
        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (!col.CompareTag(goalTag))
                continue;
            if (!found) { bounds = col.bounds; found = true; }
            else bounds.Encapsulate(col.bounds);
        }
        if (!found)
        {
            Debug.LogWarning($"{name}: no collider tagged '{goalTag}' found; goalie/progress shaping will be wrong.");
            return transform.position;
        }
        var c = bounds.center;
        c.y = transform.position.y;
        return c;
    }

    // The goal a team defends. Used by the goalie positioning shaping (G2).
    public Vector3 OwnGoalCenter(Team team)
    {
        return team == Team.Blue ? m_BlueGoalCenter : m_RedGoalCenter;
    }

    void FixedUpdate()
    {
        m_ResetTimer += 1;
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            m_BlueAgentGroup.GroupEpisodeInterrupted();
            m_RedAgentGroup.GroupEpisodeInterrupted();
            ResetScene();
            return;
        }

        RewardBallProgress();
        TickClearanceWindow();

        if (m_VsGoalieLesson)
            PenalizeBallStalling();
    }

    // S1 — ball progress toward the attacked goal, as a GROUP reward so MA-POCA does the credit
    // assignment (whoever actually moved it gets the credit, not whoever happened to be nearby).
    //
    // Delta, not position, on purpose: "reward being near the ball" produces an agent that parks
    // next to the ball and jiggles, while "reward the ball getting closer to their goal" produces
    // an agent that drives it forward. It is also exactly zero-sum between the two teams and sums
    // to zero over any round trip, so there is no oscillation exploit — the only way to bank it
    // permanently is to end the episode with the ball deep, which means scoring.
    void RewardBallProgress()
    {
        var dist = Vector3.Distance(ball.transform.position, m_RedGoalCenter);
        var progress = m_PrevBallDistToRedGoal - dist; // >0: ball moved toward the goal Blue attacks
        m_PrevBallDistToRedGoal = dist;

        if (ballProgressWeight <= 0f || Mathf.Abs(progress) > ballProgressMaxPerStep)
            return; // oversized jump == a reset/respawn teleport, not play

        var r = ballProgressWeight * progress;
        m_BlueAgentGroup.AddGroupReward(r);
        m_RedAgentGroup.AddGroupReward(-r);
    }

    // G1 — start measuring a goalie's clearance. Called from AgentSoccer.OnCollisionEnter; a fresh
    // touch re-arms the window from the ball's current spot so the LAST punt is the one scored.
    public void BeginClearanceWindow(AgentSoccer goalie)
    {
        m_ClearanceGoalie = goalie;
        m_ClearanceStepsLeft = goalieClearanceWindow;
        m_ClearanceStartDist = Vector3.Distance(ball.transform.position, OwnGoalCenter(goalie.team));
    }

    void TickClearanceWindow()
    {
        if (m_ClearanceGoalie == null)
            return;
        if (--m_ClearanceStepsLeft > 0)
            return;

        var goalie = m_ClearanceGoalie;
        m_ClearanceGoalie = null;
        if (!goalie.gameObject.activeSelf)
            return;

        var gained = Vector3.Distance(ball.transform.position, OwnGoalCenter(goalie.team)) - m_ClearanceStartDist;
        if (gained > 0f)
            goalie.RewardClearance(gained / goalieClearanceRefDist);
    }

    // VsGoalie lesson only: if the ball sits still too long the attackers are loafing (typically
    // the ball is behind them and they never turn to fetch it). Penalize the attacking group by an
    // amount that grows exponentially with how long the ball has stayed put, so dawdling snowballs.
    void PenalizeBallStalling()
    {
        const float stillSpeed = 0.15f; // below this the ball counts as "not moving"
        const int graceSteps = 100;     // ~2s @ 50fps of stillness tolerated before it bites
        const float tau = 100f;         // steps for the exponent to grow by 1

        if (ballRb.linearVelocity.sqrMagnitude < stillSpeed * stillSpeed)
            m_BallStillSteps++;
        else
            m_BallStillSteps = 0;

        var over = m_BallStillSteps - graceSteps;
        if (over <= 0 || ballStallPenaltyScale <= 0f || m_StallPenaltyPaid >= ballStallBudget)
            return;

        // exp curve, tiny at first then steep. Per-step exponent capped (e^6); ALSO cap the running
        // total per round, otherwise a ball stuck for hundreds of steps accumulates a group penalty
        // that swamps the +/-1 goal signal.
        var attackerGroup = m_DefenderTeam == Team.Blue ? m_RedAgentGroup : m_BlueAgentGroup;
        var penalty = ballStallPenaltyScale * (Mathf.Exp(Mathf.Min(over / tau, 6f)) - 1f);
        penalty = Mathf.Min(penalty, ballStallBudget - m_StallPenaltyPaid);
        m_StallPenaltyPaid += penalty;
        attackerGroup.AddGroupReward(-penalty);
    }

    public void ResetBall()
    {
        // Strong mode spreads the ball across most of the pitch (kept off the goal lines);
        // otherwise a small jitter around the center spot.
        var range = strongRandomization ? new Vector2(14f, 7f) : new Vector2(2.5f, 2.5f);
        var randomPosX = Random.Range(-range.x, range.x);
        var randomPosZ = Random.Range(-range.y, range.y);

        ball.transform.position = m_BallStartingPos + new Vector3(randomPosX, 0f, randomPosZ);
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
    }

    public void GoalTouched(Team scoredTeam)
    {
        var scoredGroup = scoredTeam == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
        var concededGroup = scoredTeam == Team.Blue ? m_RedAgentGroup : m_BlueAgentGroup;

        if (m_VsGoalieLesson && scoredTeam == m_DefenderTeam)
        {
            // The ball went into the ATTACKER's own (undefended) goal, which registers as the
            // goalie's team "scoring". Don't credit the goalie for it — instead punish the
            // attacking strikers hard for the own goal.
            concededGroup.AddGroupReward(-2f);
        }
        else
        {
            // Flat +1 for a goal. Time pressure already comes from each striker's per-step
            // existential penalty (-1/N per step); ALSO discounting the goal by time double-counts
            // it, so a scored episode netted only 1 - 2t/N. That kept the Striker's mean cumulative
            // reward below the curriculum threshold and blocked lesson progression.
            scoredGroup.AddGroupReward(1f);
            concededGroup.AddGroupReward(-1f);
        }

        // Mirror the goal onto each active striker's INDIVIDUAL reward.
        foreach (var item in AgentsList)
        {
            var a = item.Agent;
            if (!a.gameObject.activeSelf || a.position != AgentSoccer.Position.Striker)
                continue;
            a.AddReward(a.team == scoredTeam ? 1f : -1f);
        }

        if (scoredTeam == Team.Blue) blueScore++; else redScore++;

        scoredGroup.EndGroupEpisode();
        concededGroup.EndGroupEpisode();
        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;
        m_BallStillSteps = 0; // ball is reset to rest below; don't carry stillness across rounds
        m_StallPenaltyPaid = 0f;
        m_ClearanceGoalie = null;

        // Curriculum lesson (from Python environment_parameters): 0 = 2 strikers vs empty goal,
        // 1 = 2 strikers vs a lone goalie, 2 = full 3v3 self-play. Default 2 when run in-editor.
        var lesson = Academy.Instance.EnvironmentParameters.GetWithDefault("lesson", 2f);

        Team randTeam = Random.value < 0.5f ? Team.Blue : Team.Red;
        // randTeam's strikers attack; the other team's lone goalie defends (see ActiveInLesson).
        m_VsGoalieLesson = lesson >= 1f && lesson < 2f;
        m_DefenderTeam = randTeam == Team.Blue ? Team.Red : Team.Blue;
        foreach (var item in AgentsList)
        {
            var agent = item.Agent;
            var active = ActiveInLesson(agent, lesson, randTeam);
            agent.gameObject.SetActive(active); // benched agents leave the field: no body, no decisions.

            var group = agent.team == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
            if (!active)
            {
                group.UnregisterAgent(agent);
                continue;
            }
            group.RegisterAgent(agent);

            // Field-local pitch half-extents that keep spawns off the walls/goals. initialPos is
            // world-space and there are many field copies, so we work relative to the field root.
            const float pitchHalfX = 19f; // goal line begins ~20.2
            const float pitchHalfZ = 8f;  // side walls at ~9.8

            Vector3 newStartPos;
            Quaternion newRot;
            if (strongRandomization && agent.position != AgentSoccer.Position.Goalie)
            {
                // Scatter non-goalie players anywhere on the pitch, facing a random direction, so the
                // striker generalizes past the near-home kickoff layout. Goalies keep their line.
                var lx = Random.Range(-pitchHalfX, pitchHalfX);
                var lz = Random.Range(-pitchHalfZ, pitchHalfZ);
                newStartPos = new Vector3(transform.position.x + lx, agent.initialPos.y, transform.position.z + lz);
                newRot = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
            }
            else
            {
                // Default: small depth (x) jitter around home, clamped to the pitch, authored facing.
                var newX = agent.initialPos.x + Random.Range(-5f, 5f);
                var localX = Mathf.Clamp(newX - transform.position.x, -pitchHalfX, pitchHalfX);
                newStartPos = new Vector3(transform.position.x + localX, agent.initialPos.y, agent.initialPos.z);
                newRot = Quaternion.Euler(0, agent.rotSign * Random.Range(80f, 100f), 0);
            }
            agent.transform.SetPositionAndRotation(newStartPos, newRot);

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        //Reset Ball
        ResetBall();

        // Re-baseline S1 AFTER the ball teleports, so the respawn jump is never paid out.
        m_PrevBallDistToRedGoal = Vector3.Distance(ball.transform.position, m_RedGoalCenter);
    }

    // Who is on the field for a given curriculum lesson. Blue always attacks the Red goal
    // (see SoccerBallController), so Blue strikers are the ones being trained up.
    static bool ActiveInLesson(AgentSoccer agent, float lesson, Team team = Team.Blue)
    {

        var striker = agent.team == team && agent.position == AgentSoccer.Position.Striker;
        if (lesson < 1f)   // Lesson 0: only the attacking strikers, empty goal.
            return striker;
        if (lesson < 2f)   // Lesson 1: strikers vs one defending goalie.
            return striker || (agent.team != team && agent.position == AgentSoccer.Position.Goalie);
        return true;       // Lesson 2: full 3v3.
    }
}
