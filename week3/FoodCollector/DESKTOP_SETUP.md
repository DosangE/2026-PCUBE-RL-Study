# FoodCollector (Camera 센서) — 데스크탑 이어서 학습하기

노트북(Intel Arc 내장 GPU)에서 카메라 센서 학습이 **49 스텝/초**로 느려, 외장 GPU 데스크탑으로 옮기기 위한 문서.
`git pull` 후 이 문서만 따라가면 바로 학습을 돌릴 수 있다.

---

## 0. 현재 상태 요약

week3 과제(issue #2)는 Ray Perception Sensor를 떼고 **Camera Sensor(이미지 관측)만으로** 다시 학습시키는 것이다.
씬·프리팹·머티리얼·설정은 **모두 커밋되어 있으므로 추가 작업 없이 학습만 돌리면 된다.**

| 항목 | 값 |
|---|---|
| 씬 | `Assets/Scenes/MainScene.unity` |
| 학습 영역 | 9개 (TrainingArea × 9, 정책 공유) |
| Ray 센서 | 제거됨 |
| 카메라 센서 | `CameraSensorComponent` **32×32** RGB, Stacks 1, PNG 압축 |
| 관측 카메라 | `Rogue/AgentEye` — 로컬 `(0, 2.6, 0.45)`, X축 28° 하향, FOV 100°, near 0.3 / far 15 |
| 카메라 렌더 | 그림자·HDR·MSAA·depth/opaque 패스 **전부 OFF** (관측 품질 무관, 속도용) |
| 벡터 관측 | 로컬 속도 x·z (2개) |
| 행동 | 연속 2개 (전진/후진, 좌/우 회전) |
| 보상 | 고기 +1 / 당근 −0.5 / 매 스텝 −1f/MaxStep(= −0.0002), MaxStep 5000 |
| 음식 프리팹 | `Assets/Prefabs/Training/FoodGood.prefab` (`food` 태그) / `FoodBad.prefab` (`badFood` 태그) |
| 음식 모양·색 | 둘 다 지름 2 구(Unity 기본 Sphere). 좋은 음식 = 빨강(`FoodRed.mat`) / 나쁜 음식 = 파랑(`FoodBlue.mat`) |
| 바닥·벽 | 초록 / 회색 |

> 색을 원색으로 갈라놓은 이유: Ray는 태그로 구분하지만 카메라는 픽셀 색이 전부다.
> 처음엔 고기(적갈색)·당근(주황)이 비슷해 CNN이 구분을 못 배웠다.
>
> **원본 `Meat.prefab` / `Carrot.prefab`(모델링된 고기·당근 메쉬)은 건드리지 않는다.**
> 카메라 학습에는 `Assets/Prefabs/Training/` 아래 전용 프리팹만 쓴다. 원본 메쉬는 실루엣이 복잡하고
> 48×48로 줄이면 뭉개져서, 형태 변수를 없애고 **색 하나만으로 구분되도록** 구로 통일했다.
> 9개 `TrainingArea`의 `FoodArea` 컴포넌트가 이 두 프리팹을 참조한다.

---

## 1. Python 학습 환경

### 데스크탑(RTX 2080 SUPER) — 이미 구축 완료

| 항목 | 값 |
|---|---|
| conda | 26.5.3, `C:\Users\User\miniconda3` (사용자 범위 설치, **PATH 미등록**) |
| 환경 | `mlagents` — Python **3.10.12**, **conda-forge** 채널 |
| torch | **2.2.2+cu121**, `torch.cuda.is_available() == True` (RTX 2080 SUPER 인식) |
| mlagents | **1.2.0.dev0** (release_23 소스 설치, Unity 패키지 4.0.3과 정합) |
| onnx / numpy / protobuf | 1.15.0 / 1.23.5 / 3.20.3 |
| ml-agents 소스 | `C:\Users\User\ml-agents` (release_23, `--depth 1`) |

conda가 PATH에 없으므로 시작 메뉴의 **Anaconda Prompt**를 쓰거나, PowerShell이면 전체 경로로:

```bash
C:\Users\User\miniconda3\Scripts\activate mlagents
```

### 새 PC에서 처음부터 구축할 때

```bash
conda create -n mlagents python=3.10.12 --override-channels -c conda-forge
conda activate mlagents
pip install torch~=2.2.1 --index-url https://download.pytorch.org/whl/cu121   # GPU 없으면 --index-url 없이
git clone --branch release_23 --depth 1 https://github.com/Unity-Technologies/ml-agents.git
pip install ./ml-agents/ml-agents-envs
pip install ./ml-agents/ml-agents
pip install "setuptools<81"
mlagents-learn --help    # 검증
```

> **왜 `-c conda-forge`인가**: 요즘 conda는 Anaconda 기본 채널(`defaults`)에 대해 **ToS 동의를 강제**한다
> (`CondaToSNonInteractiveError`). Anaconda 약관은 200명 이상 조직의 상업적 사용에 유료 라이선스를 요구하므로,
> 동의 자체를 피하려면 conda-forge로 `--override-channels` 하는 편이 깔끔하다.
>
> **왜 `setuptools<81`인가**: conda-forge가 넣어주는 setuptools 84에는 `pkg_resources`가 **제거**돼 있다.
> `mlagents/torch_utils/torch.py`가 이걸 import 하므로 `mlagents-learn`이 즉시 죽는다.
> 80.x로 내리면 deprecation 경고만 뜨고 정상 동작한다.
>
> **`python_requires`가 `>=3.10.1,<=3.10.12`로 못박혀 있다.** 3.10.13 이상도 거부되니 3.10.12 정확히 맞출 것.

> **참고**: torch가 GPU 빌드인지는 사실 큰 영향이 없다. 학습 시간의 **97%가 Unity 렌더링 대기**이고
> PyTorch 연산은 3%뿐이다 (노트북 측정치: env_step 1945초 / trainer_advance 65초).
> 진짜 병목은 **Unity가 카메라 9대를 렌더링하는 속도**이므로 그래픽카드가 중요하다.
>
> 같은 이유로 **학습 중 GPU를 쓰는 다른 프로그램(게임 등)을 반드시 종료할 것.** VRAM 8GB를
> 게임이 6.5GB 점유한 상태로 돌리면 데스크탑으로 옮긴 의미가 사라진다.

---

## 2. Unity 프로젝트 열기

- Unity **6000.3.x**로 `week3/FoodCollector` 열기 (노트북은 `6000.3.19f1`, 데스크탑은 `6000.3.18f1`.
  패치 버전이 다르면 `ProjectVersion.txt`가 여는 쪽 버전으로 덮여 쓰이는데, 동작엔 문제없다)
- `com.unity.ml-agents 4.0.3` 은 `Packages/manifest.json`에 있으므로 자동 복원됨
- 첫 실행은 `Library/` 재생성으로 몇 분 걸린다
- `Assets/Scenes/MainScene.unity` 열기

**Behavior Parameters** (`TrainingArea/Rogue` 9개 전부) — **그대로 두면 된다.**
- `Behavior Type` = `Default`, `Model` = `FoodCollector_Camera_100k.onnx` (노트북 100k 결과)
- `Default`는 트레이너가 붙으면 트레이너를 쓰고, 없으면 물려 있는 `.onnx`로 추론한다.
  즉 **학습도 추론 확인도 설정 변경 없이 바로 된다.**
- 처음부터 새로 학습하는데 이전 모델을 아예 떼고 싶으면 Model 슬롯을 비워도 되지만, 필수는 아니다
  (트레이너가 연결되면 모델은 무시된다).

---

## 3. 학습 실행

`config/foodCollector.yaml`의 `max_steps`를 목표치로 올린다 (현재 500000).

```bash
C:\Users\User\miniconda3\Scripts\activate mlagents
cd D:\PCUBE\2026-PCUBE-RL-Study
PYTHONUTF8=1 mlagents-learn week3/FoodCollector/config/foodCollector.yaml --run-id=camera_run_04
```

`Listening on port 5004` 가 뜨면 **Unity에서 Play**.

> **`PYTHONUTF8=1` 이 필요한 이유**: yaml에 한글 주석이 있는데, Windows 기본 코덱(cp949)으로 읽다가
> `UnicodeDecodeError`로 죽는다. PowerShell이면 `$env:PYTHONUTF8=1` 먼저 실행.

모니터링:

```bash
tensorboard --logdir results
```

핵심 지표는 `Environment/Cumulative Reward`.

---

## 4. 속도 최적화 (데스크탑에서 먼저 해볼 것)

48×48 이미지 한 장에 붙는 **고정 렌더 비용**이 그림 자체보다 크다. 아래는 관측 품질에 영향 없이 줄일 수 있는 것들.

`Rogue/AgentEye` 9개의 Camera 컴포넌트에서:

| 설정 | 현재 | 권장 | 이유 |
|---|---|---|---|
| Rendering > Render Shadows | ON | **OFF** | 카메라 9대가 각자 그림자 맵을 그린다. 색 구분에 불필요 |
| Rendering > Depth Texture | Use Pipeline | **OFF** | 추가 패스 |
| Rendering > Opaque Texture | Use Pipeline | **OFF** | 추가 패스 |
| Output > HDR | ON | **OFF** | 원색 3개 구분에 고명암 정밀도 불필요 |
| Output > MSAA | ON | **OFF** | 48픽셀에서 안티앨리어싱은 무의미 |

추가로:

- **해상도 48→32 또는 24**: 픽셀 수가 2.25~4배 감소. 단 `vis_encode_type: simple`의 최소는 20×20.
  낮추면 원거리 음식이 뭉개지므로, 낮춘 뒤 게임 뷰를 1:1 비율로 놓고 사람 눈으로 확인할 것.
- **빌드해서 학습**: 에디터 오버헤드를 제거한다. 통상 2~5배 빠르다.
  `File > Build Settings`에서 MainScene 포함해 빌드한 뒤:

  ```bash
  PYTHONUTF8=1 mlagents-learn week3/FoodCollector/config/foodCollector.yaml --run-id=camera_run_03 --env=<빌드경로>/FoodCollector.exe --num-envs=1
  ```

> GPU를 바꿔도 완전히는 안 줄어드는 부분이 있다. `CameraSensor`는 렌더 후 `ReadPixels`로
> GPU→CPU 동기화를 걸어 파이프라인을 멈춘다. 이건 GPU 성능보다 왕복 지연에 좌우된다.

---

## 5. 알려진 함정

**학습 중단 시 자식 프로세스까지 정리할 것.**

```bash
taskkill /F /IM mlagents-learn.exe /T
```

`/T` 없이 죽이면 multiprocessing 자식이 살아남아 **포트 5004를 계속 점유**한다.
그 상태에서 Play를 누르면 Unity가 응답 없는 좀비에게 연결을 걸고 **핸드셰이크를 기다리며 멈춘다**
(증상: CPU 0%, 창 무응답). 노트북에서 이걸로 두 번 멈췄다.

포트 확인:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5004
```

**에디터가 비포커스면 Play가 멈출 수 있다.** 추론 결과를 눈으로 확인할 때는 Unity 창을 앞에 둘 것.

---

## 6. 노트북에서의 기준선 (비교용)

| | Ray (food_run_01) | Camera (camera_run_02) |
|---|---|---|
| 총 스텝 | 300,456 | 100,152 |
| 최종 보상 | 47.39 | 1.44 |
| 처리 속도 | 약 326 스텝/초 | 약 49 스텝/초 |
| 학습 시간 | 5.1분(마지막 구간만 기록) | 33.9분 |

### 데스크탑 첫 실행 — `camera_run_04` (RTX 2080 SUPER, 전용 프리팹)

같은 10만 스텝을 데스크탑에서, **새 학습 전용 구 프리팹**으로 다시 돌린 결과:

| | 노트북 `camera_run_02` | 데스크탑 `camera_run_04` |
|---|---|---|
| 총 스텝 | 100,152 | 100,152 |
| **최종 보상** | **1.44** | **4.85** |
| 처리 속도 | 약 49 스텝/초 | **약 290 스텝/초** |
| 학습 시간 | 33.9분 | **6.2분** |
| 음식 | 원본 고기·당근 메쉬 | 균일한 구, 빨강/파랑 |

10만 스텝 구간 추이 (Mean Reward) — 위가 노트북, 아래가 데스크탑:

```
10k 0.833 | 20k 0.278 | 30k 0.500 | 40k 1.000 | 50k 1.389
60k 1.167 | 70k 1.167 | 80k 0.667 | 90k 1.794 | 100k 1.400

10k 1.278 | 20k 1.000 | 30k 0.278 | 40k 1.500 | 50k 1.833
60k 2.444 | 70k 2.444 | 80k 2.833 | 90k 4.235 | 100k 4.850
```

**같은 스텝 수에서 보상이 3.4배**다. 노트북 곡선은 10만 내내 1 근처를 맴돌았지만,
데스크탑 곡선은 5만 이후 뚜렷한 우상향으로 꺾인다. 하드웨어는 속도만 바꾸지 보상은 못 바꾸므로,
차이를 만든 건 **관측 대상을 형태 변수 없는 단색 구로 통일한 것**으로 보인다.
다만 런이 각 1회뿐이라 시드 운이 섞여 있을 수 있어, 단정하려면 반복 측정이 필요하다.

속도 병목은 하드웨어를 바꿔도 구조가 같다 (`camera_run_04` timers):
`env_step` 326.7초 / `trainer_advance` 30.4초 — 여전히 **87%가 Unity 렌더링 대기**다.
GPU 사용률은 학습 중 16% 언저리였다.

**10만 스텝은 카메라 학습에 여전히 부족하다.** 보상이 끝까지 오르는 중이었다.
참고로 다른 참가자는 261만 스텝 / 2.05시간으로 평균 보상 272.8을 얻었다
(단, 보상 절대값은 `MaxStep`·음식 개수·시간 패널티 설정에 따라 몇 배씩 달라지므로
다른 사람 수치와의 직접 비교는 주의. 의미 있는 비교는 **같은 환경에서 잰 우리 Ray vs Camera**다).

### `camera_run_05` — 버그 수정 + 최적화 후 50만 스텝

| | Ray `food_run_01` | Camera `camera_run_04` | **Camera `camera_run_05`** |
|---|---|---|---|
| 총 스텝 | 300,456 | 100,152 | **500,184** |
| 최종 보상 | 47.39 | 4.85 | **46.06** (470k에서 최고 58.50) |
| 처리 속도 | 약 326 스텝/초 | 290 스텝/초 | **384 스텝/초** |
| 학습 시간 | — | 6.2분 | **21.7분** |
| 시간 패널티 | 미작동(버그) | 미작동(버그) | **작동** |
| 해상도 | — | 48×48 | **32×32** |

**카메라 관측만으로 Ray 센서 수준(47.39)에 도달했다.** 이게 이번 과제의 핵심 결과다.

보상 추이:

```
 10k −0.78 |  50k  0.89 | 100k  3.10 | 150k 10.83 | 200k 21.50
250k 20.89 | 300k 35.28 | 350k 39.56 | 400k 47.72 | 450k 43.00
                                      | 470k 58.50 | 500k 46.06
```

> **`camera_run_04`의 4.85와 직접 비교하면 안 된다.** run_05는 시간 패널티가 실제로 작동해
> 에피소드당 최대 −1.0이 깔려 있고 해상도도 다르다. 비교 가능한 건 Ray(47.39) ↔ run_05(46.06)뿐이며,
> 이쪽도 Ray 런은 패널티가 죽어 있던 조건이라 엄밀히는 −1.0만큼 run_05에 불리하다.

바꾼 것 세 가지:

1. **시간 패널티 정수 나눗셈 버그 수정** — `PlayerAgent.cs`의 `AddReward(-1 / MaxStep)`은
   `MaxStep`이 `int`(5000)이라 정수 나눗셈으로 **항상 0**이었다. `-1f / MaxStep`으로 고쳤다.
   `FoodCollector.md` 6장이 −0.0002를 의도로 명시하고 있으므로 설계 대비 버그다.
   Ray 런과 `camera_run_02`/`04` 전부 패널티가 죽은 상태로 측정된 값이다.
2. **카메라 렌더 옵션 끄기** — AgentEye 9대의 그림자·HDR·MSAA·depth/opaque 패스 전부 OFF.
3. **해상도 48→32** — 4개 에이전트 시점을 48/32/24/20으로 다운샘플해 눈으로 비교한 결과,
   원거리에서 빨강·파랑 음식이 **나란히 붙어 있을 때** 24에서 뭉치고 20에서 한 덩어리가 됐다.
   색 구분은 20에서도 멀쩡하다 — 한계는 색이 아니라 공간 해상도다. 32가 안전선.

2+3으로 처리 속도가 290 → 384 스텝/초(+33%)가 됐다. 다만 병목 구조는 그대로다:
`env_step` 1156.2초(**89%**) / `trainer_advance` 129.2초(10%), GPU 사용률 17%.
더 짜내려면 **빌드해서 학습**(4장)이 다음 수단이다.

### 이어서 학습하기

`camera_run_05`는 `results/camera_run_05/FoodCollector/checkpoint.pt`를 남겼다.
`config/foodCollector.yaml`의 `max_steps`를 목표치로 올린 뒤 **같은 run-id에 `--resume`**:

```bash
mlagents-learn week3/FoodCollector/config/foodCollector.yaml --run-id=camera_run_05 --resume
```

`max_steps`를 안 올리면 "이미 도달했다"며 즉시 끝나니 반드시 먼저 올릴 것.
중간 체크포인트는 349,936 / 399,968 / 449,960 / 499,992 스텝 것이 보관돼 있다
(`keep_checkpoints: 5` 기본값이라 오래된 건 밀려난다).

> **센서 설정을 바꾸면 `--resume`이 불가능하다.** 해상도를 바꾸면 관측 shape이 달라져
> 기존 체크포인트와 맞지 않는다. 그때는 새 run-id로 처음부터 돌려야 한다.

> `results/` 는 `.gitignore` 대상이라 저장소에 없다. 다른 PC로 옮기려면 `results/camera_run_04/` 를
> 통째로 복사해야 `--resume` 이 된다.

---

## 7. 이슈 제출물 체크리스트 (issue #2)

- [ ] TensorBoard 스크린샷 (`Environment/Cumulative Reward`)
- [ ] 추론 영상 (`.onnx` 물린 상태, 10MB 이하)
- [ ] 총 스텝 수 / 학습 시간 / 평균 보상 (가능하면 Ray 비교)
- [ ] 카메라 설정 + `configuration.yaml` 주요 하이퍼파라미터
- [ ] 회고
