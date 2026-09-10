using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// 셰프 한 명. 관측 / 행동 / Action Masking / 개인 보상을 담당한다.
// 팀 보상과 에피소드 종료/중단은 KitchenGroup이 처리한다.
public class ChefAgent : Agent
{
    // ── 관측 차원 ─────────────────────────────────────────────
    // Behavior Parameters의 Vector Observation Space Size와 반드시 같아야 한다.
    //   자기 위치 정규화            2
    //   바라보는 방향 one-hot        4
    //   손에 든 것 one-hot           4
    //   동료 상대좌표                2
    //   동료 손 one-hot              4
    //   냄비 재료수 / 완성 플래그    2   (조리 진행도는 일부러 제외 -> RNN이 기억)
    //   카운터 3칸 x (one-hot 4 + 상대좌표 2) = 18
    //   스테이션 4곳 상대좌표        8
    //   남은 시간                    1
    //                          합계 45
    public const int ObservationSize = 45;

    // 관측 크기를 고정하려고 슬롯 수를 상수로 박는다.
    // 실제 카운터가 이보다 적으면 0으로 채우고, 많으면 앞에서부터 잘라 쓴다.
    public const int CounterObservationSlots = 3;

    const int ItemTypeCount = 4;
    const int DirectionCount = 4;

    [SerializeField] int agentIndex = 0;   // 0 = ChefA(북쪽 구역), 1 = ChefB(남쪽 구역)

    [Tooltip("손에 든 것을 보여줄 큐브의 Renderer (없어도 동작한다)")]
    [SerializeField] Renderer heldItemRenderer;

    [Header("개인 보상")]
    [SerializeField] float rewardPickFromSource = 0.05f;
    [SerializeField] float rewardTransfer = 0.15f;
    [SerializeField] float rewardWasted = -0.2f;
    [SerializeField] float rewardPerStep = -0.002f;

    [Header("손에 든 것 표시 색")]
    [SerializeField] Color colorIngredient = new Color(0.20f, 0.80f, 0.25f);
    [SerializeField] Color colorEmptyPlate = new Color(0.95f, 0.95f, 0.95f);
    [SerializeField] Color colorCookedSoup = new Color(1.00f, 0.55f, 0.10f);

    KitchenEnv m_Env;
    KitchenGroup m_Group;
    ChefAgent m_Partner;
    BehaviorParameters m_Behavior;

    Vector2Int m_Cell;
    int m_Facing;                 // KitchenEnv.Directions 인덱스 (0=북 1=남 2=서 3=동)
    ItemType m_HeldItem = ItemType.None;
    bool m_InteractQueued;        // Heuristic 키 입력 래치

    public int AgentIndex => agentIndex;
    public Vector2Int Cell => m_Cell;
    public ItemType HeldItem => m_HeldItem;

    public override void Initialize()
    {
        m_Behavior = GetComponent<BehaviorParameters>();
        EnsureBound();
    }

    // KitchenGroup이 등록할 때 호출한다.
    public void Bind(KitchenGroup group, KitchenEnv env, ChefAgent partner)
    {
        m_Group = group;
        m_Env = env;
        m_Partner = partner;
    }

    // Awake/OnEnable 순서에 관계없이 안전하게 참조를 확보한다.
    void EnsureBound()
    {
        if (m_Env == null) m_Env = GetComponentInParent<KitchenEnv>();
        if (m_Group == null) m_Group = GetComponentInParent<KitchenGroup>();
    }

    // ─────────────────────────── 에피소드 ───────────────────────────

    // 에피소드 리셋은 KitchenGroup이 환경 리셋 직후에 한 번에 처리한다.
    // (OnEpisodeBegin은 EndGroupEpisode 시점에 불려서 환경 리셋보다 먼저 실행된다)
    public override void OnEpisodeBegin() { }

    public void ResetAgentState()
    {
        EnsureBound();
        if (m_Env == null) return;

        m_Cell = m_Env.GetSpawnCell(agentIndex);
        // 시작 시선은 중앙 카운터 쪽. ChefA(북쪽)는 남쪽을, ChefB(남쪽)는 북쪽을 본다.
        m_Facing = agentIndex == 0 ? 1 : 0;
        m_HeldItem = ItemType.None;
        m_InteractQueued = false;

        ApplyTransform();
        UpdateHeldVisual();
    }

    // ─────────────────────────── 관측 ───────────────────────────

    public override void CollectObservations(VectorSensor sensor)
    {
        EnsureBound();
        if (m_Env == null)
        {
            // 관측 개수는 어떤 경우에도 고정되어야 한다.
            for (int i = 0; i < ObservationSize; i++) sensor.AddObservation(0f);
            return;
        }

        // 1) 자기 위치 (2)
        sensor.AddObservation(m_Env.NormalizeCell(m_Cell));

        // 2) 바라보는 방향 one-hot (4)
        sensor.AddOneHotObservation(m_Facing, DirectionCount);

        // 3) 손에 든 것 one-hot (4)
        sensor.AddOneHotObservation((int)m_HeldItem, ItemTypeCount);

        // 4) 동료 상대좌표 (2) + 5) 동료 손 one-hot (4)
        if (m_Partner != null)
        {
            sensor.AddObservation(RelativeToMe(m_Partner.Cell));
            sensor.AddOneHotObservation((int)m_Partner.HeldItem, ItemTypeCount);
        }
        else
        {
            sensor.AddObservation(Vector2.zero);
            sensor.AddOneHotObservation(0, ItemTypeCount);
        }

        // 6) 냄비 상태 (2). 조리 진행도는 일부러 빼둔다 -> Memory(RNN)가 필요한 이유.
        var pot = m_Env.Pot;
        sensor.AddObservation(pot != null ? (float)pot.IngredientCount / pot.PotCapacity : 0f);
        sensor.AddObservation(pot != null && pot.HasCookedSoup ? 1f : 0f);

        // 7) 카운터 슬롯 (18)
        var counters = m_Env.Counters;
        for (int i = 0; i < CounterObservationSlots; i++)
        {
            if (i < counters.Count)
            {
                sensor.AddOneHotObservation((int)counters[i].CounterItem, ItemTypeCount);
                sensor.AddObservation(RelativeToMe(counters[i].Cell));
            }
            else
            {
                sensor.AddOneHotObservation(0, ItemTypeCount);
                sensor.AddObservation(Vector2.zero);
            }
        }

        // 8) 스테이션 4곳 상대좌표 (8)
        AddStationRelative(sensor, StationType.IngredientBox);
        AddStationRelative(sensor, StationType.Pot);
        AddStationRelative(sensor, StationType.PlateStack);
        AddStationRelative(sensor, StationType.ServingHatch);

        // 9) 남은 시간 (1)
        sensor.AddObservation(m_Env.TimeRemainingNormalized);
    }

    void AddStationRelative(VectorSensor sensor, StationType type)
    {
        var station = m_Env.GetStation(type);
        sensor.AddObservation(station != null ? RelativeToMe(station.Cell) : Vector2.zero);
    }

    // 상대 좌표를 그리드 크기로 나눠 -1~1로 만든다. 절대 좌표는 쓰지 않는다.
    Vector2 RelativeToMe(Vector2Int cell)
    {
        return new Vector2(
            (float)(cell.x - m_Cell.x) / Mathf.Max(1, m_Env.GridWidth - 1),
            (float)(cell.y - m_Cell.y) / Mathf.Max(1, m_Env.GridHeight - 1));
    }

    // ─────────────────────────── Action Masking ───────────────────────────

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        EnsureBound();
        if (m_Env == null) return;

        // Branch 0 (이동): 0=정지는 절대 막지 않는다.
        // 이동할 수 없는 방향이라도 그쪽에 스테이션이 있으면 '회전'이라는 의미가 남는다.
        // 이걸 막아버리면 스테이션을 쳐다볼 수 없어서 Interact가 영영 불가능해진다.
        for (int dir = 0; dir < DirectionCount; dir++)
        {
            var target = m_Cell + KitchenEnv.Directions[dir];
            bool canMove = m_Env.IsWalkable(target, agentIndex);
            bool canTurn = !canMove && m_Env.GetStationAt(target) != null && m_Facing != dir;
            if (!canMove && !canTurn) actionMask.SetActionEnabled(0, dir + 1, false);
        }

        // Branch 1 (Interact): 앞에 지금 손 상태로 할 수 있는 게 없으면 막는다.
        // 손이 찼는데 재료함/그릇함, 손이 비었는데 빈 카운터, 조리중인 냄비 등이 전부 여기서 걸린다.
        var front = m_Cell + KitchenEnv.Directions[m_Facing];
        if (!m_Env.CanInteractAt(front, m_HeldItem)) actionMask.SetActionEnabled(1, 1, false);
    }

    // ─────────────────────────── 행동 ───────────────────────────

    public override void OnActionReceived(ActionBuffers actions)
    {
        EnsureBound();
        if (m_Env == null) return;

        AddReward(rewardPerStep);

        int move = actions.DiscreteActions[0];      // 0=정지 1=상 2=하 3=좌 4=우
        int interact = actions.DiscreteActions[1];  // 0=없음 1=Interact

        if (move > 0)
        {
            int dir = move - 1;
            m_Facing = dir;                         // 이동 방향이 곧 시선 방향
            var target = m_Cell + KitchenEnv.Directions[dir];
            if (m_Env.IsWalkable(target, agentIndex)) m_Cell = target;   // 못 가면 회전만
            ApplyTransform();
        }

        if (interact == 1) DoInteract();
    }

    void DoInteract()
    {
        var front = m_Cell + KitchenEnv.Directions[m_Facing];
        var outcome = m_Env.TryInteract(agentIndex, front, m_HeldItem);

        m_HeldItem = outcome.NewHeldItem;
        UpdateHeldVisual();

        switch (outcome.Result)
        {
            case InteractResult.PickedFromSource:
                AddReward(rewardPickFromSource);
                break;

            case InteractResult.PlacedInPot:
                if (m_Group != null) m_Group.OnIngredientPlacedInPot();
                break;

            case InteractResult.TookFromCounter:
                // 전달이 '성립'한 순간에만 보상을 준다. 집은 쪽과 놓은 쪽 둘 다.
                // 올려놓는 순간에 주면 혼자 놓았다 집었다를 반복해서 보상을 긁을 수 있다.
                AddReward(rewardTransfer);
                if (m_Group != null) m_Group.AwardPersonalReward(outcome.TransferPartnerIndex, rewardTransfer);
                break;

            case InteractResult.Served:
                if (m_Group != null) m_Group.OnSoupServed();
                break;

            case InteractResult.Wasted:
                AddReward(rewardWasted);
                break;

            // TookSoupFromPot / PlacedOnCounter / TookOwnFromCounter / Nothing 은 보상 없음
        }
    }

    // ─────────────────────────── 표현 ───────────────────────────

    void ApplyTransform()
    {
        transform.localPosition = m_Env.CellToLocalPosition(m_Cell);
        transform.localRotation = Quaternion.Euler(0f, FacingYaw(m_Facing), 0f);
    }

    static float FacingYaw(int facing)
    {
        switch (facing)
        {
            case 0: return 0f;     // 북 (+z)
            case 1: return 180f;   // 남 (-z)
            case 2: return 270f;   // 서 (-x)
            default: return 90f;   // 동 (+x)
        }
    }

    void UpdateHeldVisual()
    {
        if (heldItemRenderer == null) return;

        bool visible = m_HeldItem != ItemType.None;
        heldItemRenderer.gameObject.SetActive(visible);
        if (!visible) return;

        switch (m_HeldItem)
        {
            case ItemType.Ingredient: heldItemRenderer.material.color = colorIngredient; break;
            case ItemType.EmptyPlate: heldItemRenderer.material.color = colorEmptyPlate; break;
            default: heldItemRenderer.material.color = colorCookedSoup; break;
        }
    }

    // ─────────────────────────── Heuristic (키보드 플레이) ───────────────────────────
    //   ChefA : WASD 이동 / LeftShift Interact
    //   ChefB : 방향키 이동 / RightShift(또는 Enter) Interact
    // 사람이 30초 안에 클리어 가능한지 직접 확인하는 용도다. 이 검증을 건너뛰지 말 것.

    void Update()
    {
        if (m_Behavior == null || m_Behavior.BehaviorType != BehaviorType.HeuristicOnly) return;
        // Decision Period(5) 사이에 눌린 키를 놓치지 않도록 래치해둔다.
        if (InteractKeyDown()) m_InteractQueued = true;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discrete = actionsOut.DiscreteActions;
        discrete[0] = 0;
        discrete[1] = 0;

        if (MoveUpHeld()) discrete[0] = 1;
        else if (MoveDownHeld()) discrete[0] = 2;
        else if (MoveLeftHeld()) discrete[0] = 3;
        else if (MoveRightHeld()) discrete[0] = 4;

        if (m_InteractQueued)
        {
            discrete[1] = 1;
            m_InteractQueued = false;
        }
    }

#if ENABLE_INPUT_SYSTEM
    bool MoveUpHeld() => KeyHeld(agentIndex == 0 ? Key.W : Key.UpArrow);
    bool MoveDownHeld() => KeyHeld(agentIndex == 0 ? Key.S : Key.DownArrow);
    bool MoveLeftHeld() => KeyHeld(agentIndex == 0 ? Key.A : Key.LeftArrow);
    bool MoveRightHeld() => KeyHeld(agentIndex == 0 ? Key.D : Key.RightArrow);

    bool InteractKeyDown()
    {
        var kb = Keyboard.current;
        if (kb == null) return false;
        return agentIndex == 0
            ? kb.leftShiftKey.wasPressedThisFrame
            : kb.rightShiftKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame;
    }

    static bool KeyHeld(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].isPressed;
    }
#else
    bool MoveUpHeld() => Input.GetKey(agentIndex == 0 ? KeyCode.W : KeyCode.UpArrow);
    bool MoveDownHeld() => Input.GetKey(agentIndex == 0 ? KeyCode.S : KeyCode.DownArrow);
    bool MoveLeftHeld() => Input.GetKey(agentIndex == 0 ? KeyCode.A : KeyCode.LeftArrow);
    bool MoveRightHeld() => Input.GetKey(agentIndex == 0 ? KeyCode.D : KeyCode.RightArrow);

    bool InteractKeyDown()
    {
        return agentIndex == 0
            ? Input.GetKeyDown(KeyCode.LeftShift)
            : Input.GetKeyDown(KeyCode.RightShift) || Input.GetKeyDown(KeyCode.Return);
    }
#endif
}
