// Interact 한 번이 실제로 무슨 일을 했는지.
// ChefAgent가 이 값으로 개인 보상을, KitchenGroup이 팀 보상을 결정한다.
public enum InteractResult
{
    Nothing = 0,          // 아무 일도 없음 (Action Masking이 걸려 있으면 나오지 않아야 정상)
    PickedFromSource,     // 재료함 / 그릇함에서 집음
    PlacedInPot,          // 냄비에 재료 투입
    TookSoupFromPot,      // 빈 그릇으로 완성된 수프를 담음
    PotNotReady,          // 빈 그릇을 들고 떠봤지만 아직 조리 중이었음 (헛도리)
    PlacedOnCounter,      // 카운터에 올림 (전달 시도)
    TookFromCounter,      // 카운터에서 '동료가 놓은' 물건을 집음 -> 전달 성립
    TookOwnFromCounter,   // 카운터에서 '자기가 놓은' 물건을 도로 집음 -> 보상 없음
    Served,               // 서빙구에 완성 수프 제출 성공
    Wasted                // 서빙구에 엉뚱한 물건을 버림
}
