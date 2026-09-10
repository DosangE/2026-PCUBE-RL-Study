using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// 주방 한 세트(TrainingArea)의 그리드 / 스테이션 / 조리 / 에피소드 타이머를 관리한다.
// TrainingArea 루트 오브젝트에 붙인다.
// 모든 좌표 계산은 이 오브젝트 기준 로컬 좌표다 -> 16개를 복제해도 관측이 동일하다.
public class KitchenEnv : MonoBehaviour
{
    // --- 레이아웃 문자 ---
    const char CharWall = '#';
    const char CharFloor = '.';
    const char CharSpawnA = 'A';
    const char CharSpawnB = 'B';
    const char CharCounter = 'C';
    const char CharIngredientBox = 'I';
    const char CharPot = 'P';
    const char CharPlateStack = 'D';
    const char CharServingHatch = 'S';

    // 맵 레이아웃. 배열 0번이 맵의 '위'(북쪽, row 최대)다. 파싱할 때 뒤집는다.
    //   # = 벽        . = 바닥
    //   A = ChefA 스폰  B = ChefB 스폰   C = 카운터 전달칸
    //   I = 재료함     P = 냄비        D = 그릇함   S = 서빙구
    //
    // 카운터 행(C가 있는 행)이 두 구역을 완전히 갈라놓는다.
    // 걸어서 넘어갈 수 없으므로 물건은 오직 카운터를 경유해야 한다 = 협동 강제.
    static readonly string[] LayoutTopDown =
    {
        "#######", // row 6
        "#.....#", // row 5
        "I..A..P", // row 4  <- Chef A 구역 (재료함 / 냄비)
        "#CCC###", // row 3  <- 중앙 카운터
        "D..B..S", // row 2  <- Chef B 구역 (그릇함 / 서빙구)
        "#.....#", // row 1
        "#######", // row 0
    };

    // 행동 branch0의 1~4번(상/하/좌/우)에 대응하는 방향 벡터.
    // facing 인덱스(관측 one-hot 4)도 같은 순서를 쓴다.
    public static readonly Vector2Int[] Directions =
    {
        new Vector2Int(0, 1),   // 0 North (+z)
        new Vector2Int(0, -1),  // 1 South (-z)
        new Vector2Int(-1, 0),  // 2 West  (-x)
        new Vector2Int(1, 0),   // 3 East  (+x)
    };

    public const int ZoneBlocked = -1;
    public const int AgentCount = 2;

    [Header("그리드")]
    [SerializeField] float cellSize = 1f;
    [SerializeField] float agentY = 0.5f;

    [Header("요리 규칙")]
    [SerializeField] int ingredientsPerSoup = 3;
    [Tooltip("EnvironmentParameters의 cook_time이 없을 때 쓰는 값")]
    [SerializeField] float defaultCookTime = 5f;
    [Tooltip("EnvironmentParameters의 target_soups가 없을 때 쓰는 값")]
    [SerializeField] int defaultTargetSoups = 3;

    [Header("에피소드")]
    [SerializeField] float episodeDuration = 30f;
    [Tooltip("끄면 항상 레이아웃의 A/B 마커 위치에서 시작한다 (디버깅용)")]
    [SerializeField] bool randomizeSpawn = true;

    int m_GridWidth;
    int m_GridHeight;
    int m_CounterRow;

    int[,] m_ZoneGrid;              // ZoneBlocked = 이동 불가, 0 = 북(ChefA), 1 = 남(ChefB)
    Station[,] m_StationGrid;       // 그 칸의 스테이션, 없으면 null
    List<Vector2Int>[] m_WalkableCells;
    Vector2Int[] m_DefaultSpawnCells;

    readonly List<Station> m_Counters = new List<Station>();
    readonly Dictionary<StationType, Station> m_TypedStations = new Dictionary<StationType, Station>();

    float m_CookTime;
    int m_TargetSoups;
    float m_EpisodeTimer;
    bool m_Initialized;

    public int GridWidth => m_GridWidth;
    public int GridHeight => m_GridHeight;
    public float CellSize => cellSize;
    public int IngredientsPerSoup => ingredientsPerSoup;

    public int TargetSoups => m_TargetSoups;
    public int SoupsServed { get; private set; }
    public bool IsGoalReached => SoupsServed >= m_TargetSoups;

    public bool IsTimeUp => m_EpisodeTimer >= episodeDuration;
    public float TimeRemainingNormalized => Mathf.Clamp01(1f - m_EpisodeTimer / episodeDuration);

    public IReadOnlyList<Station> Counters => m_Counters;
    public Station Pot => GetStation(StationType.Pot);

    void Awake()
    {
        BuildGrid();
        RegisterStations();
        m_Initialized = true;
    }

    void Start()
    {
        // 트레이너 없이 에디터에서 그냥 Play(휴리스틱 플레이) 해도 동작하도록 한 번 초기화한다.
        ResetEnv();
    }

    void FixedUpdate()
    {
        if (!m_Initialized) return;

        m_EpisodeTimer += Time.fixedDeltaTime;

        var pot = Pot;
        if (pot != null) pot.TickCooking(Time.fixedDeltaTime, m_CookTime);
    }

    // ─────────────────────────── 초기화 ───────────────────────────

    void BuildGrid()
    {
        m_GridHeight = LayoutTopDown.Length;
        m_GridWidth = LayoutTopDown[0].Length;
        m_ZoneGrid = new int[m_GridWidth, m_GridHeight];
        m_StationGrid = new Station[m_GridWidth, m_GridHeight];

        m_WalkableCells = new List<Vector2Int>[AgentCount];
        m_DefaultSpawnCells = new Vector2Int[AgentCount];
        for (int i = 0; i < AgentCount; i++)
        {
            m_WalkableCells[i] = new List<Vector2Int>();
            m_DefaultSpawnCells[i] = new Vector2Int(-1, -1);
        }

        // 카운터가 놓인 행이 두 구역을 가르는 경계다.
        m_CounterRow = -1;
        for (int line = 0; line < m_GridHeight; line++)
        {
            if (LayoutTopDown[line].IndexOf(CharCounter) < 0) continue;
            m_CounterRow = (m_GridHeight - 1) - line;
            break;
        }
        if (m_CounterRow < 0)
        {
            Debug.LogError($"[{name}] 레이아웃에 카운터('{CharCounter}') 행이 없다.");
            return;
        }

        for (int row = 0; row < m_GridHeight; row++)
        {
            for (int col = 0; col < m_GridWidth; col++)
            {
                var cell = new Vector2Int(col, row);
                char c = LayoutCharAt(cell);
                int zone = ZoneBlocked;

                if (c == CharFloor || c == CharSpawnA || c == CharSpawnB)
                {
                    zone = row > m_CounterRow ? 0 : 1;
                    m_WalkableCells[zone].Add(cell);

                    if (c == CharSpawnA) m_DefaultSpawnCells[0] = cell;
                    else if (c == CharSpawnB) m_DefaultSpawnCells[1] = cell;
                }

                m_ZoneGrid[col, row] = zone;
            }
        }

        // 스폰 마커가 빠졌으면 그 구역의 첫 칸으로 대체한다.
        for (int i = 0; i < AgentCount; i++)
        {
            if (m_DefaultSpawnCells[i].x >= 0) continue;
            if (m_WalkableCells[i].Count == 0)
            {
                Debug.LogError($"[{name}] {i}번 구역에 걸을 수 있는 칸이 하나도 없다.");
                continue;
            }
            m_DefaultSpawnCells[i] = m_WalkableCells[i][0];
        }
    }

    // 씬에 배치된 Station들을 로컬 좌표로부터 그리드에 등록하고, 레이아웃과 어긋나면 에러를 낸다.
    // 씬과 코드가 따로 노는 사고(제일 잡기 어려운 종류)를 여기서 즉시 잡는다.
    void RegisterStations()
    {
        m_Counters.Clear();
        m_TypedStations.Clear();

        foreach (var station in GetComponentsInChildren<Station>(true))
        {
            Vector3 local = transform.InverseTransformPoint(station.transform.position);
            Vector2Int cell = LocalPositionToCell(local);

            if (!IsInsideGrid(cell))
            {
                Debug.LogError($"[{name}] {station.name} 이(가) 그리드 밖({cell})에 있다.");
                continue;
            }
            if (!TryLayoutStationType(LayoutCharAt(cell), out var expected))
            {
                Debug.LogError($"[{name}] {station.name} 의 칸 {cell} 은 레이아웃상 스테이션 자리가 아니다.");
                continue;
            }
            if (expected != station.Type)
            {
                Debug.LogError($"[{name}] {station.name} 타입 불일치: 레이아웃={expected}, 컴포넌트={station.Type}");
                continue;
            }

            station.Initialize(cell, ingredientsPerSoup);
            m_StationGrid[cell.x, cell.y] = station;

            if (station.Type == StationType.Counter) m_Counters.Add(station);
            else m_TypedStations[station.Type] = station;
        }

        // 복제한 16개 환경이 같은 순서로 관측하도록 셀 기준으로 정렬한다.
        m_Counters.Sort((a, b) => a.Cell.x != b.Cell.x ? a.Cell.x - b.Cell.x : a.Cell.y - b.Cell.y);

        // 레이아웃이 요구하는 스테이션이 전부 붙어 있는지 확인한다.
        for (int row = 0; row < m_GridHeight; row++)
        {
            for (int col = 0; col < m_GridWidth; col++)
            {
                var cell = new Vector2Int(col, row);
                if (!TryLayoutStationType(LayoutCharAt(cell), out _)) continue;
                if (m_StationGrid[col, row] != null) continue;
                Debug.LogError($"[{name}] 셀 {cell} 에 Station 컴포넌트가 없다. 씬 오브젝트를 확인할 것.");
            }
        }
    }

    char LayoutCharAt(Vector2Int cell)
    {
        return LayoutTopDown[(m_GridHeight - 1) - cell.y][cell.x];
    }

    static bool TryLayoutStationType(char c, out StationType type)
    {
        switch (c)
        {
            case CharIngredientBox: type = StationType.IngredientBox; return true;
            case CharPot:           type = StationType.Pot;           return true;
            case CharPlateStack:    type = StationType.PlateStack;    return true;
            case CharServingHatch:  type = StationType.ServingHatch;  return true;
            case CharCounter:       type = StationType.Counter;       return true;
            default:                type = StationType.Counter;       return false;
        }
    }

    // ─────────────────────────── 리셋 ───────────────────────────

    // 에피소드 시작마다 KitchenGroup이 호출한다.
    public void ResetEnv()
    {
        var envParams = Academy.Instance.EnvironmentParameters;
        m_TargetSoups = Mathf.Max(1, Mathf.RoundToInt(envParams.GetWithDefault("target_soups", defaultTargetSoups)));
        m_CookTime = Mathf.Max(0f, envParams.GetWithDefault("cook_time", defaultCookTime));

        foreach (var station in GetComponentsInChildren<Station>(true)) station.ResetState();

        SoupsServed = 0;
        m_EpisodeTimer = 0f;
    }

    public Vector2Int GetSpawnCell(int agentIndex)
    {
        if (!randomizeSpawn) return m_DefaultSpawnCells[agentIndex];

        var cells = m_WalkableCells[agentIndex];
        return cells.Count == 0 ? m_DefaultSpawnCells[agentIndex] : cells[Random.Range(0, cells.Count)];
    }

    // ─────────────────────────── 좌표 변환 ───────────────────────────

    // 셀 (col,row) -> TrainingArea 기준 로컬 좌표. 그리드 중앙이 로컬 원점이 된다.
    public Vector3 CellToLocalPosition(Vector2Int cell)
    {
        float ox = (m_GridWidth - 1) * 0.5f;
        float oz = (m_GridHeight - 1) * 0.5f;
        return new Vector3((cell.x - ox) * cellSize, agentY, (cell.y - oz) * cellSize);
    }

    public Vector2Int LocalPositionToCell(Vector3 local)
    {
        float ox = (m_GridWidth - 1) * 0.5f;
        float oz = (m_GridHeight - 1) * 0.5f;
        return new Vector2Int(
            Mathf.RoundToInt(local.x / cellSize + ox),
            Mathf.RoundToInt(local.z / cellSize + oz));
    }

    // 관측용: 셀 좌표를 -1~1 범위로 정규화한다.
    public Vector2 NormalizeCell(Vector2Int cell)
    {
        float ox = (m_GridWidth - 1) * 0.5f;
        float oz = (m_GridHeight - 1) * 0.5f;
        return new Vector2((cell.x - ox) / ox, (cell.y - oz) / oz);
    }

    // ─────────────────────────── 질의 ───────────────────────────

    public bool IsInsideGrid(Vector2Int cell)
    {
        return cell.x >= 0 && cell.x < m_GridWidth && cell.y >= 0 && cell.y < m_GridHeight;
    }

    // 그 에이전트가 이 칸으로 '이동'할 수 있는가. 자기 구역의 바닥만 true.
    public bool IsWalkable(Vector2Int cell, int agentIndex)
    {
        return IsInsideGrid(cell) && m_ZoneGrid[cell.x, cell.y] == agentIndex;
    }

    public Station GetStationAt(Vector2Int cell)
    {
        return IsInsideGrid(cell) ? m_StationGrid[cell.x, cell.y] : null;
    }

    public Station GetStation(StationType type)
    {
        return m_TypedStations.TryGetValue(type, out var station) ? station : null;
    }

    // Action Masking용: 이 칸을 향해 Interact 하면 뭐라도 일어나는가.
    public bool CanInteractAt(Vector2Int cell, ItemType heldItem)
    {
        var station = GetStationAt(cell);
        return station != null && station.CanInteract(heldItem);
    }

    // ─────────────────────────── 상호작용 ───────────────────────────

    // 에이전트가 cell 을 향해 Interact 했을 때. 실제 상태 변경까지 여기서 일어난다.
    public InteractOutcome TryInteract(int agentIndex, Vector2Int cell, ItemType heldItem)
    {
        var outcome = new InteractOutcome
        {
            Result = InteractResult.Nothing,
            NewHeldItem = heldItem,
            TransferPartnerIndex = -1
        };

        var station = GetStationAt(cell);
        if (station == null) return outcome;

        // Interact가 CounterPlacedBy를 지워버리므로 미리 받아둔다.
        int placedBy = station.Type == StationType.Counter ? station.CounterPlacedBy : -1;

        outcome.Result = station.Interact(agentIndex, heldItem, out var newHeld);
        outcome.NewHeldItem = newHeld;

        if (outcome.Result == InteractResult.TookFromCounter) outcome.TransferPartnerIndex = placedBy;
        if (outcome.Result == InteractResult.Served) SoupsServed++;

        return outcome;
    }
}
