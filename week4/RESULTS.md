# Week 4 결과 정리 — SoccerBots

## 인퍼런스

학습된 역할별 정책을 적용해 3v3 축구 환경에서 인퍼런스를 실행한다.

- 스트라이커: `Striker.onnx`
- 골키퍼: `Goalie.onnx`
- 정책: MA-POCA + Self-Play
- 촬영 씬/절차: [INFERENCE_SETUP.md](INFERENCE_SETUP.md)

## TensorBoard

학습 로그가 있는 PC에서 아래 명령을 실행한 뒤 [TensorBoard 열기](http://localhost:6006/#scalars) 링크를 연다.

```powershell
tensorboard --logdir results
```

제출에는 다음 그래프를 권장한다.

- `Environment/Group Cumulative Reward`: 커리큘럼 진행 및 팀 보상 추세
- `Environment/Cumulative Reward` (Striker, Goalie): 역할별 shaping 효과 비교
- `Self-Play/ELO`: Self-Play 정책 풀의 경쟁력 변화
- `Policy/Entropy`: 탐험이 너무 일찍 사라지지 않았는지 확인

현재 저장소에는 재현 가능한 ONNX 인퍼런스 모델만 포함되어 있고 `results/` 학습 로그는 Git 정책상 제외된다. 따라서 실제 실행에서 나온 이벤트 파일을 `results/<run-id>/`에 둔 뒤 위 링크에서 그래프를 캡처한다.

## 학습 구성

| 항목 | 값 |
| --- | --- |
| 최대 스텝 | Striker 10,000,000 / Goalie 5,000,000 |
| 알고리즘 | MA-POCA + Self-Play |
| `epsilon` | 0.2 |
| `lambda` | 0.95 |
| `gamma` | 0.99 |
| 학습률 | 0.0003 |
| 병렬 실행 권장값 | 4~8 환경, `time-scale=20` |

## Reward shaping

**Striker**는 득·실점의 팀 보상에 더해, 공을 상대 골 방향으로 전진시킬 때의 그룹 보상(S1), 공 접촉 보너스(S2), 매 스텝 생존 패널티를 사용한다. 보너스는 에피소드 예산으로 제한해 접촉 보상만 반복해서 얻는 행동을 막는다.

**Goalie**는 득·실점 팀 보상에 더해, 공을 자기 골에서 멀리 걷어냈을 때의 보상(G1), 이상적인 수비 위치에서 벗어난 정도에 비례하는 패널티(G2), 작은 양의 생존 보상을 사용한다.

## 회고

보상 shaping과 커리큘럼은 희소한 득점 보상만으로 시작할 때보다 학습의 출발점을 훨씬 명확하게 만든다. 다음 개선으로는 패스 성공·수비 커버 같은 협동 행동을 별도의 단계에서 학습시키고, 그 정책을 3v3 Self-Play 커리큘럼에 연결해 볼 수 있다.
