# Week 4 인퍼런스·촬영 설정

`Assets/Models/Striker.onnx`와 `Assets/Models/Goalie.onnx`가 각각 스트라이커와 골키퍼 프리팹에 연결되어 있다. 두 프리팹 모두 `Behavior Type = Inference Only`이므로 Python 학습 프로세스 없이 Unity에서 바로 정책 대 정책 3v3 경기를 재생할 수 있다.

## 촬영용 씬 만들기

1. Unity Hub에서 `week4/SoccerBots`를 연다. 권장 에디터는 Unity `6000.3.19f1`이다.
2. 상단 메뉴에서 **PCUBE → Week 4 → Create Inference 3v3 Scene**을 누른다.
3. 생성된 `Assets/Scenes/Inference3v3.unity`를 연다.
4. Play를 누른 뒤 Game 뷰를 녹화한다.

이 씬은 원본 `Soccer.unity`의 병렬 학습 필드 중 원점의 경기장 하나만 켜고, 메인 카메라를 전체 피치가 보이는 관전 구도로 맞춘다. 원본 학습 씬과 프리팹은 변경하지 않는다.

## 모델 연결 확인

| 역할 | Behavior Name | 연결 모델 | 실행 모드 |
| --- | --- | --- | --- |
| Striker | `Striker` | `Assets/Models/Striker.onnx` | Inference Only |
| Goalie | `Goalie` | `Assets/Models/Goalie.onnx` | Inference Only |

사람 조작 장면이 필요하면, 촬영할 선수 하나만 `Behavior Type`을 `Heuristic Only`로 바꾸면 된다. 나머지는 `Inference Only`로 유지한다.

> 화면 녹화는 Unity Recorder 또는 Windows Game Bar를 사용하면 된다. 제출용으로는 15~30초 정도의 골 장면이 적당하다.
