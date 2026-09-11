# Issue #2 제출물 — Week 3 FoodCollector 카메라 에이전트

> 추론 영상은 별도 첨부한다. 이 문서는 영상을 제외한 제출 항목만 담는다.

## 1. TensorBoard 학습 곡선

![Environment/Cumulative Reward](../results/camera_run_07/tensorboard-cumulative-reward.png)

- TensorBoard 태그: `Environment/Cumulative Reward`
- 실행 ID: `camera_run_07`
- smoothing: `0.6`
- 최종 표시값: 원본 **147.3333**, smoothing 적용 **146.1782**
- 최고 평균 보상: **152.94** (2,830,000 step)

원본 이벤트 로그는 `results/camera_run_07/FoodCollector/events.out.tfevents.*`에 있다.
다음 명령으로 다시 확인할 수 있다.

```bash
tensorboard --logdir results/camera_run_07
```

## 2. 학습 결과

| 항목 | 값 |
|---|---:|
| 최종 체크포인트 | 3,000,456 steps |
| 마지막 TensorBoard 요약 step | 3,000,000 |
| 학습 시간 | 7,898.5초 (2시간 11분 38초) |
| 처리 속도 | 약 380 steps/s |
| 최종 평균 보상 | 147.33 |
| 최종 평균 보상 표준편차 | 3.63 |
| 병렬 학습 환경 | TrainingArea 9개 (공유 정책) |
| 에피소드 최대 길이 | 999 steps |

| Step | 평균 보상 |
|---:|---:|
| 100,000 | 7.90 |
| 500,000 | 118.06 |
| 1,000,000 | 126.85 |
| 1,500,000 | 139.00 |
| 2,000,000 | 145.44 |
| 2,500,000 | 147.56 |
| 2,830,000 | **152.94** |
| 3,000,000 | 147.33 |

## 3. 학습 환경과 카메라 설정

| 항목 | 설정 |
|---|---|
| 환경 | Unity FoodCollector, TrainingArea 9개 |
| 관측 | 32×32 RGB 카메라 |
| 시각 인코더 | `simple` |
| 실행 | Unity Editor, `time_scale: 20`, graphics 사용 |
| ML-Agents / PyTorch | 1.2.0.dev0 / 2.2.2+cu121 |

## 4. 하이퍼파라미터

| 구분 | 파라미터 | 값 |
|---|---|---:|
| 알고리즘 | trainer type | PPO |
| PPO | batch size / buffer size | 1,024 / 10,240 |
| PPO | learning rate | 5.0e-4 (linear) |
| PPO | beta | 8.0e-3 (linear) |
| PPO | epsilon | 0.2 (linear) |
| PPO | lambda / num epoch | 0.95 / 3 |
| 네트워크 | hidden units / layers | 128 / 2 |
| 네트워크 | observation normalization | false |
| 보상 | extrinsic gamma / strength | 0.99 / 1.0 |
| 수집 | time horizon | 64 |
| 학습 | max steps | 3,000,000 |
| 기록 | summary frequency | 10,000 |
| 저장 | checkpoint interval / 보존 개수 | 50,000 / 5 |

## 5. 회고

- 32×32 카메라 관측으로도 좋은 음식과 나쁜 음식을 구분하며, 보상은 초반에 빠르게 상승한 뒤 200만 step 이후 약 145~150 범위에서 안정화됐다.
- 9개 TrainingArea를 공유 정책으로 병렬 실행해 표본 수집 속도를 확보했다. 다만 Unity 환경 step과 카메라 이미지 처리 비중이 커서 전체 학습 시간은 약 2시간 12분이 걸렸다.
- 학습 전 시간 패널티의 정수 나눗셈 문제를 `-1f / MaxStep`으로 수정했다. 이전 Ray 실행 결과는 보상 함수와 학습 예산이 달라 이번 실행과 직접 비교하지 않았다.
- 다음 실험에서는 카메라 외 관측(거리·방향 등) 추가, 보상 설계, 실행 빌드 기반 학습을 각각 분리해 성능과 속도를 비교할 수 있다.

## 6. 산출물

- 최종 추론 모델: `results/camera_run_07/FoodCollector.onnx`
- 재개용 체크포인트: `results/camera_run_07/FoodCollector/checkpoint.pt`
- 실제 실행 설정: `results/camera_run_07/configuration.yaml`
- 시간·체크포인트 메타데이터: `results/camera_run_07/run_logs/`
