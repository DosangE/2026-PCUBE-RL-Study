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
| 카메라 센서 | `CameraSensorComponent` 48×48 RGB, Stacks 1, PNG 압축 |
| 관측 카메라 | `Rogue/AgentEye` — 로컬 `(0, 2.6, 0.45)`, X축 28° 하향, FOV 100°, near 0.3 / far 15 |
| 벡터 관측 | 로컬 속도 x·z (2개) |
| 행동 | 연속 2개 (전진/후진, 좌/우 회전) |
| 보상 | 고기 +1 / 당근 −0.5 / 매 스텝 −1/MaxStep, MaxStep 5000 |
| 음식 색 | 고기 = 빨강(`FoodRed.mat`) / 당근 = 파랑(`FoodBlue.mat`) |
| 바닥·벽 | 초록 / 회색 |

> 색을 원색으로 갈라놓은 이유: Ray는 태그로 구분하지만 카메라는 픽셀 색이 전부다.
> 처음엔 고기(적갈색)·당근(주황)이 비슷해 CNN이 구분을 못 배웠다.

---

## 1. Python 학습 환경

데스크탑에 아직 없다면:

```bash
conda create -n mlagents python=3.10.12
conda activate mlagents
```

GPU(NVIDIA)가 있으면 CUDA 빌드로:

```bash
pip install torch~=2.2.1 --index-url https://download.pytorch.org/whl/cu121
```

없으면 그냥 `pip install torch~=2.2.1`.

그다음 ML-Agents:

```bash
pip install mlagents==1.1.0
```

검증:

```bash
mlagents-learn --help
```

> **참고**: torch가 GPU 빌드인지는 사실 큰 영향이 없다. 학습 시간의 **97%가 Unity 렌더링 대기**이고
> PyTorch 연산은 3%뿐이다 (노트북 측정치: env_step 1945초 / trainer_advance 65초).
> 진짜 병목은 **Unity가 카메라 9대를 렌더링하는 속도**이므로 그래픽카드가 중요하다.

---

## 2. Unity 프로젝트 열기

- Unity **6000.3.19f1** (또는 같은 6000.3.x)로 `week3/FoodCollector` 열기
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

`config/foodCollector.yaml`의 `max_steps`를 목표치로 올린다 (현재 100000).

```bash
conda activate mlagents
cd <repo루트>
PYTHONUTF8=1 mlagents-learn week3/FoodCollector/config/foodCollector.yaml --run-id=camera_run_03
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

카메라 학습 10만 스텝 구간 추이 (Mean Reward):

```
10k 0.833 | 20k 0.278 | 30k 0.500 | 40k 1.000 | 50k 1.389
60k 1.167 | 70k 1.167 | 80k 0.667 | 90k 1.794 | 100k 1.400
```

**10만 스텝은 카메라 학습에 한참 부족하다.** 중단 시점까지 보상이 계속 오르고 있었다.
참고로 다른 참가자는 261만 스텝 / 2.05시간으로 평균 보상 272.8을 얻었다
(단, 보상 절대값은 `MaxStep`·음식 개수·시간 패널티 설정에 따라 몇 배씩 달라지므로
다른 사람 수치와의 직접 비교는 주의. 의미 있는 비교는 **같은 환경에서 잰 우리 Ray vs Camera**다).

> `results/` 는 `.gitignore` 대상이라 저장소에 없다. 노트북의 학습 로그가 필요하면
> `results/camera_run_02/` 를 따로 복사해 올 것. `--resume` 하려면 `checkpoint.pt`가 필요하다.
> 데스크탑에서는 새 run-id로 처음부터 길게 돌리는 편이 낫다.

---

## 7. 이슈 제출물 체크리스트 (issue #2)

- [ ] TensorBoard 스크린샷 (`Environment/Cumulative Reward`)
- [ ] 추론 영상 (`.onnx` 물린 상태, 10MB 이하)
- [ ] 총 스텝 수 / 학습 시간 / 평균 보상 (가능하면 Ray 비교)
- [ ] 카메라 설정 + `configuration.yaml` 주요 하이퍼파라미터
- [ ] 회고
