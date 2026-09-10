using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// TrainingArea 하나의 셰프 둘을 한 팀으로 묶는다 (MA-POCA).
// 팀 보상 지급, 에피소드 종료/중단, 씬 리셋을 담당한다.
// KitchenEnv와 같은 오브젝트(TrainingArea 루트)에 붙인다.
[RequireComponent(typeof(KitchenEnv))]
public class KitchenGroup : MonoBehaviour
{
    [Tooltip("비워두면 같은 오브젝트에서 찾는다")]
    [SerializeField] KitchenEnv env;

    [Tooltip("비워두면 자식에서 ChefAgent를 전부 찾아 AgentIndex 순으로 정렬한다")]
    [SerializeField] List<ChefAgent> agents = new List<ChefAgent>();

    [Header("팀 보상 (AddGroupReward)")]
    [SerializeField] float rewardServe = 3.0f;
    [SerializeField] float rewardIngredientInPot = 0.3f;
    [SerializeField] float rewardGoalBonus = 2.0f;

    SimpleMultiAgentGroup m_Group;
    bool m_Ready;

    void Awake()
    {
        if (env == null) env = GetComponent<KitchenEnv>();

        if (agents.Count == 0) agents.AddRange(GetComponentsInChildren<ChefAgent>(true));
        agents.Sort((a, b) => a.AgentIndex - b.AgentIndex);

        if (agents.Count != KitchenEnv.AgentCount)
        {
            Debug.LogError($"[{name}] ChefAgent가 {agents.Count}개다. {KitchenEnv.AgentCount}개여야 한다.");
            return;
        }

        m_Group = new SimpleMultiAgentGroup();
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].AgentIndex != i)
                Debug.LogError($"[{name}] {agents[i].name} 의 AgentIndex가 {agents[i].AgentIndex}다. 0,1로 하나씩 배정할 것.");

            var partner = agents[(i + 1) % agents.Count];
            agents[i].Bind(this, env, partner);
            m_Group.RegisterAgent(agents[i]);
        }

        m_Ready = true;
    }

    void Start()
    {
        ResetScene();
    }

    void FixedUpdate()
    {
        if (!m_Ready) return;

        if (env.IsGoalReached)
        {
            // 목표 수프 개수 달성 -> 성공 종료
            m_Group.AddGroupReward(rewardGoalBonus);
            m_Group.EndGroupEpisode();
            ResetScene();
            return;
        }

        if (env.IsTimeUp)
        {
            // 타임아웃은 '실패'가 아니라 '중단'이다.
            // EndGroupEpisode로 끊으면 부트스트랩 없이 가치가 0으로 잘려서
            // value function이 "시간이 지나면 가치가 0" 이라고 잘못 배운다.
            m_Group.GroupEpisodeInterrupted();
            ResetScene();
        }
    }

    // ─────────────────────────── 팀 보상 ───────────────────────────

    public void OnIngredientPlacedInPot()
    {
        if (m_Group != null) m_Group.AddGroupReward(rewardIngredientInPot);
    }

    public void OnSoupServed()
    {
        if (m_Group != null) m_Group.AddGroupReward(rewardServe);
    }

    // 카운터 전달이 성립했을 때 '놓은 쪽'에 소급해서 주는 개인 보상.
    public void AwardPersonalReward(int agentIndex, float amount)
    {
        if (agentIndex < 0 || agentIndex >= agents.Count) return;
        agents[agentIndex].AddReward(amount);
    }

    // ─────────────────────────── 리셋 ───────────────────────────

    void ResetScene()
    {
        env.ResetEnv();
        foreach (var agent in agents) agent.ResetAgentState();
    }
}
