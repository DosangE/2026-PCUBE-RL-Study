# UnderCooked — MA-POCA 2인 협동 요리

Overcooked를 극단적으로 단순화한 2인 협동 요리 환경.
두 셰프가 카운터를 사이에 두고 재료와 그릇을 주고받아 수프를 만들어 서빙한다.
**MA-POCA**로 팀 단위 협동 정책을 학습시킨다.

> 상태: 환경 구축 완료, 학습 대기. 결과 섹션은 학습 후 채운다.

---

## 1. 게임 규칙

재료는 1종류. 냄비에 3개 넣으면 자동 조리가 시작된다.
파이프라인은 5단계 고정이다.

```
재료 줍기 → 냄비에 넣기 → 자동 조리(N초) → 그릇에 담기 → 서빙구에 제출
```

에피소드는 30초(약 300 decision) 타임아웃. 목표 수프 개수를 채우면 성공 종료.

### 맵

```
         col0   col1   col2   col3   col4   col5   col6
 row6     #      #      #      #      #      #      #
 row5     #      .      .      .      .      .      #     ┐ Chef A 구역
 row4     #      .      .      .      .      .    [POT]   ┘ (냄비 전담)
 row3     #      C      C      C      #      #      #     ← 중앙 카운터
 row2    [PS]    .      .      .      .      .    [SH]    ┐ Chef B 구역
 row1     #      .      .      .      .      .      #     ┘ (재료함/그릇함/서빙구)
 row0     #      #      #     [IB]    #      #      #

#  벽      .  바닥      C  카운터 전달칸(3개)
IB 재료함   POT 냄비    PS 그릇함    SH 서빙구
```

**협동은 맵 구조로 강제된다.** row3(카운터 행)이 두 구역을 물리적으로 완전히
분리해서 걸어서 넘어갈 수 없다. 재료도 그릇도 전부 B 구역에 있고 냄비만 A 구역에
있으므로, 수프 하나를 만들려면 **반드시 카운터를 5번 경유**해야 한다.

| 전달 | 방향 | 횟수 |
|---|---|---|
| 재료 | B → A | 3 |
| 빈 그릇 | B → A | 1 |
| 완성 수프 | A → B | 1 |

혼자 다 하는 정책은 물리적으로 불가능하다.

---

## 2. 환경 설계

### 관측 — 44차원 (전부 상대좌표, `normalize: true`)

| 항목 | 차원 |
|---|---|
| 자기 위치 정규화 | 2 |
| 바라보는 방향 one-hot | 4 |
| 손에 든 것 one-hot (`None/Ingredient/EmptyPlate/CookedSoup`) | 4 |
| 동료 상대좌표 | 2 |
| 동료 손에 든 것 one-hot | 4 |
| 냄비 재료 개수 정규화 | 1 |
| 카운터 3칸 × (내용물 one-hot 4 + 상대좌표 2) | 18 |
| 스테이션 4곳 상대좌표 | 8 |
| 남은 시간 정규화 | 1 |
| **합계** | **44** |

**조리가 끝났는지는 관측에 넣지 않는다.** 재료를 언제 다 넣었는지 기억해서
스스로 추정해야 한다 — Memory(RNN)를 쓰는 근거다. (§3 참조)

### 행동 — Discrete 2 branch

- Branch 0 (5): `정지 / 상 / 하 / 좌 / 우`
- Branch 1 (2): `없음 / Interact`

이동 방향이 곧 시선 방향이다. 목표 칸이 자기 구역의 바닥이면 이동하고,
아니면 **회전만** 한다. (벽 방향 이동을 전부 막으면 스테이션을 쳐다볼 수 없어서
Interact가 영영 불가능해진다.)

### Action Masking

- 이동: 이동도 회전도 무의미한 방향만 마스킹. `정지`는 절대 막지 않는다.
- Interact: 바라보는 칸에서 지금 손 상태로 할 수 있는 게 없으면 마스킹.
  손이 찼는데 재료함, 손이 비었는데 빈 카운터, 재료를 든 채 가득 찬 냄비 등이 여기 걸린다.

### 보상

**팀** (`AddGroupReward`)

| 이벤트 | 값 |
|---|---|
| 수프 서빙 성공 | +3.0 |
| 목표 수프 개수 달성 | +2.0 |
| 재료를 냄비에 투입 | +0.3 |

**개인** (`AddReward`)

| 이벤트 | 값 |
|---|---|
| 카운터 전달 성립 (놓은 쪽·집은 쪽 **둘 다**) | +0.15 |
| 재료함/그릇함에서 집기 | +0.05 |
| 조리 전에 수프를 뜨려 함 (헛도리) | -0.02 |
| 서빙구에 엉뚱한 물건 버림 | -0.2 |
| 매 스텝 | -0.002 |

### 에피소드 종료

- 목표 달성 → `EndGroupEpisode()`
- 30초 경과 → **`GroupEpisodeInterrupted()`**

타임아웃을 `EndGroupEpisode()`로 끊으면 부트스트랩 없이 가치가 0으로 잘려서
value function이 "시간이 지나면 가치가 0"이라고 잘못 배운다.
Agent의 `MaxStep`은 0이고, 에피소드 제어는 전적으로 `KitchenGroup`이 한다.

---

## 3. 사용한 필수 개념 4가지

### ⑥ MA-POCA + Team ID
`KitchenGroup`이 `SimpleMultiAgentGroup`으로 두 셰프를 묶는다.
Behavior Name `Chef`, Team ID 둘 다 0. 서빙 성공은 팀 보상이므로
"누구 덕분에 점수가 났는지"를 POCA가 분배해야 한다.

### ⑦ Action Masking
`WriteDiscreteActionMask`에서 매 스텝 적용. 불가능한 이동과 무의미한 상호작용을
잘라내 탐색 공간을 줄인다.

### ① Memory / RNN
`network_settings.memory` (sequence_length 64, memory_size 128).
조리 완료 여부를 관측에서 빼고 **Action Mask로도 새지 않게 막아서**,
"재료를 언제 다 넣었는가"를 기억해야만 낭비 없이 수프를 뜰 수 있게 했다.
덜 끓었을 때 뜨면 -0.02를 문다.

### ⑤ Curriculum Learning
- `target_soups`: 1 → 2 → 3
- `cook_time`: 2초 → 5초

조리 시간이 길어질수록 **기억해야 할 시간 길이가 늘어난다.**
①과 ⑤가 맞물려 있다.

---

## 4. 설계 과정에서 고친 것

환경 설계에서 실제로 학습을 망가뜨렸을 문제들과 그 근거.

### 4-1. 카운터 보상 어뷰징 2건

**(a) 자기 물건 회수 루프.** 최초 명세는 "카운터에 올림 +0.15"였다.
혼자 놓았다 집었다를 반복하면 2스텝당 +0.15를 무한히 긁을 수 있었다.

| 전략 | decision당 |
|---|---|
| 놓기/집기 반복 | +0.146 |
| 정직한 플레이 | +0.075 |

→ 올릴 때는 0, **동료가 집어간 순간에 양쪽 다 +0.15**로 변경.

**(b) A↔B 핑퐁.** (a)를 고친 뒤에도, A가 놓고 B가 집고 B가 놓고 A가 집는 것만
반복하면 매 전달마다 양쪽이 +0.15를 받았다(`CounterPlacedBy`가 매번 바뀌므로
항상 "동료 물건"). decision당 +0.073으로 정직한 플레이(+0.048)보다 높았다.

→ 전달 보상에 **"받는 쪽 구역에서 쓸모가 있을 것"** 조건 추가
(`KitchenEnv.IsItemUsefulFor`). 되돌아가는 방향은 무보상이라 +0.036으로 떨어졌다.

### 4-2. RNN이 장식이었다

관측에서 '조리 진행도'만 뺐지 '조리가 끝났는지'는 그대로 주고 있었다.
게다가 `Station.CanInteract`가 `HasCookedSoup`을 직접 봐서, 관측에서 뺐더라도
**Action Mask가 열리는 시점으로 완료 여부가 그대로 새어나갔다.**

→ 완성 플래그를 관측에서 제거(45→44차원)하고, 냄비가 차 있기만 하면 뜨는 시도를
허용하도록 마스크를 바꿨다. 덜 끓었으면 헛도리(-0.02)로 끝난다.

검증: 조리 중/완료 두 상태에서 Interact 마스크가 **동일하게 열림** = 누설 없음.

### 4-3. 역할이 극단적으로 치우쳐 있었다

최초 배치(A = 재료함 + 냄비, B = 그릇함 + 서빙구)에서 실측한 결과:

| | 수프 1개당 decision |
|---|---|
| Chef A | 46 |
| Chef B | 14 (유휴 **70%**) |

협동이 아니라 "한 명이 다 하고 한 명이 배달받는" 구조였다.

→ 재료함을 A 구역에서 B 구역(남쪽 벽)으로 이동.

| | 이전 | 이후 |
|---|---|---|
| Chef A | 46 | 34 |
| Chef B | 14 (유휴 70%) | 26 (유휴 24%) |
| 작업량 비 | 3.30 : 1 | **1.31 : 1** |
| 수프당 전달 | 2회 | **5회** |
| 수프 3개 소요 | 138 / 300 | 102 / 300 |

### 4-4. config가 학습을 시작조차 못 했다

`mlagents.trainers.cli_utils.load_config()`는 yaml을 **OS 기본 코드페이지**로 연다.
이 PC는 cp949라서 한글 주석이 든 UTF-8 yaml이 `TrainerConfigError`로 죽었다.

→ yaml을 **ASCII 전용**으로. (설계 근거는 이 README로 옮김)

### 4-5. Run In Background가 꺼져 있었다

에디터가 포커스를 잃으면 Play 모드 게임 루프가 멈춘다.
이 상태로 학습을 돌리면 alt-tab 하는 순간 학습이 멈춘다. → 활성화.

---

## 5. 실행

```bash
conda activate mlagents
cd week7-8/UnderCooked-Hyeongjun

# 학습 (먼저 실행 -> "Listening on port 5004" 뜨면 Unity 에디터에서 Play)
mlagents-learn configs/undercooked.yaml --run-id=undercooked_v1

# 이어서 학습
mlagents-learn configs/undercooked.yaml --run-id=undercooked_v1 --resume

# TensorBoard (별도 터미널)
tensorboard --logdir results
```

### Heuristic 플레이 (사람이 직접)

두 셰프의 Behavior Type을 `Heuristic Only`로 바꾸고 Play.

- **Chef A** (빨강, 북쪽): `WASD` 이동, `LeftShift` Interact
- **Chef B** (보라, 남쪽): `방향키` 이동, `RightShift` / `Enter` Interact

---

## 6. 결과

> 학습 후 작성.

- [ ] 학습 곡선 (TensorBoard)
- [ ] 커리큘럼 lesson 전환 시점
- [ ] **memory on/off ablation** — RNN이 실제로 기여하는지
- [ ] 최종 정책 데모 GIF

---

## 7. 파일 구조

```
UnderCooked-Hyeongjun/
├── README.md
├── UnityProject/Assets/
│   ├── Scenes/UnderCooked.unity        16개 TrainingArea (에이전트 32)
│   ├── Prefabs/TrainingArea.prefab     환경 1세트
│   └── Scripts/
│       ├── ItemType.cs                 enum: 손에 들 수 있는 것
│       ├── StationType.cs              enum: 스테이션 종류
│       ├── InteractResult.cs           enum: 상호작용 결과
│       ├── InteractOutcome.cs          struct: 결과 + 새 아이템 + 전달 상대
│       ├── Station.cs                  스테이션 상태와 상호작용 규칙
│       ├── KitchenEnv.cs               그리드/조리/타이머/리셋
│       ├── ChefAgent.cs                관측/행동/마스킹/개인보상/Heuristic
│       └── KitchenGroup.cs             SimpleMultiAgentGroup, 팀 보상, 종료
├── configs/undercooked.yaml            ASCII 전용 (§4-4)
├── models/undercooked.onnx
└── assets/demo.gif
```

## 8. 환경 버전

Unity 6000.3.19f1 · URP 17.3.0 · `com.unity.ml-agents` 4.0.3 ·
Python 3.10.12 · pip `mlagents` 1.1.0 · PyTorch 2.2.2+cpu (CPU 학습)
